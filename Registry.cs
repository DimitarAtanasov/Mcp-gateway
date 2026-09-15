using McpGateway.Auth;
using McpGateway.Connectors;
using McpGateway.Connectors.OpenSearch;
using YamlDotNet.Serialization;

namespace McpGateway;

/// <summary>Loads registry.yaml, instantiates enabled connectors, and builds
/// the per-identity tool allowlist used for authorization.
///
/// Adding a new connector = one entry in ConnectorFactories + a class
/// under Connectors/ implementing Connector + one entry in
/// registry.yaml. Nothing else in this file changes.</summary>
public sealed class ToolRegistry
{
    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, object?>, ManagedIdentityTokenProvider, Connector>> ConnectorFactories = new()
    {
        ["opensearch"] = (config, tokenProvider) => new OpenSearchConnector(config, tokenProvider),
        // ["redis"] = (config, tokenProvider) => new RedisConnector(config, tokenProvider),
        // ["sql"] = (config, tokenProvider) => new SqlConnector(config, tokenProvider),
    };

    public Dictionary<string, Connector> Connectors { get; } = new();
    public Dictionary<string, ToolDefinition> Tools { get; } = new();

    // identity (e.g. AAD app id) -> set of allowed tool names
    private readonly Dictionary<string, HashSet<string>> _authz = new();

    public static ToolRegistry FromYaml(string path, ManagedIdentityTokenProvider tokenProvider)
    {
        var registry = new ToolRegistry();
        var deserializer = new DeserializerBuilder().Build();
        var raw = deserializer.Deserialize<Dictionary<object, object>>(File.ReadAllText(path));

        foreach (var entryObj in AsList(raw.GetValueOrDefault("connectors")))
        {
            var entry = (Dictionary<object, object>)entryObj;
            if (!AsBool(entry.GetValueOrDefault("enabled")))
                continue;

            var name = (string)entry["name"];
            if (!ConnectorFactories.TryGetValue(name, out var factory))
            {
                throw new InvalidOperationException(
                    $"Unknown connector '{name}' in registry.yaml. Known: {string.Join(", ", ConnectorFactories.Keys)}");
            }

            var config = ToStringKeyed(entry);
            var connector = factory(config, tokenProvider);
            registry.Connectors[name] = connector;

            var enabledToolNames = AsList(entry.GetValueOrDefault("tools")).Select(o => (string)o).ToHashSet();
            foreach (var tool in connector.Tools())
            {
                if (!enabledToolNames.Contains(tool.Name))
                    continue;
                if (!registry.Tools.TryAdd(tool.Name, tool))
                    throw new InvalidOperationException($"Duplicate tool name: {tool.Name}");
            }
        }

        foreach (var identityEntryObj in AsList(raw.GetValueOrDefault("authz")))
        {
            var identityEntry = (Dictionary<object, object>)identityEntryObj;
            var identity = (string)identityEntry["identity"];
            var allowedTools = AsList(identityEntry.GetValueOrDefault("allowed_tools")).Select(o => (string)o).ToHashSet();
            registry._authz[identity] = allowedTools;
        }

        return registry;
    }

    public async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        foreach (var connector in Connectors.Values)
            await connector.ConnectAsync(cancellationToken);
    }

    public async Task<Dictionary<string, bool>> HealthCheckAllAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, bool>();
        foreach (var (name, connector) in Connectors)
        {
            try
            {
                results[name] = await connector.HealthCheckAsync(cancellationToken);
            }
            catch
            {
                results[name] = false;
            }
        }
        return results;
    }

    public bool IsAllowed(string identity, string toolName) =>
        // Fail closed: unknown identity -> no tools.
        _authz.TryGetValue(identity, out var allowed) && allowed.Contains(toolName);

    private static List<object> AsList(object? value) => value switch
    {
        null => [],
        List<object> list => list,
        _ => throw new InvalidOperationException("Expected a YAML sequence."),
    };

    private static bool AsBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => bool.Parse(s),
        _ => throw new InvalidOperationException("Expected a YAML boolean."),
    };

    private static Dictionary<string, object?> ToStringKeyed(Dictionary<object, object> map) =>
        map.ToDictionary(kv => (string)kv.Key, kv => (object?)kv.Value);
}

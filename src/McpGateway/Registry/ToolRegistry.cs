using McpGateway.Auth;
using McpGateway.Connectors;

namespace McpGateway.Registry;

/// <summary>
/// The set of connectors the gateway serves, the tools they expose, and the per-identity tool
/// allowlist used for authorization.
///
/// Adding a connector is a registry entry plus a factory mapping — nothing in this class or in
/// the MCP wiring changes.
/// </summary>
public sealed class ToolRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, Connector> _connectors;
    private readonly Dictionary<string, ToolDefinition> _tools;
    private readonly Dictionary<string, HashSet<string>> _authz;

    private ToolRegistry(
        Dictionary<string, Connector> connectors,
        Dictionary<string, ToolDefinition> tools,
        Dictionary<string, HashSet<string>> authz)
    {
        _connectors = connectors;
        _tools = tools;
        _authz = authz;
    }

    /// <summary>Enabled connectors, keyed by name.</summary>
    public IReadOnlyDictionary<string, Connector> Connectors => _connectors;

    /// <summary>Tools exposed over MCP, keyed by tool name.</summary>
    public IReadOnlyDictionary<string, ToolDefinition> Tools => _tools;

    /// <summary>
    /// Builds a registry from a parsed document, instantiating every enabled connector and
    /// validating that the tools it names actually exist.
    /// </summary>
    /// <exception cref="RegistryValidationException">The document describes something unservable.</exception>
    public static ToolRegistry Create(RegistryDocument document, IConnectorFactory factory)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(factory);

        var connectors = new Dictionary<string, Connector>(StringComparer.OrdinalIgnoreCase);
        var tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        foreach (var entry in document.Connectors)
        {
            if (!entry.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(entry.Name))
                throw new RegistryValidationException("A connector entry is missing its 'name'.");

            if (!factory.SupportedNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new RegistryValidationException(
                    $"Unknown connector '{entry.Name}' in registry.yaml. Known connectors: {string.Join(", ", factory.SupportedNames)}.");
            }

            if (connectors.ContainsKey(entry.Name))
                throw new RegistryValidationException($"Connector '{entry.Name}' is listed more than once.");

            var connector = factory.Create(entry);
            connectors.Add(entry.Name, connector);

            var provided = connector.Tools().ToDictionary(tool => tool.Name, StringComparer.Ordinal);

            foreach (var toolName in entry.Tools)
            {
                if (!provided.TryGetValue(toolName, out var tool))
                {
                    throw new RegistryValidationException(
                        $"Connector '{entry.Name}' does not provide a tool named '{toolName}'. " +
                        $"It provides: {string.Join(", ", provided.Keys)}.");
                }

                if (!tools.TryAdd(toolName, tool))
                    throw new RegistryValidationException($"Duplicate tool name '{toolName}' across connectors.");
            }
        }

        var authz = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Authz)
        {
            if (string.IsNullOrWhiteSpace(entry.Identity))
                throw new RegistryValidationException("An authz entry is missing its 'identity'.");

            var allowed = new HashSet<string>(entry.AllowedTools, StringComparer.Ordinal);
            foreach (var toolName in allowed)
            {
                if (!tools.ContainsKey(toolName))
                {
                    throw new RegistryValidationException(
                        $"Identity '{entry.Identity}' is allowed tool '{toolName}', which no enabled connector exposes.");
                }
            }

            if (!authz.TryAdd(entry.Identity, allowed))
                throw new RegistryValidationException($"Identity '{entry.Identity}' is listed more than once in authz.");
        }

        return new ToolRegistry(connectors, tools, authz);
    }

    /// <summary>
    /// Whether <paramref name="identity"/> may invoke <paramref name="toolName"/>. Fail-closed:
    /// an unknown identity, or one with no matching authz entry, gets nothing.
    /// </summary>
    public bool IsAllowed(CallerIdentity identity, string toolName)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(toolName) || identity.IsUnknown)
            return false;

        foreach (var (configuredIdentity, allowedTools) in _authz)
        {
            if (identity.Candidates.Contains(configuredIdentity, StringComparer.OrdinalIgnoreCase) &&
                allowedTools.Contains(toolName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Connects every enabled connector. Throws if any fails, so startup fails loudly.</summary>
    public async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        foreach (var connector in _connectors.Values)
            await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Probes every connector, mapping a thrown probe to <c>false</c> rather than failing.</summary>
    public async Task<IReadOnlyDictionary<string, bool>> HealthCheckAllAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, connector) in _connectors)
        {
            try
            {
                results[name] = await connector.HealthCheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[name] = false;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var connector in _connectors.Values)
            await connector.DisposeAsync().ConfigureAwait(false);

        _connectors.Clear();
        _tools.Clear();
    }
}

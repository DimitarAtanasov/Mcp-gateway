using System.Text.Json;
using McpGateway.Connectors;
using McpGateway.Registry;

namespace McpGateway.Tests.Fakes;

/// <summary>A connector with scripted behaviour, used to exercise the registry in isolation.</summary>
internal sealed class FakeConnector : Connector
{
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public FakeConnector(string name, params string[] toolNames)
    {
        Name = name;
        _tools = [.. toolNames.Select(name => CreateTool(name))];
    }

    public override string Name { get; }

    public int ConnectCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool Healthy { get; set; } = true;

    public Exception? ThrowOnConnect { get; set; }

    public Exception? ThrowOnHealthCheck { get; set; }

    public override Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        return ThrowOnConnect is null ? Task.CompletedTask : Task.FromException(ThrowOnConnect);
    }

    public override Task<bool> HealthCheckAsync(CancellationToken cancellationToken) =>
        ThrowOnHealthCheck is null ? Task.FromResult(Healthy) : Task.FromException<bool>(ThrowOnHealthCheck);

    public override IReadOnlyList<ToolDefinition> Tools() => _tools;

    public override ValueTask DisposeAsync()
    {
        DisposeCount++;
        return base.DisposeAsync();
    }

    public static ToolDefinition CreateTool(
        string name,
        ToolHandler? handler = null,
        string inputSchemaJson = """{"type":"object","additionalProperties":true}""")
    {
        using var schema = JsonDocument.Parse(inputSchemaJson);
        return new ToolDefinition(
            name,
            $"Fake tool {name}.",
            schema.RootElement.Clone(),
            outputSchema: null,
            handler ?? ((_, _) => Task.FromResult<object>(new { ok = true })));
    }
}

/// <summary>Builds connectors from a fixed map, bypassing real backends.</summary>
internal sealed class FakeConnectorFactory : IConnectorFactory
{
    private readonly Dictionary<string, Connector> _connectors;

    public FakeConnectorFactory(params Connector[] connectors) =>
        _connectors = connectors.ToDictionary(connector => connector.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> SupportedNames => _connectors.Keys;

    public List<ConnectorEntry> CreatedFrom { get; } = [];

    public Connector Create(ConnectorEntry entry)
    {
        CreatedFrom.Add(entry);
        return _connectors[entry.Name];
    }
}

using System.Text.Json;

namespace McpGateway.Connectors;

/// <summary>Describes a single tool exposed by a connector.
///
/// `Handler` is the last line of defense before a call reaches the
/// backend and should never trust raw values blindly (e.g. never build
/// raw query strings from unsanitized input - always go through a
/// parameterized/allowlisted path).</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public required Func<IDictionary<string, JsonElement>, CancellationToken, Task<object>> Handler { get; init; }
}

/// <summary>Base class every backend connector (OpenSearch, Redis, SQL, ...) implements.
///
/// A connector owns exactly one backend's connection lifecycle and
/// exposes a list of ToolDefinitions. The gateway never talks to a
/// backend directly - it only ever calls into a connector's tools.</summary>
public abstract class Connector
{
    protected Connector(IReadOnlyDictionary<string, object?> config)
    {
        Config = config;
    }

    public abstract string Name { get; }

    protected IReadOnlyDictionary<string, object?> Config { get; }

    /// <summary>Establish (or lazily prepare) the pooled client connection.</summary>
    public abstract Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Cheap liveness check used by startup logging and readiness.</summary>
    public abstract Task<bool> HealthCheckAsync(CancellationToken cancellationToken);

    /// <summary>Returns the tools this connector exposes, already bound to itself.</summary>
    public abstract IReadOnlyList<ToolDefinition> Tools();

    /// <summary>Optional cleanup hook; override if the backend client needs it.</summary>
    public virtual Task CloseAsync() => Task.CompletedTask;
}

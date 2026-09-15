namespace McpGateway.Connectors;

/// <summary>
/// Base class every backend connector (OpenSearch, Redis, SQL, ...) implements.
///
/// A connector owns exactly one backend's connection lifecycle and exposes a list of
/// <see cref="ToolDefinition"/>s. The gateway never talks to a backend directly — it only ever
/// calls into a connector's tools, so credentials stay on this side of the wire.
/// </summary>
public abstract class Connector : IAsyncDisposable
{
    /// <summary>Connector name, matching the <c>name</c> key in the registry file.</summary>
    public abstract string Name { get; }

    /// <summary>Establishes the pooled client connection and any credential refresh loops.</summary>
    public abstract Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Cheap liveness probe used by startup logging and the readiness endpoint.</summary>
    public abstract Task<bool> HealthCheckAsync(CancellationToken cancellationToken);

    /// <summary>Returns the tools this connector exposes, already bound to itself.</summary>
    public abstract IReadOnlyList<ToolDefinition> Tools();

    /// <summary>Releases the backend client and stops background work.</summary>
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

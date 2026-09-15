using McpGateway.Diagnostics;
using McpGateway.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpGateway.Hosting;

/// <summary>
/// Connects every enabled connector before the gateway starts serving, and shuts them down
/// cleanly on stop so credential refresh loops and HTTP handlers do not outlive the process's
/// intent to exit.
///
/// A connector that cannot connect fails host startup: a gateway that cannot reach its backend
/// should not accept traffic and report itself healthy.
/// </summary>
public sealed class ConnectorLifecycleService : IHostedService
{
    private readonly ToolRegistry _registry;
    private readonly ILogger<ConnectorLifecycleService> _logger;

    /// <summary>Creates the lifecycle service.</summary>
    public ConnectorLifecycleService(ToolRegistry registry, ILogger<ConnectorLifecycleService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.ConnectingConnectors(
            _registry.Connectors.Count,
            _registry.Tools.Count,
            string.Join(", ", _registry.Tools.Keys));

        await _registry.ConnectAllAsync(cancellationToken).ConfigureAwait(false);

        var health = await _registry.HealthCheckAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (name, healthy) in health)
        {
            if (healthy)
                _logger.ConnectorHealthy(name);
            else
                _logger.ConnectorUnhealthy(name);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.ShuttingDownConnectors(_registry.Connectors.Count);
        await _registry.DisposeAsync().ConfigureAwait(false);
    }
}

using McpGateway.Registry;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace McpGateway.Hosting;

/// <summary>
/// Readiness probe: reports the gateway ready only while every connector's backend answers.
/// Container Apps and Kubernetes use this to keep traffic off an instance whose backend is
/// unreachable.
/// </summary>
public sealed class ConnectorsHealthCheck : IHealthCheck
{
    private readonly ToolRegistry _registry;

    /// <summary>Creates the health check.</summary>
    public ConnectorsHealthCheck(ToolRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = await _registry.HealthCheckAllAsync(cancellationToken).ConfigureAwait(false);
        var data = health.ToDictionary(entry => entry.Key, entry => (object)entry.Value, StringComparer.Ordinal);
        var unhealthy = health.Where(entry => !entry.Value).Select(entry => entry.Key).ToArray();

        if (unhealthy.Length == 0)
            return HealthCheckResult.Healthy("All connectors are reachable.", data);

        return new HealthCheckResult(
            context.Registration.FailureStatus,
            $"Unreachable connector(s): {string.Join(", ", unhealthy)}.",
            data: data);
    }
}

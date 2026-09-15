using McpGateway.Hosting;
using McpGateway.Registry;
using McpGateway.Tests.Fakes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpGateway.Tests;

public sealed class ConnectorLifecycleServiceTests
{
    [Fact]
    public async Task StartAsync_ConnectsEveryConnector()
    {
        var connector = new FakeConnector("search", "vector_search");
        var service = new ConnectorLifecycleService(
            BuildRegistry(connector),
            NullLogger<ConnectorLifecycleService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, connector.ConnectCount);
    }

    [Fact]
    public async Task StartAsync_ConnectorFailure_FailsStartup()
    {
        var connector = new FakeConnector("search", "vector_search")
        {
            ThrowOnConnect = new UnauthorizedAccessException("no managed identity"),
        };
        var service = new ConnectorLifecycleService(
            BuildRegistry(connector),
            NullLogger<ConnectorLifecycleService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_UnhealthyConnectorStillStarts()
    {
        // A backend that is briefly unreachable should not stop the gateway from booting;
        // the readiness probe keeps traffic away until it recovers.
        var connector = new FakeConnector("search", "vector_search") { Healthy = false };
        var service = new ConnectorLifecycleService(
            BuildRegistry(connector),
            NullLogger<ConnectorLifecycleService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, connector.ConnectCount);
    }

    [Fact]
    public async Task StopAsync_DisposesConnectorsSoRefreshLoopsStop()
    {
        var connector = new FakeConnector("search", "vector_search");
        var service = new ConnectorLifecycleService(
            BuildRegistry(connector),
            NullLogger<ConnectorLifecycleService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, connector.DisposeCount);
    }

    private static ToolRegistry BuildRegistry(FakeConnector connector) => ToolRegistry.Create(
        new RegistryDocument
        {
            Connectors =
            [
                new ConnectorEntry
                {
                    Name = connector.Name,
                    Enabled = true,
                    Endpoint = "https://backend.example",
                    Tools = [.. connector.Tools().Select(tool => tool.Name)],
                },
            ],
        },
        new FakeConnectorFactory(connector));
}

public sealed class ConnectorsHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_AllConnectorsReachable_IsHealthy()
    {
        var check = new ConnectorsHealthCheck(BuildRegistry(new FakeConnector("search", "vector_search")));

        var result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True((bool)result.Data["search"]);
    }

    [Fact]
    public async Task CheckHealthAsync_UnreachableConnector_UsesTheRegisteredFailureStatus()
    {
        var connector = new FakeConnector("search", "vector_search") { Healthy = false };
        var check = new ConnectorsHealthCheck(BuildRegistry(connector));

        var result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("search", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckHealthAsync_ThrownProbe_IsUnhealthyRatherThanAnException()
    {
        var connector = new FakeConnector("search", "vector_search")
        {
            ThrowOnHealthCheck = new HttpRequestException("connection refused"),
        };
        var check = new ConnectorsHealthCheck(BuildRegistry(connector));

        var result = await check.CheckHealthAsync(Context(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(
            "connectors",
            _ => throw new NotSupportedException(),
            HealthStatus.Unhealthy,
            ["ready"]),
    };

    private static ToolRegistry BuildRegistry(FakeConnector connector) => ToolRegistry.Create(
        new RegistryDocument
        {
            Connectors =
            [
                new ConnectorEntry
                {
                    Name = connector.Name,
                    Enabled = true,
                    Endpoint = "https://backend.example",
                    Tools = [.. connector.Tools().Select(tool => tool.Name)],
                },
            ],
        },
        new FakeConnectorFactory(connector));
}

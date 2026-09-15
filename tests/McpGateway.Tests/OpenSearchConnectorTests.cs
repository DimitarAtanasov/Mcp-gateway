using System.Text.Json;
using System.Text.Json.Nodes;
using McpGateway.Connectors;
using McpGateway.Connectors.OpenSearch;
using McpGateway.Registry;
using McpGateway.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpGateway.Tests;

public sealed class OpenSearchConnectorOptionsTests
{
    [Fact]
    public void FromEntry_ReadsEndpointAndAllowlist()
    {
        var options = OpenSearchConnectorOptions.FromEntry(new ConnectorEntry
        {
            Name = "opensearch",
            Endpoint = "https://search.example",
            AllowedIndices = ["a", "b"],
        });

        Assert.Equal(new Uri("https://search.example"), options.Endpoint);
        Assert.Equal(["a", "b"], options.AllowedIndices.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(OpenSearchConnectorOptions.DefaultAadScope, options.AadScope);
    }

    [Fact]
    public void FromEntry_MissingEndpoint_Throws()
    {
        var exception = Assert.Throws<RegistryValidationException>(
            () => OpenSearchConnectorOptions.FromEntry(new ConnectorEntry { Name = "opensearch" }));

        Assert.Contains("requires an 'endpoint'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEntry_RelativeEndpoint_Throws()
    {
        Assert.Throws<RegistryValidationException>(() => OpenSearchConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "opensearch", Endpoint = "search.example" }));
    }

    [Fact]
    public void FromEntry_PlaintextEndpoint_Throws()
    {
        // Bearer tokens must never travel in clear text.
        var exception = Assert.Throws<RegistryValidationException>(() => OpenSearchConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "opensearch", Endpoint = "http://search.example" }));

        Assert.Contains("must use https", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEntry_BlankIndicesAreDropped()
    {
        var options = OpenSearchConnectorOptions.FromEntry(new ConnectorEntry
        {
            Name = "opensearch",
            Endpoint = "https://search.example",
            AllowedIndices = ["a", "  ", ""],
        });

        Assert.Equal(["a"], options.AllowedIndices);
    }

    [Fact]
    public void FromEntry_HonoursAScopeOverride()
    {
        var options = OpenSearchConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "opensearch", Endpoint = "https://search.example" },
            "api://custom/.default");

        Assert.Equal("api://custom/.default", options.AadScope);
    }
}

public sealed class OpenSearchConnectorTests
{
    [Fact]
    public async Task SearchAsync_BeforeConnect_Throws()
    {
        await using var connector = Build(new RecordingConnection(), out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connector.SearchAsync("docs", new JsonObject(), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_FetchesATokenAndStampsEveryRequest()
    {
        var transport = new RecordingConnection("""{"hits":{"hits":[]}}""");
        await using var connector = Build(transport, out var tokenProvider);

        await connector.ConnectAsync(CancellationToken.None);
        await connector.SearchAsync("docs", new JsonObject { ["size"] = 5 }, CancellationToken.None);

        Assert.Equal(1, tokenProvider.CallCount);
        Assert.Equal("Bearer token-1", Assert.Single(transport.AuthorizationHeaders));
    }

    [Fact]
    public async Task ConnectAsync_IsIdempotent()
    {
        var transport = new RecordingConnection();
        await using var connector = Build(transport, out var tokenProvider);

        await connector.ConnectAsync(CancellationToken.None);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, tokenProvider.CallCount);
    }

    [Fact]
    public async Task ConnectAsync_PropagatesACredentialFailure()
    {
        var transport = new RecordingConnection();
        await using var connector = Build(transport, out var tokenProvider);
        tokenProvider.ThrowOnNextCall = new UnauthorizedAccessException("no managed identity");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => connector.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_SendsTheBodyToTheSearchEndpointAndReturnsTheResponse()
    {
        var transport = new RecordingConnection("""{"hits":{"hits":[{"_id":"doc-1"}]}}""");
        await using var connector = Build(transport, out _);
        await connector.ConnectAsync(CancellationToken.None);

        var response = await connector.SearchAsync(
            "product-docs-v1",
            new JsonObject { ["size"] = 5 },
            CancellationToken.None);

        var request = Assert.Single(transport.Requests);
        Assert.Contains("/product-docs-v1/_search", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("doc-1", response.GetProperty("hits").GetProperty("hits")[0].GetProperty("_id").GetString());
    }

    [Fact]
    public async Task SearchAsync_NonSuccessStatus_ThrowsWithoutLeakingTheBackendBody()
    {
        var transport = new RecordingConnection("""{"error":{"reason":"index_not_found_exception, cluster secret"}}""", 404);
        await using var connector = Build(transport, out _);
        await connector.ConnectAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConnectorException>(
            () => connector.SearchAsync("missing", new JsonObject(), CancellationToken.None));

        Assert.Contains("failed with status 404", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cluster secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthCheckAsync_BeforeConnect_IsFalse()
    {
        await using var connector = Build(new RecordingConnection(), out _);

        Assert.False(await connector.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthCheckAsync_SuccessfulProbe_IsTrue()
    {
        await using var connector = Build(new RecordingConnection("""{"version":{"number":"2.11.0"}}"""), out _);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.True(await connector.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthCheckAsync_FailingProbe_IsFalse()
    {
        await using var connector = Build(new RecordingConnection("""{"error":"nope"}""", 503), out _);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.False(await connector.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tools_ExposesVectorSearchBoundToThisConnector()
    {
        await using var connector = Build(new RecordingConnection(), out _);

        var tool = Assert.Single(connector.Tools());

        Assert.Equal("vector_search", tool.Name);
    }

    [Fact]
    public async Task DisposeAsync_StopsTheTokenRenewalLoop()
    {
        var transport = new RecordingConnection();
        var connector = Build(transport, out var tokenProvider);
        await connector.ConnectAsync(CancellationToken.None);

        await connector.DisposeAsync();

        // Disposing twice must stay safe, and no further tokens are fetched after shutdown.
        await connector.DisposeAsync();
        Assert.Equal(1, tokenProvider.CallCount);
    }

    private static OpenSearchConnector Build(
        OpenSearch.Net.IConnection transport,
        out FakeTokenProvider tokenProvider)
    {
        tokenProvider = new FakeTokenProvider();
        var options = OpenSearchConnectorOptions.FromEntry(new ConnectorEntry
        {
            Name = "opensearch",
            Endpoint = "https://search.example",
            AllowedIndices = ["product-docs-v1"],
        });

        return new OpenSearchConnector(options, tokenProvider, NullLoggerFactory.Instance, transport);
    }
}

using System.Security.Claims;
using System.Text.Json;
using McpGateway.Connectors;
using McpGateway.Registry;
using McpGateway.Server;
using McpGateway.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpGateway.Tests;

public sealed class ToolDispatcherTests
{
    private const string AllowedIdentity = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void ListTools_AdvertisesEveryRegisteredToolWithItsSchemas()
    {
        var dispatcher = Build(out _, FakeConnector.CreateTool("vector_search"));

        var result = dispatcher.ListTools();

        var tool = Assert.Single(result.Tools);
        Assert.Equal("vector_search", tool.Name);
        Assert.Equal("Fake tool vector_search.", tool.Description);
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
    }

    [Fact]
    public async Task CallToolAsync_MissingToolName_ThrowsProtocolError()
    {
        var dispatcher = Build(out _, FakeConnector.CreateTool("vector_search"));

        await Assert.ThrowsAsync<McpException>(
            () => dispatcher.CallToolAsync(new CallToolRequestParams { Name = string.Empty }, Caller(), default));
    }

    [Fact]
    public async Task CallToolAsync_NullRequest_ThrowsProtocolError()
    {
        var dispatcher = Build(out _, FakeConnector.CreateTool("vector_search"));

        await Assert.ThrowsAsync<McpException>(() => dispatcher.CallToolAsync(null, Caller(), default));
    }

    [Fact]
    public async Task CallToolAsync_UnknownTool_ThrowsProtocolError()
    {
        var dispatcher = Build(out _, FakeConnector.CreateTool("vector_search"));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => dispatcher.CallToolAsync(Request("nope"), Caller(), default));

        Assert.Contains("Unknown tool 'nope'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_UnauthorizedIdentity_ThrowsAndNeverRunsTheHandler()
    {
        var invoked = false;
        var tool = FakeConnector.CreateTool("vector_search", (_, _) =>
        {
            invoked = true;
            return Task.FromResult<object>(new { ok = true });
        });
        var dispatcher = Build(out _, tool);

        await Assert.ThrowsAsync<McpException>(
            () => dispatcher.CallToolAsync(Request("vector_search"), Caller("someone-else"), default));

        Assert.False(invoked);
    }

    [Fact]
    public async Task CallToolAsync_UnauthenticatedCaller_Throws()
    {
        var dispatcher = Build(out _, FakeConnector.CreateTool("vector_search"));

        await Assert.ThrowsAsync<McpException>(
            () => dispatcher.CallToolAsync(Request("vector_search"), user: null, default));
    }

    [Fact]
    public async Task CallToolAsync_AuthorizedCall_ReturnsStructuredContent()
    {
        var tool = FakeConnector.CreateTool(
            "vector_search",
            (_, _) => Task.FromResult<object>(new Dictionary<string, object> { ["results"] = new[] { "a", "b" } }));
        var dispatcher = Build(out _, tool);

        var result = await dispatcher.CallToolAsync(Request("vector_search"), Caller(), default);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(
            ["a", "b"],
            result.StructuredContent!.Value.GetProperty("results").EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("results", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_PassesArgumentsThroughToTheHandler()
    {
        IReadOnlyDictionary<string, JsonElement>? seen = null;
        var tool = FakeConnector.CreateTool("vector_search", (arguments, _) =>
        {
            seen = arguments;
            return Task.FromResult<object>(new { ok = true });
        });
        var dispatcher = Build(out _, tool);

        using var document = JsonDocument.Parse("""{"index":"docs"}""");
        var request = new CallToolRequestParams
        {
            Name = "vector_search",
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["index"] = document.RootElement.GetProperty("index").Clone(),
            },
        };

        await dispatcher.CallToolAsync(request, Caller(), default);

        Assert.NotNull(seen);
        Assert.Equal("docs", seen!["index"].GetString());
    }

    [Fact]
    public async Task CallToolAsync_NullArguments_HandlerSeesAnEmptyMap()
    {
        IReadOnlyDictionary<string, JsonElement>? seen = null;
        var tool = FakeConnector.CreateTool("vector_search", (arguments, _) =>
        {
            seen = arguments;
            return Task.FromResult<object>(new { ok = true });
        });
        var dispatcher = Build(out _, tool);

        await dispatcher.CallToolAsync(
            new CallToolRequestParams { Name = "vector_search", Arguments = null },
            Caller(),
            default);

        Assert.NotNull(seen);
        Assert.Empty(seen!);
    }

    [Fact]
    public async Task CallToolAsync_InvalidArguments_ReturnsAnErrorResultWithoutRunningTheHandler()
    {
        var invoked = false;
        var tool = FakeConnector.CreateTool(
            "vector_search",
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult<object>(new { ok = true });
            },
            inputSchemaJson: """{"type":"object","required":["index"]}""");
        var dispatcher = Build(out _, tool);

        var result = await dispatcher.CallToolAsync(Request("vector_search"), Caller(), default);

        Assert.True(result.IsError);
        Assert.False(invoked);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("Invalid arguments", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_ConnectorFailure_ReturnsTheMessageToTheModel()
    {
        var tool = FakeConnector.CreateTool(
            "vector_search",
            (_, _) => throw new ConnectorException("The OpenSearch query against index 'docs' failed with status 503."));
        var dispatcher = Build(out _, tool);

        var result = await dispatcher.CallToolAsync(Request("vector_search"), Caller(), default);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("status 503", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_ArgumentFailureFromAHandler_ReturnsTheMessageToTheModel()
    {
        var tool = FakeConnector.CreateTool(
            "vector_search",
            (_, _) => throw new ArgumentException("Index 'secrets' is not in this connector's allowed_indices list."));
        var dispatcher = Build(out _, tool);

        var result = await dispatcher.CallToolAsync(Request("vector_search"), Caller(), default);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("allowed_indices", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_UnexpectedFailure_HidesInternalsBehindAnErrorId()
    {
        var tool = FakeConnector.CreateTool(
            "vector_search",
            (_, _) => throw new InvalidOperationException("connection string is Server=secret;Password=hunter2"));
        var dispatcher = Build(out _, tool);

        var result = await dispatcher.CallToolAsync(Request("vector_search"), Caller(), default);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.DoesNotContain("hunter2", content.Text, StringComparison.Ordinal);
        Assert.Contains("Server error id", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_Cancellation_PropagatesRatherThanBecomingAToolError()
    {
        var tool = FakeConnector.CreateTool(
            "vector_search",
            (_, cancellationToken) => Task.FromCanceled<object>(cancellationToken));
        var dispatcher = Build(out _, tool);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.CallToolAsync(Request("vector_search"), Caller(), cancellation.Token));
    }

    private static CallToolRequestParams Request(string toolName) => new() { Name = toolName };

    private static ClaimsPrincipal Caller(string appId = AllowedIdentity) =>
        new(new ClaimsIdentity([new Claim("appid", appId)], authenticationType: "Bearer"));

    private static ToolDispatcher Build(out ToolRegistry registry, params ToolDefinition[] tools)
    {
        var connector = new FakeConnectorWithTools("search", tools);
        var document = new RegistryDocument
        {
            Connectors =
            [
                new ConnectorEntry
                {
                    Name = "search",
                    Enabled = true,
                    Endpoint = "https://backend.example",
                    Tools = [.. tools.Select(tool => tool.Name)],
                },
            ],
            Authz =
            [
                new AuthzEntry
                {
                    Identity = AllowedIdentity,
                    AllowedTools = [.. tools.Select(tool => tool.Name)],
                },
            ],
        };

        registry = ToolRegistry.Create(document, new FakeConnectorFactory(connector));
        return new ToolDispatcher(registry, new ToolArgumentValidator(), NullLogger<ToolDispatcher>.Instance);
    }

    private sealed class FakeConnectorWithTools : Connector
    {
        private readonly IReadOnlyList<ToolDefinition> _tools;

        public FakeConnectorWithTools(string name, IReadOnlyList<ToolDefinition> tools)
        {
            Name = name;
            _tools = tools;
        }

        public override string Name { get; }

        public override Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task<bool> HealthCheckAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public override IReadOnlyList<ToolDefinition> Tools() => _tools;
    }
}

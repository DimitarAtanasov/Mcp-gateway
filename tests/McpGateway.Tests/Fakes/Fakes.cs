using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using McpGateway.Auth;
using McpGateway.Connectors;
using McpGateway.Connectors.OpenSearch;
using McpGateway.Registry;
using OpenSearch.Net;

namespace McpGateway.Tests.Fakes;

/// <summary>Hands out canned tokens and counts how often it was asked.</summary>
internal sealed class FakeTokenProvider : IAccessTokenProvider
{
    private readonly Func<int, string> _tokenFactory;

    public FakeTokenProvider(Func<int, string>? tokenFactory = null) =>
        _tokenFactory = tokenFactory ?? (call => $"token-{call}");

    public int CallCount { get; private set; }

    public Exception? ThrowOnNextCall { get; set; }

    public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        CallCount++;

        if (ThrowOnNextCall is { } failure)
        {
            ThrowOnNextCall = null;
            throw failure;
        }

        return ValueTask.FromResult(_tokenFactory(CallCount));
    }
}

/// <summary>A credential returning a fixed token with a controllable expiry.</summary>
internal sealed class FakeTokenCredential : TokenCredential
{
    private readonly Func<int, AccessToken> _tokenFactory;

    public FakeTokenCredential(Func<int, AccessToken> tokenFactory) => _tokenFactory = tokenFactory;

    public int CallCount { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        RequestedScopes.Add(string.Join(" ", requestContext.Scopes));
        return _tokenFactory(++CallCount);
    }

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));

    public List<string> RequestedScopes { get; } = [];
}

/// <summary>Captures the requests an OpenSearch client makes and replays a canned response.</summary>
internal sealed class RecordingConnection : InMemoryConnection
{
    public RecordingConnection(string responseBody = "{}", int statusCode = 200)
        : base(Encoding.UTF8.GetBytes(responseBody), statusCode, exception: null, contentType: "application/json")
    {
    }

    public List<RequestData> Requests { get; } = [];

    public List<string> AuthorizationHeaders { get; } = [];

    public override TResponse Request<TResponse>(RequestData requestData)
    {
        Record(requestData);
        return base.Request<TResponse>(requestData);
    }

    public override Task<TResponse> RequestAsync<TResponse>(
        RequestData requestData,
        CancellationToken cancellationToken)
    {
        Record(requestData);
        return base.RequestAsync<TResponse>(requestData, cancellationToken);
    }

    private void Record(RequestData requestData)
    {
        Requests.Add(requestData);
        AuthorizationHeaders.Add(requestData.Headers["Authorization"] ?? string.Empty);
    }
}

/// <summary>An OpenSearch backend that records the query body and replays a canned response.</summary>
internal sealed class FakeOpenSearchBackend : IOpenSearchBackend
{
    private readonly string _responseJson;

    public FakeOpenSearchBackend(string? responseJson = null, IReadOnlySet<string>? allowedIndices = null)
    {
        _responseJson = responseJson ?? """{"hits":{"hits":[]}}""";
        AllowedIndices = allowedIndices ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> AllowedIndices { get; }

    public List<(string Index, JsonObject Body)> Searches { get; } = [];

    public Exception? ThrowOnSearch { get; set; }

    public Task<JsonElement> SearchAsync(string index, JsonObject body, CancellationToken cancellationToken)
    {
        Searches.Add((index, body));

        if (ThrowOnSearch is { } failure)
            throw failure;

        using var document = JsonDocument.Parse(_responseJson);
        return Task.FromResult(document.RootElement.Clone());
    }
}

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

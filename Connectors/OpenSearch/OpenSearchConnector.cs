using System.Text.Json;
using McpGateway.Auth;
using McpGateway.Connectors.OpenSearch.Tools;
using OpenSearch.Client;
using OpenSearch.Net;

namespace McpGateway.Connectors.OpenSearch;

/// <summary>OpenSearch connector: owns the OpenSearch client lifecycle and
/// exposes OpenSearch-specific tools (v1: vector_search only).</summary>
public sealed class OpenSearchConnector : Connector
{
    // AAD scope for the OpenSearch endpoint. If fronting a self-hosted
    // OpenSearch cluster with AAD auth (e.g. via APIM or a sidecar proxy),
    // point this at that resource's exposed scope instead.
    private const string OpenSearchAadScope = "https://opensearch.azure.com/.default";

    private readonly ManagedIdentityTokenProvider _tokenProvider;
    private OpenSearchClient? _client;
    private AadAuthConnection? _connection;

    public OpenSearchConnector(IReadOnlyDictionary<string, object?> config, ManagedIdentityTokenProvider tokenProvider)
        : base(config)
    {
        _tokenProvider = tokenProvider;
        Endpoint = (string)config["endpoint"]!;
        AllowedIndices = config.TryGetValue("allowed_indices", out var raw) && raw is List<object> list
            ? list.Select(o => (string)o).ToHashSet()
            : new HashSet<string>();
    }

    public override string Name => "opensearch";

    public string Endpoint { get; }

    public IReadOnlySet<string> AllowedIndices { get; }

    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            return;

        _connection = new AadAuthConnection(_tokenProvider, OpenSearchAadScope);
        await _connection.WarmUpAsync(cancellationToken);

        var settings = new ConnectionSettings(new Uri(Endpoint), _connection);
        _client = new OpenSearchClient(settings);
    }

    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
            return false;
        try
        {
            var response = await _client.LowLevel.RootNodeInfoAsync<StringResponse>(ctx: cancellationToken);
            return response.Success;
        }
        catch
        {
            return false;
        }
    }

    public override async Task CloseAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    public override IReadOnlyList<ToolDefinition> Tools() => [VectorSearchTool.Build(this)];

    internal async Task<JsonElement> SearchRawAsync(string index, object body, CancellationToken cancellationToken)
    {
        if (_client is null)
            throw new InvalidOperationException("OpenSearchConnector used before ConnectAsync().");

        var response = await _client.LowLevel.SearchAsync<StringResponse>(index, PostData.Serializable(body), ctx: cancellationToken);
        if (!response.Success)
            throw new InvalidOperationException($"OpenSearch search failed: {response.DebugInformation}");

        return JsonDocument.Parse(response.Body).RootElement;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using McpGateway.Auth;
using McpGateway.Connectors.OpenSearch.Tools;
using McpGateway.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;

namespace McpGateway.Connectors.OpenSearch;

/// <summary>
/// Owns the OpenSearch client lifecycle and exposes OpenSearch-specific tools
/// (v1: <c>vector_search</c> only).
/// </summary>
public sealed class OpenSearchConnector : Connector, IOpenSearchBackend
{
    private readonly OpenSearchConnectorOptions _options;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenSearchConnector> _logger;
    private readonly IConnection? _innerConnection;
    private readonly TimeSpan? _refreshInterval;
    private readonly TimeProvider? _timeProvider;

    private AadAuthConnection? _authConnection;
    private IOpenSearchClient? _client;

    /// <summary>Creates the connector.</summary>
    /// <param name="options">Validated endpoint and index allowlist.</param>
    /// <param name="tokenProvider">Source of AAD tokens for the cluster.</param>
    /// <param name="loggerFactory">Logger factory for this connector and its auth connection.</param>
    /// <param name="innerConnection">Transport to delegate to; a real HTTP connection when omitted.</param>
    /// <param name="refreshInterval">Token renewal cadence; the default when omitted.</param>
    /// <param name="timeProvider">Clock driving token renewal.</param>
    public OpenSearchConnector(
        OpenSearchConnectorOptions options,
        IAccessTokenProvider tokenProvider,
        ILoggerFactory loggerFactory,
        IConnection? innerConnection = null,
        TimeSpan? refreshInterval = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<OpenSearchConnector>();
        _innerConnection = innerConnection;
        _refreshInterval = refreshInterval;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public override string Name => "opensearch";

    /// <inheritdoc />
    public IReadOnlySet<string> AllowedIndices => _options.AllowedIndices;

    /// <inheritdoc />
    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            return;

        var authConnection = new AadAuthConnection(
            _tokenProvider,
            _options.AadScope,
            _loggerFactory.CreateLogger<AadAuthConnection>(),
            _innerConnection,
            _refreshInterval,
            _timeProvider);

        await authConnection.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _authConnection = authConnection;
        _client = new OpenSearchClient(new ConnectionSettings(_options.Endpoint, authConnection));

        _logger.OpenSearchConnectorReady(_options.Endpoint, _options.AllowedIndices.Count);
    }

    /// <inheritdoc />
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
            return false;

        try
        {
            var response = await _client.LowLevel
                .RootNodeInfoAsync<StringResponse>(ctx: cancellationToken)
                .ConfigureAwait(false);

            return response.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.OpenSearchHealthProbeFailed(ex, _options.Endpoint);
            return false;
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<ToolDefinition> Tools() => [VectorSearchTool.Build(this)];

    /// <inheritdoc />
    public async Task<JsonElement> SearchAsync(string index, JsonObject body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentNullException.ThrowIfNull(body);

        if (_client is null)
        {
            throw new InvalidOperationException(
                $"{nameof(OpenSearchConnector)} was used before {nameof(ConnectAsync)} completed.");
        }

        var response = await _client.LowLevel
            .SearchAsync<StringResponse>(index, PostData.String(body.ToJsonString()), ctx: cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccessful(response))
        {
            // DebugInformation can carry the full request and response payload; keep it in the
            // server log and hand the model only the status.
            _logger.OpenSearchSearchFailed(
                response.OriginalException,
                index,
                response.HttpStatusCode,
                response.DebugInformation);

            throw new ConnectorException(
                $"The OpenSearch query against index '{index}' failed with status {response.HttpStatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.OpenSearchUnreadableResponse(ex, index);
            throw new ConnectorException($"OpenSearch returned an unreadable response for index '{index}'.", ex);
        }
    }

    /// <summary>
    /// The low-level client reports some non-2xx responses (a 404 for a missing index, say) as
    /// successful "known errors", so the status code is checked directly rather than trusting
    /// <see cref="OpenSearchResponseBase.Success"/> alone.
    /// </summary>
    private static bool IsSuccessful(StringResponse response) =>
        response.Success && response.HttpStatusCode is >= 200 and <= 299;

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_authConnection is not null)
        {
            await _authConnection.DisposeAsync().ConfigureAwait(false);
            _authConnection = null;
        }

        _client = null;
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

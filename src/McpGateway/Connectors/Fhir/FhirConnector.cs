using System.Net.Http.Headers;
using McpGateway.Connectors.Fhir.Extraction;
using McpGateway.Connectors.Fhir.Indexing;
using McpGateway.Connectors.Fhir.Tools;
using McpGateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace McpGateway.Connectors.Fhir;

/// <summary>
/// Owns the FHIR server connection and the local document index, and exposes keyword search over
/// the text extracted from clinical document attachments.
///
/// A FHIR server cannot full-text search inside a base64 PDF — the bytes are opaque to it — so
/// searching scanned and PDF documents means extracting their text into an index this gateway
/// owns. That index holds PHI copied out of the FHIR server; see the README for what that implies
/// for storage.
/// </summary>
public sealed class FhirConnector : Connector
{
    private readonly FhirConnectorOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FhirConnector> _logger;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _ownsTransport;

    private HttpClient? _httpClient;
    private FhirApi? _api;
    private IDocumentIndex? _index;
    private DocumentIngestor? _ingestor;

    /// <summary>Creates the connector.</summary>
    /// <param name="options">Validated endpoint, credential and index settings.</param>
    /// <param name="loggerFactory">Logger factory for the connector and its ingestor.</param>
    /// <param name="transport">HTTP transport to use; a real handler when omitted. Tests inject a fake.</param>
    /// <param name="index">Index to use; a SQLite index at the configured path when omitted.</param>
    public FhirConnector(
        FhirConnectorOptions options,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? transport = null,
        IDocumentIndex? index = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<FhirConnector>();
        _transport = transport;
        _ownsTransport = transport is null;
        _index = index;
    }

    /// <inheritdoc />
    public override string Name => "fhir";

    /// <summary>The document index, available once <see cref="ConnectAsync"/> has run.</summary>
    public IDocumentIndex Index => _index ?? throw new InvalidOperationException(
        $"{nameof(FhirConnector)} was used before {nameof(ConnectAsync)} completed.");

    /// <summary>The ingestor driving incremental sync, available once connected.</summary>
    public DocumentIngestor Ingestor => _ingestor ?? throw new InvalidOperationException(
        $"{nameof(FhirConnector)} was used before {nameof(ConnectAsync)} completed.");

    /// <summary>How often the background sync should run.</summary>
    public TimeSpan SyncInterval => _options.SyncInterval;

    /// <summary>Whether a sync should run shortly after startup.</summary>
    public bool SyncOnStartup => _options.SyncOnStartup;

    /// <inheritdoc />
    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_api is not null)
            return;

        _httpClient = new HttpClient(_transport ?? new HttpClientHandler(), disposeHandler: _ownsTransport)
        {
            BaseAddress = _options.Endpoint,
            Timeout = _options.RequestTimeout,
        };

        ApplyCredential(_httpClient);

        _api = new FhirApi(_httpClient, _options.MaxAttachmentBytes);

        var index = _index ?? new SqliteDocumentIndex(_options.IndexPath);
        await index.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _index = index;

        _ingestor = new DocumentIngestor(
            _api,
            index,
            new CompositeTextExtractor(),
            _options,
            _loggerFactory.CreateLogger<DocumentIngestor>());

        var statistics = await index.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        _logger.FhirConnectorReady(
            _options.Endpoint,
            statistics.TotalDocuments,
            statistics.SearchableDocuments,
            _options.IndexPath);
    }

    /// <inheritdoc />
    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
    {
        if (_api is null)
            return false;

        try
        {
            return await _api.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.FhirHealthProbeFailed(ex, _options.Endpoint);
            return false;
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<ToolDefinition> Tools() =>
    [
        SearchDocumentsTool.Build(() => Index),
        GetDocumentTool.Build(() => Index),
    ];

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        _api = null;
        _ingestor = null;

        if (_index is not null)
        {
            await _index.DisposeAsync().ConfigureAwait(false);
            _index = null;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ApplyCredential(HttpClient httpClient)
    {
        switch (_options.AuthScheme)
        {
            case FhirAuthScheme.Bearer:
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _options.Credential);
                break;

            case FhirAuthScheme.ApiKeyHeader:
                httpClient.DefaultRequestHeaders.Remove(_options.ApiKeyHeaderName);
                httpClient.DefaultRequestHeaders.Add(_options.ApiKeyHeaderName, _options.Credential);
                break;

            case FhirAuthScheme.None:
                break;

            default:
                throw new NotSupportedException($"Unsupported FHIR auth scheme '{_options.AuthScheme}'.");
        }
    }
}

using McpGateway.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;

namespace McpGateway.Auth;

/// <summary>
/// Decorates an OpenSearch connection so every request carries a current Azure AD bearer token,
/// instead of an <c>Authorization</c> header captured once when the client was built.
///
/// AAD access tokens expire. A background loop renews the cached token well ahead of expiry so
/// the request path — which OpenSearch.Net invokes synchronously — never blocks on a token fetch.
/// </summary>
public sealed class AadAuthConnection : IConnection, IAsyncDisposable
{
    /// <summary>How often the cached token is renewed.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IAccessTokenProvider _tokenProvider;
    private readonly string _scope;
    private readonly ILogger<AadAuthConnection> _logger;
    private readonly IConnection _inner;
    private readonly bool _ownsInner;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();

    private PeriodicTimer? _timer;
    private Task? _refreshLoop;
    private volatile string? _cachedToken;

    /// <summary>Creates a connection that stamps requests with tokens for <paramref name="scope"/>.</summary>
    /// <param name="tokenProvider">Source of access tokens.</param>
    /// <param name="scope">AAD scope to request tokens for.</param>
    /// <param name="logger">Logger for renewal failures.</param>
    /// <param name="inner">Connection to delegate to. A real <see cref="HttpConnection"/> when omitted.</param>
    /// <param name="refreshInterval">Renewal cadence. <see cref="DefaultRefreshInterval"/> when omitted.</param>
    /// <param name="timeProvider">Clock driving the renewal loop.</param>
    public AadAuthConnection(
        IAccessTokenProvider tokenProvider,
        string scope,
        ILogger<AadAuthConnection> logger,
        IConnection? inner = null,
        TimeSpan? refreshInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _scope = scope;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsInner = inner is null;
        _inner = inner ?? new HttpConnection();
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of times the token has been fetched. Exposed for diagnostics and tests.</summary>
    internal int RefreshCount { get; private set; }

    /// <summary>
    /// Fetches the first token and starts the renewal loop. Awaiting the first fetch means a
    /// misconfigured identity fails at startup rather than on the first search.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (_refreshLoop is not null)
            return;

        // The timer is created here rather than inside the loop so that it is already
        // scheduled once this method returns.
        _timer = new PeriodicTimer(_refreshInterval, _timeProvider);
        _refreshLoop = Task.Run(() => RunRefreshLoopAsync(_timer, _shutdown.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public TResponse Request<TResponse>(RequestData requestData)
        where TResponse : class, IOpenSearchResponse, new()
    {
        StampAuthorization(requestData);
        return _inner.Request<TResponse>(requestData);
    }

    /// <inheritdoc />
    public Task<TResponse> RequestAsync<TResponse>(RequestData requestData, CancellationToken cancellationToken)
        where TResponse : class, IOpenSearchResponse, new()
    {
        StampAuthorization(requestData);
        return _inner.RequestAsync<TResponse>(requestData, cancellationToken);
    }

    private void StampAuthorization(RequestData requestData)
    {
        ArgumentNullException.ThrowIfNull(requestData);

        var token = _cachedToken ?? throw new InvalidOperationException(
            $"{nameof(AadAuthConnection)} was used before {nameof(InitializeAsync)} completed.");

        requestData.Headers["Authorization"] = $"Bearer {token}";
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _cachedToken = await _tokenProvider.GetTokenAsync(_scope, cancellationToken).ConfigureAwait(false);
        RefreshCount++;
    }

    private async Task RunRefreshLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep serving the previous, still-valid token and try again next tick.
                    _logger.TokenRenewalFailed(ex, _scope);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
            await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            _refreshLoop = null;
        }

        _timer?.Dispose();
        _timer = null;
        _shutdown.Dispose();

        if (_ownsInner)
            _inner.Dispose();
    }
}

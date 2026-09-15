using OpenSearch.Net;

namespace McpGateway.Auth;

/// <summary>Custom OpenSearch.Net connection that stamps every request with a
/// fresh Bearer token, instead of the default of setting an Authorization
/// header once when the client is built. AAD access tokens expire; a
/// background loop refreshes the cached token well before that so
/// `Request`/`RequestAsync` never block waiting on a token fetch.</summary>
public sealed class AadAuthConnection : HttpConnection, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ManagedIdentityTokenProvider _tokenProvider;
    private readonly string _scope;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _refreshLoopTask;
    private volatile string? _cachedToken;

    public AadAuthConnection(ManagedIdentityTokenProvider tokenProvider, string scope)
    {
        _tokenProvider = tokenProvider;
        _scope = scope;
        _refreshLoopTask = RefreshLoopAsync(_cts.Token);
    }

    /// <summary>Fetches the first token and waits for it, so ConnectAsync can
    /// fail fast if the identity can't get a token at all, rather than
    /// deferring that failure to the first real request.</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken) => RefreshOnceAsync(cancellationToken);

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        _cachedToken = await _tokenProvider.GetTokenAsync(_scope, cancellationToken);
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        await RefreshOnceAsync(cancellationToken);
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshOnceAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Keep serving the previous (still-valid) token; try again next tick.
            }
        }
    }

    public override TResponse Request<TResponse>(RequestData requestData)
    {
        ApplyAuthHeader(requestData);
        return base.Request<TResponse>(requestData);
    }

    public override Task<TResponse> RequestAsync<TResponse>(RequestData requestData, CancellationToken cancellationToken)
    {
        ApplyAuthHeader(requestData);
        return base.RequestAsync<TResponse>(requestData, cancellationToken);
    }

    private void ApplyAuthHeader(RequestData requestData)
    {
        var token = _cachedToken
            ?? throw new InvalidOperationException("AadAuthConnection used before its first token fetch completed.");
        requestData.Headers["Authorization"] = $"Bearer {token}";
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _refreshLoopTask;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}

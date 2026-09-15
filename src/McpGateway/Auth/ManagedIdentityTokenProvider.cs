using Azure.Core;
using McpGateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace McpGateway.Auth;

/// <summary>
/// Caches Azure AD access tokens per resource scope, renewing them before they expire.
///
/// Both directions of the gateway (Azure AI to Gateway, Gateway to backends) use Azure AD
/// tokens wherever the backend supports AAD auth; centralizing acquisition here keeps
/// connectors from each reinventing the caching and renewal window.
/// </summary>
public sealed class ManagedIdentityTokenProvider : IAccessTokenProvider, IDisposable
{
    /// <summary>How long before expiry a cached token stops being handed out.</summary>
    public static readonly TimeSpan RenewalWindow = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly ILogger<ManagedIdentityTokenProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<string, AccessToken> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a provider over the supplied credential.</summary>
    public ManagedIdentityTokenProvider(
        TokenCredential credential,
        ILogger<ManagedIdentityTokenProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(scope, out var cached) && !IsDueForRenewal(cached))
                return cached.Token;

            var token = await _credential
                .GetTokenAsync(new TokenRequestContext([scope]), cancellationToken)
                .ConfigureAwait(false);

            _cache[scope] = token;
            _logger.AccessTokenAcquired(scope, token.ExpiresOn);

            return token.Token;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private bool IsDueForRenewal(AccessToken token) =>
        token.ExpiresOn - _timeProvider.GetUtcNow() <= RenewalWindow;

    /// <inheritdoc />
    public void Dispose() => _mutex.Dispose();
}

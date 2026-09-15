using Azure.Core;
using Azure.Identity;

namespace McpGateway.Auth;

/// <summary>Caches AAD tokens per resource scope, refreshing before expiry.
/// Both directions of the gateway (Azure AI -> Gateway, Gateway ->
/// backends) use Azure AD tokens wherever the backend supports AAD auth;
/// this centralizes token acquisition so connectors don't each reinvent
/// it.</summary>
public sealed class ManagedIdentityTokenProvider
{
    private readonly DefaultAzureCredential _credential = new();
    private readonly Dictionary<string, AccessToken> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(scope, out var cached)
                && cached.ExpiresOn - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
            {
                return cached.Token;
            }

            var token = await _credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            _cache[scope] = token;
            return token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}

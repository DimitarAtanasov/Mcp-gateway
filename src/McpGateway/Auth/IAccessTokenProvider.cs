namespace McpGateway.Auth;

/// <summary>
/// Supplies Azure AD access tokens for outbound calls to backends. Abstracted so connectors
/// depend on the capability rather than on <c>DefaultAzureCredential</c> directly.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>Returns a currently valid access token for <paramref name="scope"/>.</summary>
    ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken);
}

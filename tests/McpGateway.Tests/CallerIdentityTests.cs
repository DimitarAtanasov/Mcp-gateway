using System.Security.Claims;
using McpGateway.Auth;
using Xunit;

namespace McpGateway.Tests;

public sealed class CallerIdentityTests
{
    [Fact]
    public void FromPrincipal_NullPrincipal_IsUnknown()
    {
        var identity = CallerIdentity.FromPrincipal(null);

        Assert.True(identity.IsUnknown);
        Assert.Equal(CallerIdentity.UnknownName, identity.Primary);
        Assert.Empty(identity.Candidates);
    }

    [Fact]
    public void FromPrincipal_UnauthenticatedPrincipal_IsUnknown()
    {
        // An identity with no authentication type is unauthenticated even when it carries claims.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("appid", "app-1")]));

        var identity = CallerIdentity.FromPrincipal(principal);

        Assert.True(identity.IsUnknown);
        Assert.Equal(CallerIdentity.UnknownName, identity.Primary);
    }

    [Fact]
    public void FromPrincipal_AuthenticatedWithoutUsableClaims_IsUnknown()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(new Claim("name", "someone")));

        Assert.True(identity.IsUnknown);
    }

    [Fact]
    public void FromPrincipal_PrefersAppIdOverEverythingElse()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(
            new Claim("oid", "object-id"),
            new Claim("azp", "authorized-party"),
            new Claim("appid", "app-id")));

        Assert.Equal("app-id", identity.Primary);
    }

    [Fact]
    public void FromPrincipal_FallsBackToAuthorizedParty()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(
            new Claim("azp", "authorized-party"),
            new Claim("oid", "object-id")));

        Assert.Equal("authorized-party", identity.Primary);
    }

    [Fact]
    public void FromPrincipal_FallsBackToObjectId()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(new Claim("oid", "object-id")));

        Assert.Equal("object-id", identity.Primary);
    }

    [Fact]
    public void FromPrincipal_FallsBackToSubject()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(new Claim("sub", "subject")));

        Assert.Equal("subject", identity.Primary);
    }

    [Fact]
    public void FromPrincipal_ReadsClaimsMappedToLongFormUris()
    {
        // The JWT bearer middleware rewrites `oid` to its SOAP-era claim type unless mapping is off.
        var identity = CallerIdentity.FromPrincipal(Authenticated(
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "object-id")));

        Assert.Equal("object-id", identity.Primary);
    }

    [Fact]
    public void FromPrincipal_KeepsEveryIdentifierAsACandidate()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(
            new Claim("appid", "app-id"),
            new Claim("oid", "object-id"),
            new Claim("sub", "subject")));

        Assert.Equal(
            ["app-id", "object-id", "subject"],
            identity.Candidates.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MatchesAny_MatchesOnANonPrimaryCandidate()
    {
        // The operator listed the service principal's object id; the token carries an app id too.
        var identity = CallerIdentity.FromPrincipal(Authenticated(
            new Claim("appid", "app-id"),
            new Claim("oid", "object-id")));

        Assert.True(identity.MatchesAny(new HashSet<string>(["object-id"], StringComparer.Ordinal)));
    }

    [Fact]
    public void MatchesAny_IsCaseInsensitive()
    {
        var identity = CallerIdentity.FromPrincipal(Authenticated(new Claim("appid", "AAAA-BBBB")));

        Assert.True(identity.MatchesAny(new HashSet<string>(["aaaa-bbbb"], StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MatchesAny_UnknownIdentityMatchesNothing()
    {
        Assert.False(CallerIdentity.Unknown.MatchesAny(
            new HashSet<string>([CallerIdentity.UnknownName], StringComparer.Ordinal)));
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));
}

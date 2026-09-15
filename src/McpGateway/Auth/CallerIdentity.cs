using System.Security.Claims;

namespace McpGateway.Auth;

/// <summary>
/// The validated identity of an MCP caller, resolved from the claims that Azure AD issues for
/// application tokens.
///
/// Azure AD spells the calling application differently depending on token version and flow:
/// v1 tokens carry <c>appid</c>, v2 tokens carry <c>azp</c>, and the service principal's object
/// id arrives as <c>oid</c>. The registry's authz map may reasonably be written against any of
/// them, so every candidate is kept and the allowlist matches on any of them.
/// </summary>
public sealed class CallerIdentity
{
    /// <summary>Identity used when no authenticated principal is present. Matches nothing.</summary>
    public const string UnknownName = "unknown";

    private static readonly string[] AppIdClaimTypes =
        ["appid", "http://schemas.microsoft.com/identity/claims/appid"];

    private static readonly string[] AuthorizedPartyClaimTypes = ["azp"];

    private static readonly string[] ObjectIdClaimTypes =
        ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"];

    private static readonly string[] SubjectClaimTypes = ["sub", ClaimTypes.NameIdentifier];

    /// <summary>An unauthenticated caller; fails every authorization check.</summary>
    public static readonly CallerIdentity Unknown = new(UnknownName, []);

    private readonly HashSet<string> _candidates;

    private CallerIdentity(string primary, IEnumerable<string> candidates)
    {
        Primary = primary;
        _candidates = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The identifier used in logs: the caller's app id when present.</summary>
    public string Primary { get; }

    /// <summary>Every identifier this caller may be listed under in the authz map.</summary>
    public IReadOnlyCollection<string> Candidates => _candidates;

    /// <summary>True when no authenticated principal supplied any usable identifier.</summary>
    public bool IsUnknown => _candidates.Count == 0;

    /// <summary>Resolves the caller from a principal populated by the JWT bearer middleware.</summary>
    public static CallerIdentity FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return Unknown;

        var appId = FindFirst(principal, AppIdClaimTypes);
        var authorizedParty = FindFirst(principal, AuthorizedPartyClaimTypes);
        var objectId = FindFirst(principal, ObjectIdClaimTypes);
        var subject = FindFirst(principal, SubjectClaimTypes);

        var candidates = new[] { appId, authorizedParty, objectId, subject }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        return candidates.Length == 0
            ? Unknown
            : new CallerIdentity(candidates[0], candidates);
    }

    /// <summary>True when any of this caller's identifiers appears in <paramref name="allowed"/>.</summary>
    public bool MatchesAny(IReadOnlySet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return _candidates.Any(allowed.Contains);
    }

    /// <inheritdoc />
    public override string ToString() => Primary;

    private static string? FindFirst(ClaimsPrincipal principal, string[] claimTypes) =>
        claimTypes
            .Select(principal.FindFirst)
            .FirstOrDefault(claim => !string.IsNullOrWhiteSpace(claim?.Value))
            ?.Value;
}

using McpGateway.Registry;

namespace McpGateway.Connectors.Fhir;

/// <summary>How the gateway authenticates to the FHIR server.</summary>
public enum FhirAuthScheme
{
    /// <summary>No credential. Only sensible against a local test server holding synthetic data.</summary>
    None,

    /// <summary><c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    Bearer,

    /// <summary>The credential in a custom header, e.g. <c>X-API-Key</c>.</summary>
    ApiKeyHeader,
}

/// <summary>Validated configuration for the FHIR connector.</summary>
public sealed class FhirConnectorOptions
{
    /// <summary>Default header for <see cref="FhirAuthScheme.ApiKeyHeader"/>.</summary>
    public const string DefaultApiKeyHeader = "X-API-Key";

    /// <summary>Base URL of the FHIR server, e.g. <c>https://hapi.example.org/fhir/</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Which credential to present.</summary>
    public FhirAuthScheme AuthScheme { get; init; } = FhirAuthScheme.Bearer;

    /// <summary>Header name when <see cref="AuthScheme"/> is <see cref="FhirAuthScheme.ApiKeyHeader"/>.</summary>
    public string ApiKeyHeaderName { get; init; } = DefaultApiKeyHeader;

    /// <summary>
    /// The credential itself, supplied by environment variable or a mounted secret. Never logged,
    /// never returned to a caller.
    /// </summary>
    public string? Credential { get; init; }

    /// <summary>Where the index lives. <c>:memory:</c> keeps extracted PHI out of storage.</summary>
    public string IndexPath { get; init; } = "index.db";

    /// <summary>DocumentReferences requested per page.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Pages per sync run, bounding how long one run can hold the server.</summary>
    public int MaxPagesPerSync { get; init; } = 200;

    /// <summary>Largest attachment fetched. Larger ones are recorded as too large, not indexed.</summary>
    public long MaxAttachmentBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>Characters of extracted text kept per document.</summary>
    public int MaxIndexedCharacters { get; init; } = 500_000;

    /// <summary>How often the background sync runs.</summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether to sync shortly after startup rather than waiting a full interval.</summary>
    public bool SyncOnStartup { get; init; } = true;

    /// <summary>Timeout for a single FHIR request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Validates a registry entry and the ambient credential into connector options.</summary>
    /// <exception cref="RegistryValidationException">The entry cannot produce a usable connector.</exception>
    public static FhirConnectorOptions FromEntry(ConnectorEntry entry, FhirRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(entry.Endpoint))
            throw new RegistryValidationException("Connector 'fhir' requires an 'endpoint'.");

        if (!Uri.TryCreate(entry.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new RegistryValidationException(
                $"Connector 'fhir' has an endpoint that is not an absolute URL: '{entry.Endpoint}'.");
        }

        var isLoopback = endpoint.IsLoopback;
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && !isLoopback)
        {
            throw new RegistryValidationException(
                $"Connector 'fhir' must use https; '{entry.Endpoint}' uses '{endpoint.Scheme}'. " +
                "Patient data and the API credential must never travel in clear text.");
        }

        if (settings.AuthScheme != FhirAuthScheme.None && string.IsNullOrWhiteSpace(settings.Credential))
        {
            throw new RegistryValidationException(
                $"The FHIR connector is configured for {settings.AuthScheme} auth but no credential was supplied. " +
                "Set FHIR_CREDENTIAL, or set FHIR_AUTH_SCHEME=None for an unauthenticated test server.");
        }

        // A base address must end in '/' or Uri composition drops its last path segment, which
        // silently turns https://host/fhir/ into https://host/ for every request.
        var normalized = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");

        return new FhirConnectorOptions
        {
            Endpoint = normalized,
            AuthScheme = settings.AuthScheme,
            ApiKeyHeaderName = settings.ApiKeyHeaderName,
            Credential = settings.Credential,
            IndexPath = settings.IndexPath,
            PageSize = settings.PageSize,
            SyncInterval = settings.SyncInterval,
            SyncOnStartup = settings.SyncOnStartup,
            MaxAttachmentBytes = settings.MaxAttachmentBytes,
        };
    }
}

/// <summary>
/// Deployment-time FHIR settings that do not belong in the registry file, either because they are
/// secret or because they are environment-specific.
/// </summary>
public sealed class FhirRuntimeSettings
{
    /// <summary>Which credential to present.</summary>
    public FhirAuthScheme AuthScheme { get; init; } = FhirAuthScheme.Bearer;

    /// <summary>Header name for API-key auth.</summary>
    public string ApiKeyHeaderName { get; init; } = FhirConnectorOptions.DefaultApiKeyHeader;

    /// <summary>The credential, from environment or a mounted secret.</summary>
    public string? Credential { get; init; }

    /// <summary>Where the index lives.</summary>
    public string IndexPath { get; init; } = "index.db";

    /// <summary>DocumentReferences per page.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>How often the background sync runs.</summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether to sync shortly after startup.</summary>
    public bool SyncOnStartup { get; init; } = true;

    /// <summary>Largest attachment fetched.</summary>
    public long MaxAttachmentBytes { get; init; } = 25 * 1024 * 1024;
}

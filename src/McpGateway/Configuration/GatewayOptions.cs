using System.ComponentModel.DataAnnotations;
using McpGateway.Connectors.Fhir;

namespace McpGateway.Configuration;

/// <summary>Transport the gateway exposes MCP over.</summary>
public enum GatewayTransport
{
    /// <summary>Local development: a trusted pipe between this process and whatever spawned it.</summary>
    Stdio,

    /// <summary>Production: HTTP, fronted by APIM or Container Apps auth, with per-call bearer tokens.</summary>
    StreamableHttp,
}

/// <summary>Strongly typed gateway configuration, bound from the environment.</summary>
public sealed class GatewayOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Gateway";

    /// <summary>Transport to serve MCP over. Defaults to <see cref="GatewayTransport.Stdio"/>.</summary>
    public GatewayTransport Transport { get; set; } = GatewayTransport.Stdio;

    /// <summary>Path to the registry file describing connectors, tools and the authz map.</summary>
    [Required]
    public string RegistryPath { get; set; } = "registry.yaml";

    /// <summary>Azure AD tenant that issues caller tokens. Required for <see cref="GatewayTransport.StreamableHttp"/>.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// This gateway's own app registration client id. Microsoft.Identity.Web derives the accepted
    /// <c>aud</c> values from it. Required for <see cref="GatewayTransport.StreamableHttp"/>.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>Azure AD instance. Override for sovereign clouds (e.g. <c>https://login.microsoftonline.us/</c>).</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Address the HTTP transport binds to.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Port the HTTP transport binds to.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 8000;

    /// <summary>Path the MCP endpoint is served at.</summary>
    public string McpPath { get; set; } = "/mcp";

    /// <summary>How the gateway authenticates to the FHIR server.</summary>
    public FhirAuthScheme FhirAuthScheme { get; set; } = FhirAuthScheme.Bearer;

    /// <summary>Header carrying the credential when the scheme is ApiKeyHeader.</summary>
    public string FhirApiKeyHeader { get; set; } = FhirConnectorOptions.DefaultApiKeyHeader;

    /// <summary>
    /// The FHIR credential. Comes from the environment or a mounted secret, is never logged, and
    /// is never returned to a caller.
    /// </summary>
    public string? FhirCredential { get; set; }

    /// <summary>
    /// Where the document index lives. <c>:memory:</c> keeps extracted PHI out of storage at the
    /// cost of re-syncing after every restart.
    /// </summary>
    public string IndexPath { get; set; } = "index.db";

    /// <summary>How often the background document sync runs.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether to sync shortly after startup rather than waiting a full interval.</summary>
    public bool SyncOnStartup { get; set; } = true;

    /// <summary>DocumentReferences requested per page during sync.</summary>
    public int FhirPageSize { get; set; } = 50;

    /// <summary>Largest attachment fetched; larger ones are recorded as too large, not indexed.</summary>
    public long MaxAttachmentBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Projects the deployment-time FHIR settings for the connector factory.</summary>
    public FhirRuntimeSettings ToFhirSettings() => new()
    {
        AuthScheme = FhirAuthScheme,
        ApiKeyHeaderName = FhirApiKeyHeader,
        Credential = FhirCredential,
        IndexPath = IndexPath,
        PageSize = FhirPageSize,
        SyncInterval = SyncInterval,
        SyncOnStartup = SyncOnStartup,
        MaxAttachmentBytes = MaxAttachmentBytes,
    };

    /// <summary>Builds the Kestrel URL for the HTTP transport.</summary>
    public string BindUrl => $"http://{Host}:{Port}";
}

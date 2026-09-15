using System.ComponentModel.DataAnnotations;

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

    /// <summary>AAD scope the OpenSearch connector requests tokens for.</summary>
    public string OpenSearchScope { get; set; } = "https://opensearch.azure.com/.default";

    /// <summary>Builds the Kestrel URL for the HTTP transport.</summary>
    public string BindUrl => $"http://{Host}:{Port}";
}

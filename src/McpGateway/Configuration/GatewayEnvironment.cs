using System.Diagnostics.CodeAnalysis;

namespace McpGateway.Configuration;

/// <summary>
/// Maps the gateway's documented environment variables onto configuration keys.
///
/// The names are mapped explicitly rather than through a configuration prefix because
/// .NET's environment-variable provider treats <c>_</c> as part of the key, not as a word
/// separator: <c>GATEWAY_EXPECTED_AUDIENCE</c> would never bind to <c>ExpectedAudience</c>.
/// </summary>
public static class GatewayEnvironment
{
    /// <summary>Environment variable names this gateway reads, in documentation order.</summary>
    public static readonly IReadOnlyDictionary<string, string> VariableToConfigurationKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GATEWAY_TRANSPORT"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.Transport)}",
            ["GATEWAY_REGISTRY_PATH"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.RegistryPath)}",
            ["GATEWAY_EXPECTED_AUDIENCE"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.ExpectedAudience)}",
            ["GATEWAY_HOST"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.Host)}",
            ["GATEWAY_PORT"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.Port)}",
            ["GATEWAY_MCP_PATH"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.McpPath)}",
            ["FHIR_AUTH_SCHEME"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.FhirAuthScheme)}",
            ["FHIR_API_KEY_HEADER"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.FhirApiKeyHeader)}",
            ["FHIR_CREDENTIAL"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.FhirCredential)}",
            ["GATEWAY_INDEX_PATH"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.IndexPath)}",
            ["GATEWAY_SYNC_INTERVAL"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.SyncInterval)}",
            ["GATEWAY_SYNC_ON_STARTUP"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.SyncOnStartup)}",
            ["FHIR_PAGE_SIZE"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.FhirPageSize)}",
            ["FHIR_MAX_ATTACHMENT_BYTES"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.MaxAttachmentBytes)}",
            ["AZURE_TENANT_ID"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.TenantId)}",
            ["AZURE_AD_INSTANCE"] = $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.Instance)}",
        };

    /// <summary>
    /// Projects environment variables onto configuration keys, skipping absent or blank values so
    /// they fall back to the defaults on <see cref="GatewayOptions"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ToConfiguration(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (variable, key) in VariableToConfigurationKey)
        {
            var value = readVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                values[key] = NormalizeValue(key, value);
        }

        return values;
    }

    /// <summary>Reads the gateway's variables from the current process environment.</summary>
    public static IReadOnlyDictionary<string, string?> ToConfiguration() =>
        ToConfiguration(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Accepts the wire spelling of the transport (<c>streamable-http</c>) alongside the enum
    /// spelling, so deployment scripts can use the name the MCP specification uses.
    /// </summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Matching a lowercase wire format, not producing a display or security value.")]
    private static string NormalizeValue(string key, string value) =>
        key.EndsWith($":{nameof(GatewayOptions.Transport)}", StringComparison.Ordinal)
            ? value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant()
            : value.Trim();
}

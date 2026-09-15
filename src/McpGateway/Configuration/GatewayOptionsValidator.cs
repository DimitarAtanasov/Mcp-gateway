using Microsoft.Extensions.Options;

namespace McpGateway.Configuration;

/// <summary>
/// Fails startup when the HTTP transport is selected without the values needed to validate
/// caller tokens, rather than silently serving an unauthenticated gateway.
/// </summary>
public sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RegistryPath))
            failures.Add("Gateway:RegistryPath (GATEWAY_REGISTRY_PATH) must not be empty.");

        if (options.Port is < 1 or > 65535)
            failures.Add($"Gateway:Port (GATEWAY_PORT) must be between 1 and 65535 but was {options.Port}.");

        if (!options.McpPath.StartsWith('/'))
            failures.Add($"Gateway:McpPath (GATEWAY_MCP_PATH) must start with '/' but was '{options.McpPath}'.");

        if (options.Transport == GatewayTransport.StreamableHttp)
        {
            if (string.IsNullOrWhiteSpace(options.TenantId))
                failures.Add("AZURE_TENANT_ID is required when GATEWAY_TRANSPORT=streamable-http.");

            if (string.IsNullOrWhiteSpace(options.ExpectedAudience))
                failures.Add("GATEWAY_EXPECTED_AUDIENCE is required when GATEWAY_TRANSPORT=streamable-http.");

            if (!Uri.TryCreate(options.Instance, UriKind.Absolute, out var instance) ||
                !string.Equals(instance.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                failures.Add($"AZURE_AD_INSTANCE must be an absolute https URL but was '{options.Instance}'.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

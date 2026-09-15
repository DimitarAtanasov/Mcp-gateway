using McpGateway.Registry;

namespace McpGateway.Connectors.OpenSearch;

/// <summary>Validated configuration for a single OpenSearch connector.</summary>
public sealed class OpenSearchConnectorOptions
{
    /// <summary>Default AAD scope for an Azure-fronted OpenSearch endpoint.</summary>
    public const string DefaultAadScope = "https://opensearch.azure.com/.default";

    private OpenSearchConnectorOptions(Uri endpoint, IReadOnlySet<string> allowedIndices, string aadScope)
    {
        Endpoint = endpoint;
        AllowedIndices = allowedIndices;
        AadScope = aadScope;
    }

    /// <summary>Cluster endpoint.</summary>
    public Uri Endpoint { get; }

    /// <summary>Indices the model may query. Empty means no restriction.</summary>
    public IReadOnlySet<string> AllowedIndices { get; }

    /// <summary>AAD scope tokens are requested for.</summary>
    public string AadScope { get; }

    /// <summary>Validates a registry entry into connector options.</summary>
    /// <exception cref="RegistryValidationException">The entry is missing or has an unusable endpoint.</exception>
    public static OpenSearchConnectorOptions FromEntry(ConnectorEntry entry, string? aadScope = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Endpoint))
            throw new RegistryValidationException("Connector 'opensearch' requires an 'endpoint'.");

        if (!Uri.TryCreate(entry.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new RegistryValidationException(
                $"Connector 'opensearch' has an endpoint that is not an absolute URL: '{entry.Endpoint}'.");
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new RegistryValidationException(
                $"Connector 'opensearch' must use https; '{entry.Endpoint}' uses '{endpoint.Scheme}'. " +
                "Bearer tokens must never travel in clear text.");
        }

        var allowedIndices = entry.AllowedIndices
            .Where(index => !string.IsNullOrWhiteSpace(index))
            .ToHashSet(StringComparer.Ordinal);

        return new OpenSearchConnectorOptions(
            endpoint,
            allowedIndices,
            string.IsNullOrWhiteSpace(aadScope) ? DefaultAadScope : aadScope);
    }
}

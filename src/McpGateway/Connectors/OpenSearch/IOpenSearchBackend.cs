using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpGateway.Connectors.OpenSearch;

/// <summary>
/// The slice of the OpenSearch connector its tools depend on. Keeping tools behind this
/// interface lets them be tested against canned responses without a live cluster.
/// </summary>
public interface IOpenSearchBackend
{
    /// <summary>Indices the model may query. Empty means no restriction.</summary>
    IReadOnlySet<string> AllowedIndices { get; }

    /// <summary>Runs a search against <paramref name="index"/> and returns the raw response body.</summary>
    /// <exception cref="ConnectorException">The cluster rejected the request or was unreachable.</exception>
    Task<JsonElement> SearchAsync(string index, JsonObject body, CancellationToken cancellationToken);
}

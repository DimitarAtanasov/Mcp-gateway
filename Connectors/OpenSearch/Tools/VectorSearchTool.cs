using System.Text.Json;

namespace McpGateway.Connectors.OpenSearch.Tools;

/// <summary>vector_search tool: k-NN vector similarity search against an
/// OpenSearch index.</summary>
internal static class VectorSearchTool
{
    // Hard caps independent of what the caller asks for - protects the
    // model's context window from an oversized result dump.
    private const int MaxResults = 20;
    private const int MaxSourceFieldChars = 2000;

    private static readonly JsonElement InputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query_embedding": {
              "type": "array",
              "items": { "type": "number" },
              "description": "Pre-computed query embedding vector."
            },
            "index": {
              "type": "string",
              "description": "Target index name. Must be in the connector's allowlist."
            },
            "top_k": { "type": "integer", "minimum": 1, "maximum": 20, "default": 5 },
            "filters": {
              "type": "object",
              "description": "Optional term filters applied alongside the kNN query."
            }
          },
          "required": ["query_embedding", "index"]
        }
        """).RootElement.Clone();

    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "score": { "type": "number" },
                  "source": { "type": "object" }
                }
              }
            }
          }
        }
        """).RootElement.Clone();

    public static ToolDefinition Build(OpenSearchConnector connector) => new()
    {
        Name = "vector_search",
        Description =
            "k-NN vector similarity search against an OpenSearch index. " +
            "Requires a pre-computed query embedding.",
        InputSchema = InputSchema,
        OutputSchema = OutputSchema,
        Handler = (arguments, cancellationToken) => HandleAsync(connector, arguments, cancellationToken),
    };

    private static async Task<object> HandleAsync(
        OpenSearchConnector connector,
        IDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("index", out var indexElement) || indexElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("'index' is required and must be a string.");
        var index = indexElement.GetString()!;

        if (connector.AllowedIndices.Count > 0 && !connector.AllowedIndices.Contains(index))
        {
            throw new ArgumentException(
                $"Index '{index}' is not in the allowed_indices list for this connector. " +
                "The model may not query arbitrary indices.");
        }

        if (!arguments.TryGetValue("query_embedding", out var embeddingElement) || embeddingElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("'query_embedding' is required and must be an array of numbers.");
        var queryEmbedding = embeddingElement.EnumerateArray().Select(e => e.GetDouble()).ToArray();

        var topK = arguments.TryGetValue("top_k", out var topKElement) && topKElement.ValueKind == JsonValueKind.Number
            ? Math.Min(topKElement.GetInt32(), MaxResults)
            : Math.Min(5, MaxResults);

        var knnQuery = new Dictionary<string, object>
        {
            ["knn"] = new Dictionary<string, object>
            {
                ["embedding"] = new Dictionary<string, object> { ["vector"] = queryEmbedding, ["k"] = topK },
            },
        };

        object query = knnQuery;
        if (arguments.TryGetValue("filters", out var filtersElement) && filtersElement.ValueKind == JsonValueKind.Object)
        {
            var filterClauses = filtersElement.EnumerateObject()
                .Select(p => (object)new Dictionary<string, object>
                {
                    ["term"] = new Dictionary<string, object?> { [p.Name] = JsonElementToObject(p.Value) },
                })
                .ToList();

            query = new Dictionary<string, object>
            {
                ["bool"] = new Dictionary<string, object>
                {
                    ["must"] = new[] { knnQuery },
                    ["filter"] = filterClauses,
                },
            };
        }

        var body = new Dictionary<string, object> { ["size"] = topK, ["query"] = query };

        var result = await connector.SearchRawAsync(index, body, cancellationToken);
        var hits = result.TryGetProperty("hits", out var hitsElement) && hitsElement.TryGetProperty("hits", out var hitList)
            ? hitList.EnumerateArray().Take(MaxResults)
            : [];

        var results = hits.Select(hit => new Dictionary<string, object?>
        {
            ["id"] = hit.GetProperty("_id").GetString(),
            ["score"] = hit.GetProperty("_score").GetDouble(),
            ["source"] = TruncateSource(hit.TryGetProperty("_source", out var source) ? source : default),
        }).ToList();

        return new Dictionary<string, object> { ["results"] = results };
    }

    private static Dictionary<string, object?> TruncateSource(JsonElement source)
    {
        var truncated = new Dictionary<string, object?>();
        if (source.ValueKind != JsonValueKind.Object)
            return truncated;

        foreach (var field in source.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.String)
            {
                var value = field.Value.GetString()!;
                truncated[field.Name] = value.Length > MaxSourceFieldChars
                    ? value[..MaxSourceFieldChars] + "...[truncated]"
                    : value;
            }
            else
            {
                truncated[field.Name] = JsonElementToObject(field.Value);
            }
        }

        return truncated;
    }

    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.Clone(),
    };
}

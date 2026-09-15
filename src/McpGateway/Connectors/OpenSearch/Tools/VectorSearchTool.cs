using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace McpGateway.Connectors.OpenSearch.Tools;

/// <summary>k-NN vector similarity search against an OpenSearch index.</summary>
public static class VectorSearchTool
{
    /// <summary>Tool name as advertised over MCP.</summary>
    public const string ToolName = "vector_search";

    /// <summary>Hard cap on returned hits, independent of what the caller asks for.</summary>
    public const int MaxResults = 20;

    /// <summary>Hits returned when the caller does not ask for a specific count.</summary>
    public const int DefaultResults = 5;

    /// <summary>Longest string field returned per hit before truncation.</summary>
    public const int MaxSourceFieldChars = 2000;

    /// <summary>Longest embedding accepted, guarding against oversized request bodies.</summary>
    public const int MaxEmbeddingDimensions = 4096;

    private static readonly JsonElement InputSchemaElement = ParseSchema($$"""
        {
          "type": "object",
          "properties": {
            "query_embedding": {
              "type": "array",
              "items": { "type": "number" },
              "minItems": 1,
              "maxItems": {{MaxEmbeddingDimensions}},
              "description": "Pre-computed query embedding vector."
            },
            "index": {
              "type": "string",
              "minLength": 1,
              "description": "Target index name. Must be in the connector's allowlist."
            },
            "top_k": {
              "type": "integer",
              "minimum": 1,
              "maximum": {{MaxResults}},
              "default": {{DefaultResults}},
              "description": "How many hits to return."
            },
            "filters": {
              "type": "object",
              "additionalProperties": { "type": ["string", "number", "boolean"] },
              "description": "Optional exact-match term filters applied alongside the kNN query."
            }
          },
          "required": ["query_embedding", "index"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement OutputSchemaElement = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "score": { "type": ["number", "null"] },
                  "source": { "type": "object" }
                }
              }
            }
          },
          "required": ["results"]
        }
        """);

    /// <summary>Builds the tool bound to <paramref name="backend"/>.</summary>
    public static ToolDefinition Build(IOpenSearchBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        return new ToolDefinition(
            ToolName,
            "k-NN vector similarity search against an OpenSearch index. Requires a pre-computed query embedding.",
            InputSchemaElement,
            OutputSchemaElement,
            (arguments, cancellationToken) => HandleAsync(backend, arguments, cancellationToken));
    }

    /// <summary>Builds the OpenSearch request body for a k-NN search. Exposed for testing.</summary>
    internal static JsonObject BuildQueryBody(double[] queryEmbedding, int topK, JsonElement? filters)
    {
        var knnQuery = new JsonObject
        {
            ["knn"] = new JsonObject
            {
                ["embedding"] = new JsonObject
                {
                    ["vector"] = new JsonArray([.. queryEmbedding.Select(value => JsonValue.Create(value))]),
                    ["k"] = topK,
                },
            },
        };

        JsonNode query = knnQuery;

        if (filters is { ValueKind: JsonValueKind.Object } filterObject && filterObject.EnumerateObject().Any())
        {
            var filterClauses = new JsonArray();
            foreach (var filter in filterObject.EnumerateObject())
            {
                // Values are structured JSON in a term clause, never concatenated into a query
                // string, and the input schema restricts them to scalars.
                filterClauses.Add(new JsonObject
                {
                    ["term"] = new JsonObject
                    {
                        [filter.Name] = JsonNode.Parse(filter.Value.GetRawText()),
                    },
                });
            }

            query = new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["must"] = new JsonArray(knnQuery),
                    ["filter"] = filterClauses,
                },
            };
        }

        return new JsonObject
        {
            ["size"] = topK,
            ["query"] = query,
        };
    }

    private static async Task<object> HandleAsync(
        IOpenSearchBackend backend,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        var index = arguments["index"].GetString()!;

        if (backend.AllowedIndices.Count > 0 && !backend.AllowedIndices.Contains(index))
        {
            throw new ArgumentException(
                $"Index '{index}' is not in this connector's allowed_indices list. " +
                "The model may not query arbitrary indices.");
        }

        var queryEmbedding = arguments["query_embedding"]
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();

        var topK = arguments.TryGetValue("top_k", out var topKElement) && topKElement.ValueKind == JsonValueKind.Number
            ? Math.Clamp(topKElement.GetInt32(), 1, MaxResults)
            : DefaultResults;

        var filters = arguments.TryGetValue("filters", out var filtersElement) ? filtersElement : (JsonElement?)null;

        var body = BuildQueryBody(queryEmbedding, topK, filters);
        var response = await backend.SearchAsync(index, body, cancellationToken).ConfigureAwait(false);

        return new SearchResults(ExtractHits(response));
    }

    private static List<SearchHit> ExtractHits(JsonElement response)
    {
        if (!response.TryGetProperty("hits", out var outerHits) ||
            outerHits.ValueKind != JsonValueKind.Object ||
            !outerHits.TryGetProperty("hits", out var hitArray) ||
            hitArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SearchHit>();
        foreach (var hit in hitArray.EnumerateArray().Take(MaxResults))
        {
            results.Add(new SearchHit(
                hit.TryGetProperty("_id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                hit.TryGetProperty("_score", out var score) && score.ValueKind == JsonValueKind.Number
                    ? score.GetDouble()
                    : null,
                TruncateSource(hit.TryGetProperty("_source", out var source) ? source : default)));
        }

        return results;
    }

    /// <summary>Caps long string fields so one oversized document cannot flood the model's context.</summary>
    internal static Dictionary<string, JsonNode?> TruncateSource(JsonElement source)
    {
        var truncated = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        if (source.ValueKind != JsonValueKind.Object)
            return truncated;

        foreach (var field in source.EnumerateObject())
        {
            if (field.Value.ValueKind == JsonValueKind.String)
            {
                var value = field.Value.GetString()!;
                truncated[field.Name] = JsonValue.Create(value.Length > MaxSourceFieldChars
                    ? string.Concat(value.AsSpan(0, MaxSourceFieldChars), "...[truncated]")
                    : value);
            }
            else
            {
                truncated[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            }
        }

        return truncated;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record SearchResults(
        [property: JsonPropertyName("results")] IReadOnlyList<SearchHit> Results);

    private sealed record SearchHit(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("score")] double? Score,
        [property: JsonPropertyName("source")] Dictionary<string, JsonNode?> Source);
}

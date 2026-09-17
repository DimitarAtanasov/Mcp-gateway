using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpGateway.Connectors.Fhir.Indexing;

namespace McpGateway.Connectors.Fhir.Tools;

/// <summary>Keyword search over the text extracted from clinical document attachments.</summary>
public static class SearchDocumentsTool
{
    /// <summary>Tool name as advertised over MCP.</summary>
    public const string ToolName = "search_documents";

    /// <summary>Hard cap on hits returned, whatever the caller asks for.</summary>
    public const int MaxResults = 25;

    /// <summary>Hits returned when the caller does not ask for a count.</summary>
    public const int DefaultResults = 10;

    private static readonly JsonElement InputSchemaElement = Schema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "minLength": 1,
              "maxLength": 512,
              "description": "Keywords to search for. Quote a phrase to require the words together, e.g. \"chest pain\"."
            },
            "top_k": {
              "type": "integer",
              "minimum": 1,
              "maximum": {{MaxResults}},
              "default": {{DefaultResults}},
              "description": "How many documents to return."
            },
            "subject": {
              "type": "string",
              "maxLength": 128,
              "description": "Optional subject filter, exactly as stored, e.g. 'Patient/123'."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement OutputSchemaElement = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "title": { "type": ["string", "null"] },
                  "score": { "type": "number" },
                  "snippet": { "type": ["string", "null"] },
                  "subject": { "type": ["string", "null"] },
                  "type": { "type": ["string", "null"] },
                  "created": { "type": ["string", "null"] }
                }
              }
            },
            "coverage": {
              "type": "object",
              "description": "What the index holds, so an empty result can be told apart from an unread archive.",
              "properties": {
                "searchable_documents": { "type": "integer" },
                "unsearchable_documents": { "type": "integer" }
              }
            }
          },
          "required": ["results"]
        }
        """);

    /// <summary>
    /// Builds the tool. The index is resolved lazily because the registry describes and validates
    /// tools at startup, before any connector has opened its backend.
    /// </summary>
    public static ToolDefinition Build(Func<IDocumentIndex> index)
    {
        ArgumentNullException.ThrowIfNull(index);

        return new ToolDefinition(
            ToolName,
            "Keyword search over clinical documents held in the FHIR server, searching the text " +
            "extracted from their attachments. Returns matching documents with a highlighted snippet.",
            InputSchemaElement,
            OutputSchemaElement,
            (arguments, cancellationToken) => HandleAsync(index(), arguments, cancellationToken));
    }

    private static async Task<object> HandleAsync(
        IDocumentIndex index,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        var query = arguments["query"].GetString()!;

        var limit = arguments.TryGetValue("top_k", out var topK) && topK.ValueKind == JsonValueKind.Number
            ? Math.Clamp(topK.GetInt32(), 1, MaxResults)
            : DefaultResults;

        var subject = arguments.TryGetValue("subject", out var subjectElement) &&
                      subjectElement.ValueKind == JsonValueKind.String
            ? subjectElement.GetString()
            : null;

        var hits = await index.SearchAsync(query, limit, subject, cancellationToken).ConfigureAwait(false);
        var statistics = await index.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);

        // Coverage travels with every result: without it, "no matches" and "the archive is mostly
        // scans we cannot read" look identical to the caller.
        return new SearchResponse(
            hits,
            new CoverageSummary(
                statistics.SearchableDocuments,
                statistics.TotalDocuments - statistics.SearchableDocuments));
    }

    private sealed record SearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<DocumentSearchHit> Results,
        [property: JsonPropertyName("coverage")] CoverageSummary Coverage);

    private sealed record CoverageSummary(
        [property: JsonPropertyName("searchable_documents")] int SearchableDocuments,
        [property: JsonPropertyName("unsearchable_documents")] int UnsearchableDocuments);
}

/// <summary>Retrieves one indexed document's text by id.</summary>
public static class GetDocumentTool
{
    /// <summary>Tool name as advertised over MCP.</summary>
    public const string ToolName = "get_document";

    /// <summary>Characters of text returned before truncation.</summary>
    public const int MaxTextCharacters = 20_000;

    private static readonly JsonElement InputSchemaElement = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "minLength": 1,
              "maxLength": 128,
              "description": "The document id returned by search_documents."
            }
          },
          "required": ["id"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement OutputSchemaElement = Schema.Parse("""
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "title": { "type": ["string", "null"] },
            "subject": { "type": ["string", "null"] },
            "type": { "type": ["string", "null"] },
            "created": { "type": ["string", "null"] },
            "content_type": { "type": ["string", "null"] },
            "status": { "type": "string" },
            "status_detail": { "type": ["string", "null"] },
            "text": { "type": "string" },
            "truncated": { "type": "boolean" }
          },
          "required": ["id", "status", "text", "truncated"]
        }
        """);

    /// <summary>Builds the tool. The index is resolved lazily; see <see cref="SearchDocumentsTool.Build"/>.</summary>
    public static ToolDefinition Build(Func<IDocumentIndex> index)
    {
        ArgumentNullException.ThrowIfNull(index);

        return new ToolDefinition(
            ToolName,
            "Returns the full extracted text of one clinical document by its id, as returned by search_documents.",
            InputSchemaElement,
            OutputSchemaElement,
            (arguments, cancellationToken) => HandleAsync(index(), arguments, cancellationToken));
    }

    private static async Task<object> HandleAsync(
        IDocumentIndex index,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        var id = arguments["id"].GetString()!;
        var document = await index.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            throw new ArgumentException(
                $"No document with id '{id}' is indexed. Ids come from search_documents; the document may " +
                "also exist in the FHIR server but not yet be synced.");
        }

        var truncated = document.Text.Length > MaxTextCharacters;

        return new DocumentResponse(
            document.Id,
            document.Title,
            document.SubjectReference,
            document.TypeDisplay,
            document.Created,
            document.ContentType,
            document.Status.ToString(),
            document.StatusDetail,
            truncated ? document.Text[..MaxTextCharacters] : document.Text,
            truncated);
    }

    private sealed record DocumentResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("created")] DateTimeOffset? Created,
        [property: JsonPropertyName("content_type")] string? ContentType,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("status_detail")] string? StatusDetail,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("truncated")] bool Truncated);
}

/// <summary>Parses a tool's JSON Schema literal once at type load.</summary>
internal static class Schema
{
    /// <summary>Parses schema JSON into a detached element.</summary>
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Converts a JSON element into a mutable node, for tests and diagnostics.</summary>
    public static JsonNode? ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText());
}

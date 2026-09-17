using System.Text.Json.Serialization;

namespace McpGateway.Connectors.Fhir;

/// <summary>
/// Why a document's text is, or is not, searchable.
///
/// Recorded per document so an unreadable attachment is a visible gap rather than a silently
/// empty entry: a scanned PDF that indexes as empty text would otherwise be invisible to search
/// with nobody aware it was missed.
/// </summary>
public enum ExtractionStatus
{
    /// <summary>Text was extracted and indexed.</summary>
    Extracted,

    /// <summary>A PDF carrying no text layer — almost always a scan. Needs OCR, which this gateway does not do.</summary>
    NoTextLayer,

    /// <summary>The attachment's media type has no text extractor (images, opaque binaries).</summary>
    UnsupportedMediaType,

    /// <summary>The attachment exceeded the configured size cap and was not fetched.</summary>
    TooLarge,

    /// <summary>The document reference carried no retrievable attachment.</summary>
    NoAttachment,

    /// <summary>Extraction was attempted and failed.</summary>
    Failed,
}

/// <summary>
/// One indexed clinical document: the DocumentReference metadata worth searching or filtering on,
/// plus the outcome of extracting its attachment.
/// </summary>
public sealed record FhirDocument
{
    /// <summary>The DocumentReference's logical id.</summary>
    public required string Id { get; init; }

    /// <summary>The resource version, used to skip re-extracting unchanged documents.</summary>
    public string? VersionId { get; init; }

    /// <summary>Server-assigned last-modified instant, used as the incremental sync watermark.</summary>
    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>Human-readable title, from description or the type's display text.</summary>
    public string? Title { get; init; }

    /// <summary>The document's subject, e.g. <c>Patient/123</c>.</summary>
    public string? SubjectReference { get; init; }

    /// <summary>The document type's display text, e.g. "Discharge summary".</summary>
    public string? TypeDisplay { get; init; }

    /// <summary>The document type's code, e.g. a LOINC code.</summary>
    public string? TypeCode { get; init; }

    /// <summary>Clinical date of the document, as reported by the server.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>Media type of the attachment that was processed.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether the text is searchable, and if not, why not.</summary>
    public required ExtractionStatus Status { get; init; }

    /// <summary>Extra detail for a non-extracted status, safe to log (no document content).</summary>
    public string? StatusDetail { get; init; }

    /// <summary>The extracted text. Empty unless <see cref="Status"/> is <see cref="ExtractionStatus.Extracted"/>.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>One keyword-search hit.</summary>
public sealed record DocumentSearchHit
{
    /// <summary>The DocumentReference id, usable with the get_document tool.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Document title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Relevance, higher is better. Derived from BM25.</summary>
    [JsonPropertyName("score")]
    public required double Score { get; init; }

    /// <summary>Matched text with the query terms marked, for the model to judge relevance.</summary>
    [JsonPropertyName("snippet")]
    public string? Snippet { get; init; }

    /// <summary>The document's subject, e.g. <c>Patient/123</c>.</summary>
    [JsonPropertyName("subject")]
    public string? SubjectReference { get; init; }

    /// <summary>The document type's display text.</summary>
    [JsonPropertyName("type")]
    public string? TypeDisplay { get; init; }

    /// <summary>Clinical date of the document.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }
}

/// <summary>Counts of what is in the index, including what could not be read.</summary>
public sealed record IndexStatistics
{
    /// <summary>Documents present in the index, whatever their extraction status.</summary>
    public required int TotalDocuments { get; init; }

    /// <summary>Documents whose text is searchable.</summary>
    public required int SearchableDocuments { get; init; }

    /// <summary>Documents present but not searchable, by reason.</summary>
    public required IReadOnlyDictionary<string, int> UnsearchableByReason { get; init; }

    /// <summary>The incremental sync watermark, or null before the first sync.</summary>
    public DateTimeOffset? Watermark { get; init; }
}

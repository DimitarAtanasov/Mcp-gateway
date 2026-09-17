namespace McpGateway.Connectors.Fhir.Indexing;

/// <summary>
/// The local store of extracted document text and the keyword search over it.
///
/// This index holds PHI copied out of the FHIR server. Where it lives, and whether it is
/// encrypted at rest, is a deployment decision — see the index options and the README.
/// </summary>
public interface IDocumentIndex : IAsyncDisposable
{
    /// <summary>Creates the schema if it is not already present.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a document and its searchable text.</summary>
    Task UpsertAsync(FhirDocument document, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a batch of documents in one transaction.</summary>
    Task UpsertManyAsync(IReadOnlyCollection<FhirDocument> documents, CancellationToken cancellationToken);

    /// <summary>
    /// Keyword search over extracted text, ranked by relevance.
    /// </summary>
    /// <param name="query">Caller keywords; sanitized into an FTS expression before use.</param>
    /// <param name="limit">Maximum hits to return.</param>
    /// <param name="subjectReference">Optional subject filter, e.g. <c>Patient/123</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<DocumentSearchHit>> SearchAsync(
        string query,
        int limit,
        string? subjectReference,
        CancellationToken cancellationToken);

    /// <summary>Returns one document by id, or null when it is not indexed.</summary>
    Task<FhirDocument?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns the stored version id for a document, used to skip unchanged documents.</summary>
    Task<string?> GetVersionAsync(string id, CancellationToken cancellationToken);

    /// <summary>Counts of indexed documents, including those that could not be read.</summary>
    Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>The incremental sync watermark, or null before the first sync.</summary>
    Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken);

    /// <summary>Advances the incremental sync watermark.</summary>
    Task SetWatermarkAsync(DateTimeOffset watermark, CancellationToken cancellationToken);
}

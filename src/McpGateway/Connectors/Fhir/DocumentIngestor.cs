using System.Globalization;
using System.Text.Json;
using McpGateway.Connectors.Fhir.Extraction;
using McpGateway.Connectors.Fhir.Indexing;
using McpGateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace McpGateway.Connectors.Fhir;

/// <summary>What one ingest run did.</summary>
/// <param name="Examined">DocumentReferences read from the server.</param>
/// <param name="Indexed">Documents whose text was extracted and indexed.</param>
/// <param name="Unreadable">Documents stored with a non-extracted status.</param>
/// <param name="Skipped">Documents already indexed at the same version.</param>
/// <param name="Watermark">The highest lastUpdated seen, or null when nothing was read.</param>
public sealed record IngestReport(int Examined, int Indexed, int Unreadable, int Skipped, DateTimeOffset? Watermark);

/// <summary>
/// Walks DocumentReference resources page by page, fetches each attachment, extracts its text and
/// writes it to the index.
///
/// Sync is incremental: the highest <c>lastUpdated</c> from the previous run is the watermark for
/// the next, so a restart does not re-download an entire archive. Documents whose version has not
/// changed are skipped without re-fetching the attachment, which is the expensive part.
/// </summary>
public sealed class DocumentIngestor
{
    private readonly IFhirApi _api;
    private readonly IDocumentIndex _index;
    private readonly CompositeTextExtractor _extractor;
    private readonly FhirConnectorOptions _options;
    private readonly ILogger<DocumentIngestor> _logger;

    /// <summary>Creates the ingestor.</summary>
    public DocumentIngestor(
        IFhirApi api,
        IDocumentIndex index,
        CompositeTextExtractor extractor,
        FhirConnectorOptions options,
        ILogger<DocumentIngestor> logger)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one incremental sync and returns what it did.</summary>
    public async Task<IngestReport> SyncAsync(CancellationToken cancellationToken)
    {
        var watermark = await _index.GetWatermarkAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_count"] = _options.PageSize.ToString(CultureInfo.InvariantCulture),
            ["_sort"] = "_lastUpdated",
        };

        if (watermark is { } since)
            parameters["_lastUpdated"] = $"gt{FhirApi.FormatInstant(since)}";

        var examined = 0;
        var indexed = 0;
        var unreadable = 0;
        var skipped = 0;
        var highest = watermark;
        var pages = 0;

        var page = await _api.SearchAsync("DocumentReference", parameters, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var batch = new List<FhirDocument>(page.Entries.Count);

            foreach (var resource in page.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;

                var reference = DocumentReferenceReader.Read(resource);
                if (reference is null)
                    continue;

                if (reference.LastUpdated is { } updated && (highest is null || updated > highest))
                    highest = updated;

                // The attachment is the expensive part of the loop, so an unchanged version is
                // skipped before any fetch happens.
                if (reference.VersionId is not null)
                {
                    var known = await _index.GetVersionAsync(reference.Id, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(known, reference.VersionId, StringComparison.Ordinal))
                    {
                        skipped++;
                        continue;
                    }
                }

                var document = await BuildDocumentAsync(reference, cancellationToken).ConfigureAwait(false);
                batch.Add(document);

                if (document.Status == ExtractionStatus.Extracted)
                    indexed++;
                else
                    unreadable++;
            }

            if (batch.Count > 0)
                await _index.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);

            pages++;
            if (page.NextPageUrl is null || pages >= _options.MaxPagesPerSync)
                break;

            page = await _api.SearchNextAsync(page.NextPageUrl, cancellationToken).ConfigureAwait(false);
        }

        if (highest is { } advanced && advanced != watermark)
            await _index.SetWatermarkAsync(advanced, cancellationToken).ConfigureAwait(false);

        _logger.DocumentSyncCompleted(examined, indexed, unreadable, skipped);
        return new IngestReport(examined, indexed, unreadable, skipped, highest);
    }

    private async Task<FhirDocument> BuildDocumentAsync(
        DocumentReferenceMetadata reference,
        CancellationToken cancellationToken)
    {
        var document = new FhirDocument
        {
            Id = reference.Id,
            VersionId = reference.VersionId,
            LastUpdated = reference.LastUpdated,
            Title = reference.Title,
            SubjectReference = reference.SubjectReference,
            TypeCode = reference.TypeCode,
            TypeDisplay = reference.TypeDisplay,
            Created = reference.Created,
            ContentType = reference.ContentType,
            Status = ExtractionStatus.NoAttachment,
            StatusDetail = "The DocumentReference carried no inline data and no attachment URL.",
        };

        try
        {
            ReadOnlyMemory<byte> content;
            var mediaType = reference.ContentType;

            if (reference.InlineData is { } inline)
            {
                if (inline.Length > _options.MaxAttachmentBytes)
                {
                    return document with
                    {
                        Status = ExtractionStatus.TooLarge,
                        StatusDetail = $"Inline attachment exceeds the {_options.MaxAttachmentBytes} byte cap.",
                    };
                }

                content = inline;
            }
            else if (!string.IsNullOrWhiteSpace(reference.AttachmentUrl))
            {
                var fetched = await _api
                    .FetchAttachmentAsync(reference.AttachmentUrl, cancellationToken)
                    .ConfigureAwait(false);

                if (fetched is null)
                {
                    return document with
                    {
                        Status = ExtractionStatus.TooLarge,
                        StatusDetail = "The attachment was unavailable or exceeded the size cap.",
                    };
                }

                content = fetched.Value.Content;
                mediaType = fetched.Value.MediaType ?? mediaType;
            }
            else
            {
                return document;
            }

            var result = _extractor.Extract(content, mediaType);
            var text = result.Text.Length > _options.MaxIndexedCharacters
                ? result.Text[.._options.MaxIndexedCharacters]
                : result.Text;

            return document with
            {
                ContentType = mediaType,
                Status = result.Status,
                StatusDetail = result.Detail,
                Text = text,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable document must not stop the sync; it is recorded and counted.
            _logger.DocumentIngestFailed(ex, reference.Id);
            return document with
            {
                Status = ExtractionStatus.Failed,
                StatusDetail = $"Ingest failed: {ex.GetType().Name}.",
            };
        }
    }
}

/// <summary>The DocumentReference fields this gateway indexes.</summary>
internal sealed record DocumentReferenceMetadata
{
    public required string Id { get; init; }

    public string? VersionId { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public string? Title { get; init; }

    public string? SubjectReference { get; init; }

    public string? TypeCode { get; init; }

    public string? TypeDisplay { get; init; }

    public DateTimeOffset? Created { get; init; }

    public string? ContentType { get; init; }

    public string? AttachmentUrl { get; init; }

    public ReadOnlyMemory<byte>? InlineData { get; init; }
}

/// <summary>Reads a DocumentReference resource without a full FHIR model.</summary>
internal static class DocumentReferenceReader
{
    /// <summary>Extracts the indexed fields, or null when the resource has no usable id.</summary>
    public static DocumentReferenceMetadata? Read(JsonElement resource)
    {
        var id = GetString(resource, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var (contentType, attachmentUrl, inlineData, attachmentTitle) = ReadFirstAttachment(resource);

        return new DocumentReferenceMetadata
        {
            Id = id,
            VersionId = resource.TryGetProperty("meta", out var meta) ? GetString(meta, "versionId") : null,
            LastUpdated = resource.TryGetProperty("meta", out var metaForDate)
                ? GetInstant(metaForDate, "lastUpdated")
                : null,
            Title = GetString(resource, "description")
                    ?? attachmentTitle
                    ?? ReadCodeableConcept(resource, "type").Display,
            SubjectReference = resource.TryGetProperty("subject", out var subject)
                ? GetString(subject, "reference")
                : null,
            TypeCode = ReadCodeableConcept(resource, "type").Code,
            TypeDisplay = ReadCodeableConcept(resource, "type").Display,
            Created = GetInstant(resource, "date"),
            ContentType = contentType,
            AttachmentUrl = attachmentUrl,
            InlineData = inlineData,
        };
    }

    private static (string? ContentType, string? Url, ReadOnlyMemory<byte>? Data, string? Title) ReadFirstAttachment(
        JsonElement resource)
    {
        if (!resource.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return (null, null, null, null);

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object)
                continue;

            var contentType = GetString(attachment, "contentType");
            var url = GetString(attachment, "url");
            var title = GetString(attachment, "title");

            ReadOnlyMemory<byte>? data = null;
            var encoded = GetString(attachment, "data");
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                try
                {
                    data = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    // Malformed base64 is treated as no inline data; the URL may still work.
                }
            }

            if (contentType is not null || url is not null || data is not null)
                return (contentType, url, data, title);
        }

        return (null, null, null, null);
    }

    private static (string? Code, string? Display) ReadCodeableConcept(JsonElement resource, string property)
    {
        if (!resource.TryGetProperty(property, out var concept) || concept.ValueKind != JsonValueKind.Object)
            return (null, null);

        var text = GetString(concept, "text");

        if (concept.TryGetProperty("coding", out var codings) && codings.ValueKind == JsonValueKind.Array)
        {
            foreach (var coding in codings.EnumerateArray())
            {
                var code = GetString(coding, "code");
                var display = GetString(coding, "display") ?? text;
                if (code is not null || display is not null)
                    return (code, display);
            }
        }

        return (null, text);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetInstant(JsonElement element, string property)
    {
        var raw = GetString(element, property);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}

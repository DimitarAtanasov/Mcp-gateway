namespace McpGateway.Connectors.Fhir.Extraction;

/// <summary>The outcome of trying to read an attachment's text.</summary>
/// <param name="Status">Whether text was obtained, and if not, why not.</param>
/// <param name="Text">The extracted text; empty unless <paramref name="Status"/> is Extracted.</param>
/// <param name="Detail">Detail for a failure, safe to log: never document content.</param>
public readonly record struct ExtractionResult(ExtractionStatus Status, string Text, string? Detail = null)
{
    /// <summary>Text was read successfully.</summary>
    public static ExtractionResult Success(string text) => new(ExtractionStatus.Extracted, text);

    /// <summary>The attachment parsed but carried no text — a scan, in practice.</summary>
    public static ExtractionResult NoTextLayer(string? detail = null) =>
        new(ExtractionStatus.NoTextLayer, string.Empty, detail);

    /// <summary>Nothing here can read this media type.</summary>
    public static ExtractionResult Unsupported(string mediaType) =>
        new(ExtractionStatus.UnsupportedMediaType, string.Empty, $"No text extractor for media type '{mediaType}'.");

    /// <summary>Extraction threw.</summary>
    public static ExtractionResult Failed(string detail) => new(ExtractionStatus.Failed, string.Empty, detail);
}

/// <summary>Reads plain text out of one family of attachment media types.</summary>
public interface ITextExtractor
{
    /// <summary>Whether this extractor handles <paramref name="mediaType"/>.</summary>
    bool CanExtract(string mediaType);

    /// <summary>Extracts text from the attachment bytes.</summary>
    ExtractionResult Extract(ReadOnlyMemory<byte> content, string mediaType);
}

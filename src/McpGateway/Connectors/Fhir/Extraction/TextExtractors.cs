using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace McpGateway.Connectors.Fhir.Extraction;

/// <summary>
/// Reads text out of PDF attachments.
///
/// A PDF built from a scanner holds page images and no text layer, and there is no amount of
/// parsing that recovers words from pixels: that needs OCR, which this gateway does not do. Such
/// a document is reported as <see cref="ExtractionStatus.NoTextLayer"/> rather than indexed as
/// empty, so the gap is countable instead of invisible.
/// </summary>
public sealed partial class PdfTextExtractor : ITextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string mediaType) =>
        mediaType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ExtractionResult Extract(ReadOnlyMemory<byte> content, string mediaType)
    {
        try
        {
            using var document = PdfDocument.Open(content.ToArray());

            var builder = new StringBuilder();
            var pageCount = 0;
            foreach (var page in document.GetPages())
            {
                pageCount++;
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    builder.AppendLine(text);
            }

            var extracted = TextNormalizer.Normalize(builder.ToString());

            return extracted.Length == 0
                ? ExtractionResult.NoTextLayer(
                    $"PDF parsed with {pageCount.ToString(CultureInfo.InvariantCulture)} page(s) but no text layer; OCR would be required.")
                : ExtractionResult.Success(extracted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ExtractionResult.Failed($"PDF could not be parsed: {ex.GetType().Name}.");
        }
    }
}

/// <summary>Reads text attachments, honouring the charset when the server declares one.</summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string mediaType) =>
        mediaType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ExtractionResult Extract(ReadOnlyMemory<byte> content, string mediaType)
    {
        try
        {
            var text = TextNormalizer.Normalize(MediaType.DecodeText(content, mediaType));
            return text.Length == 0 ? ExtractionResult.NoTextLayer("The attachment held no text.") : ExtractionResult.Success(text);
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
        {
            return ExtractionResult.Failed($"Text could not be decoded: {ex.GetType().Name}.");
        }
    }
}

/// <summary>Strips markup from HTML and XHTML attachments, including FHIR narrative.</summary>
public sealed partial class HtmlTextExtractor : ITextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string mediaType) =>
        mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
        mediaType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ExtractionResult Extract(ReadOnlyMemory<byte> content, string mediaType)
    {
        try
        {
            var markup = MediaType.DecodeText(content, mediaType);

            // Script and style bodies are not clinical content and would pollute the index.
            markup = ScriptOrStyle().Replace(markup, " ");
            markup = Tag().Replace(markup, " ");

            var text = TextNormalizer.Normalize(System.Net.WebUtility.HtmlDecode(markup));
            return text.Length == 0 ? ExtractionResult.NoTextLayer("The markup held no text.") : ExtractionResult.Success(text);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return ExtractionResult.Failed($"Markup could not be read: {ex.GetType().Name}.");
        }
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Tag();
}

/// <summary>
/// Strips RTF control words, which is enough to recover the body text of the RTF clinical notes
/// that document systems commonly emit.
/// </summary>
public sealed partial class RtfTextExtractor : ITextExtractor
{
    /// <inheritdoc />
    public bool CanExtract(string mediaType) =>
        mediaType.StartsWith("application/rtf", StringComparison.OrdinalIgnoreCase) ||
        mediaType.StartsWith("text/rtf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ExtractionResult Extract(ReadOnlyMemory<byte> content, string mediaType)
    {
        try
        {
            var rtf = Encoding.ASCII.GetString(content.Span);

            rtf = BinaryGroup().Replace(rtf, " ");
            rtf = ControlWord().Replace(rtf, " ");
            rtf = Braces().Replace(rtf, " ");

            var text = TextNormalizer.Normalize(rtf);
            return text.Length == 0 ? ExtractionResult.NoTextLayer("The RTF held no text.") : ExtractionResult.Success(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return ExtractionResult.Failed("RTF could not be read: parsing timed out.");
        }
    }

    // Embedded objects and pictures carry binary payloads that are not text.
    [GeneratedRegex(@"\{\\\*?\\(?:pict|object|fonttbl|colortbl|stylesheet|info)[^{}]*(?:\{[^{}]*\}[^{}]*)*\}",
        RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BinaryGroup();

    [GeneratedRegex(@"\\(?:[a-zA-Z]+-?\d*[ ]?|[^a-zA-Z])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ControlWord();

    [GeneratedRegex(@"[{}]", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Braces();
}

/// <summary>Dispatches an attachment to the extractor that handles its media type.</summary>
public sealed class CompositeTextExtractor
{
    private readonly IReadOnlyList<ITextExtractor> _extractors;

    /// <summary>Creates a dispatcher over the supplied extractors, tried in order.</summary>
    public CompositeTextExtractor(IReadOnlyList<ITextExtractor>? extractors = null) =>
        _extractors = extractors ??
        [
            new PdfTextExtractor(),
            new PlainTextExtractor(),
            new HtmlTextExtractor(),
            new RtfTextExtractor(),
        ];

    /// <summary>Media types this gateway can read, for diagnostics and documentation.</summary>
    public static IReadOnlyList<string> SupportedMediaTypes { get; } =
        ["application/pdf", "text/plain", "text/html", "application/xhtml+xml", "application/rtf", "text/rtf"];

    /// <summary>Extracts text, or reports why it could not be read.</summary>
    public ExtractionResult Extract(ReadOnlyMemory<byte> content, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ExtractionResult.Unsupported("(none declared)");

        var normalized = MediaType.BaseType(mediaType);
        var extractor = _extractors.FirstOrDefault(candidate => candidate.CanExtract(normalized));

        return extractor is null
            ? ExtractionResult.Unsupported(normalized)
            : extractor.Extract(content, mediaType);
    }
}

/// <summary>Media type helpers shared by the extractors.</summary>
internal static class MediaType
{
    /// <summary>Strips parameters, so <c>text/plain; charset=utf-8</c> becomes <c>text/plain</c>.</summary>
    public static string BaseType(string mediaType)
    {
        var separator = mediaType.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? mediaType : mediaType[..separator]).Trim();
    }

    /// <summary>Decodes bytes using the declared charset, falling back to UTF-8.</summary>
    public static string DecodeText(ReadOnlyMemory<byte> content, string mediaType)
    {
        var encoding = Encoding.UTF8;

        var marker = mediaType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var name = mediaType[(marker + "charset=".Length)..].Trim().Trim('"');
            var separator = name.IndexOf(';', StringComparison.Ordinal);
            if (separator >= 0)
                name = name[..separator];

            try
            {
                encoding = Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                // An unknown charset is not worth failing the document over; UTF-8 is the
                // overwhelmingly likely intent.
            }
        }

        return encoding.GetString(content.Span);
    }
}

/// <summary>Collapses extracted text into something worth indexing.</summary>
internal static partial class TextNormalizer
{
    /// <summary>Collapses runs of whitespace and trims, so snippets read cleanly.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        try
        {
            return Whitespace().Replace(text, " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return text.Trim();
        }
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Whitespace();
}

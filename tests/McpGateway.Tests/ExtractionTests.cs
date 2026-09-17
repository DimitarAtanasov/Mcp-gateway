using System.Text;
using McpGateway.Connectors.Fhir;
using McpGateway.Connectors.Fhir.Extraction;
using Xunit;

namespace McpGateway.Tests;

public sealed class TextExtractionTests
{
    private readonly CompositeTextExtractor _extractor = new();

    [Fact]
    public void Extract_PlainText_ReturnsTheText()
    {
        var result = _extractor.Extract(Bytes("Patient presented with chest pain."), "text/plain");

        Assert.Equal(ExtractionStatus.Extracted, result.Status);
        Assert.Equal("Patient presented with chest pain.", result.Text);
    }

    [Fact]
    public void Extract_HonoursTheDeclaredCharset()
    {
        var latin1 = Encoding.Latin1.GetBytes("Röntgen");

        var result = _extractor.Extract(latin1, "text/plain; charset=iso-8859-1");

        Assert.Equal("Röntgen", result.Text);
    }

    [Fact]
    public void Extract_UnknownCharset_FallsBackToUtf8()
    {
        var result = _extractor.Extract(Bytes("chest pain"), "text/plain; charset=not-a-charset");

        Assert.Equal(ExtractionStatus.Extracted, result.Status);
    }

    [Fact]
    public void Extract_Html_StripsMarkup()
    {
        var html = "<html><body><h1>Discharge</h1><p>Chest&nbsp;pain resolved.</p></body></html>";

        var result = _extractor.Extract(Bytes(html), "text/html");

        Assert.Equal(ExtractionStatus.Extracted, result.Status);
        Assert.Contains("Discharge", result.Text, StringComparison.Ordinal);
        Assert.Contains("Chest", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_Html_DropsScriptAndStyleBodies()
    {
        var html = "<html><head><style>p{color:red}</style><script>var secret='xyz';</script></head>" +
                   "<body><p>Clinical note</p></body></html>";

        var result = _extractor.Extract(Bytes(html), "text/html");

        Assert.Contains("Clinical note", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_Rtf_RecoversTheBodyText()
    {
        const string rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Times;}}\f0\fs24 Patient reports chest pain.\par}";

        var result = _extractor.Extract(Bytes(rtf), "application/rtf");

        Assert.Equal(ExtractionStatus.Extracted, result.Status);
        Assert.Contains("Patient reports chest pain.", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("rtf1", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("fonttbl", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_UnsupportedMediaType_IsReportedNotSilentlyEmpty()
    {
        var result = _extractor.Extract(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png");

        Assert.Equal(ExtractionStatus.UnsupportedMediaType, result.Status);
        Assert.Contains("image/png", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_MissingMediaType_IsReported()
    {
        var result = _extractor.Extract(Bytes("text"), null);

        Assert.Equal(ExtractionStatus.UnsupportedMediaType, result.Status);
    }

    [Fact]
    public void Extract_MediaTypeParameters_DoNotDefeatDispatch()
    {
        var result = _extractor.Extract(Bytes("note"), "text/plain; charset=utf-8");

        Assert.Equal(ExtractionStatus.Extracted, result.Status);
    }

    [Fact]
    public void Extract_EmptyText_IsReportedAsNoTextLayer()
    {
        var result = _extractor.Extract(Bytes("   "), "text/plain");

        Assert.Equal(ExtractionStatus.NoTextLayer, result.Status);
    }

    [Fact]
    public void Extract_CorruptPdf_FailsWithoutThrowing()
    {
        var result = _extractor.Extract(Bytes("%PDF-1.4 this is not really a pdf"), "application/pdf");

        Assert.Equal(ExtractionStatus.Failed, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void Extract_NormalizesWhitespace()
    {
        var result = _extractor.Extract(Bytes("chest\n\n\tpain   resolved"), "text/plain");

        Assert.Equal("chest pain resolved", result.Text);
    }

    [Fact]
    public void SupportedMediaTypes_AreDocumented()
    {
        Assert.Contains("application/pdf", CompositeTextExtractor.SupportedMediaTypes);
        Assert.DoesNotContain("image/tiff", CompositeTextExtractor.SupportedMediaTypes);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}

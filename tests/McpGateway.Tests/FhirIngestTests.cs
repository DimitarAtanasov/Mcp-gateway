using System.Net;
using System.Text;
using McpGateway.Connectors.Fhir;
using McpGateway.Connectors.Fhir.Extraction;
using McpGateway.Connectors.Fhir.Indexing;
using McpGateway.Registry;
using McpGateway.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpGateway.Tests;

public sealed class DocumentIngestorTests : IAsyncLifetime
{
    private readonly SqliteDocumentIndex _index = new(":memory:");
    private readonly FakeFhirApi _api = new();

    public async Task InitializeAsync() => await _index.InitializeAsync(CancellationToken.None);

    public async Task DisposeAsync() => await _index.DisposeAsync();

    [Fact]
    public async Task SyncAsync_IndexesADocumentAndItsText()
    {
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1")))
            .AddAttachment("Binary/att-1", "Patient presented with acute chest pain.");

        var report = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(1, report.Examined);
        Assert.Equal(1, report.Indexed);
        Assert.Single(await _index.SearchAsync("chest pain", 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task SyncAsync_PagesThroughEveryPage()
    {
        _api.AddPage(FhirSamples.Bundle("https://fhir.example/next-1", FhirSamples.DocumentReference("doc-1")))
            .AddPage(FhirSamples.Bundle("https://fhir.example/next-2", FhirSamples.DocumentReference("doc-2")))
            .AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-3")))
            .AddAttachment("Binary/att-1", "first note")
            .AddAttachment("Binary/att-2", "second note");

        var report = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(3, report.Examined);
        Assert.Equal(["https://fhir.example/next-1", "https://fhir.example/next-2"], _api.NextPageRequests);
    }

    [Fact]
    public async Task SyncAsync_FirstRun_DoesNotFilterByWatermark()
    {
        _api.AddPage(FhirSamples.Bundle(null));

        await Ingestor().SyncAsync(CancellationToken.None);

        Assert.DoesNotContain("_lastUpdated", Assert.Single(_api.SearchRequests).Keys);
    }

    [Fact]
    public async Task SyncAsync_AdvancesTheWatermarkToTheNewestDocument()
    {
        _api.AddPage(FhirSamples.Bundle(
                null,
                FhirSamples.DocumentReference("doc-1", lastUpdated: "2026-01-10T00:00:00Z"),
                FhirSamples.DocumentReference("doc-2", lastUpdated: "2026-02-20T00:00:00Z")))
            .AddAttachment("Binary/att-1", "note");

        await Ingestor().SyncAsync(CancellationToken.None);

        var watermark = await _index.GetWatermarkAsync(CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero), watermark);
    }

    [Fact]
    public async Task SyncAsync_SubsequentRun_AsksOnlyForNewerDocuments()
    {
        await _index.SetWatermarkAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        _api.AddPage(FhirSamples.Bundle(null));

        await Ingestor().SyncAsync(CancellationToken.None);

        var parameters = Assert.Single(_api.SearchRequests);
        Assert.StartsWith("gt2026-01-01T00:00:00", parameters["_lastUpdated"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_UnchangedVersion_IsSkippedWithoutRefetchingTheAttachment()
    {
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1", versionId: "3")))
            .AddAttachment("Binary/att-1", "note");
        await Ingestor().SyncAsync(CancellationToken.None);

        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1", versionId: "3")));
        var second = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Indexed);
        Assert.Single(_api.AttachmentRequests);
    }

    [Fact]
    public async Task SyncAsync_ChangedVersion_IsReindexed()
    {
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1", versionId: "1")))
            .AddAttachment("Binary/att-1", "original text");
        await Ingestor().SyncAsync(CancellationToken.None);

        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1", versionId: "2")))
            .AddAttachment("Binary/att-1", "revised text");
        var second = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(1, second.Indexed);
        Assert.Single(await _index.SearchAsync("revised", 10, null, CancellationToken.None));
        Assert.Empty(await _index.SearchAsync("original", 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task SyncAsync_InlineAttachment_IsIndexedWithoutAFetch()
    {
        var inline = Convert.ToBase64String(Encoding.UTF8.GetBytes("inline discharge note"));
        _api.AddPage(FhirSamples.Bundle(
            null,
            FhirSamples.DocumentReference("doc-1", attachmentUrl: null, inlineBase64: inline)));

        var report = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(1, report.Indexed);
        Assert.Empty(_api.AttachmentRequests);
        Assert.Single(await _index.SearchAsync("discharge", 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task SyncAsync_ScannedPdf_IsRecordedAsUnreadableRatherThanSilentlyEmpty()
    {
        // A PDF with no text layer: parseable, but there are no words to index.
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("scan-1", contentType: "application/pdf")))
            .AddAttachmentBytes("Binary/att-1", MinimalPdfWithoutText(), "application/pdf");

        var report = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(0, report.Indexed);
        Assert.Equal(1, report.Unreadable);

        var document = await _index.GetAsync("scan-1", CancellationToken.None);
        Assert.NotNull(document);
        Assert.True(
            document!.Status is ExtractionStatus.NoTextLayer or ExtractionStatus.Failed,
            $"A scan must be recorded as unreadable, but was {document.Status}.");
    }

    [Fact]
    public async Task SyncAsync_UnsupportedMediaType_IsRecordedWithItsReason()
    {
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("img-1", contentType: "image/tiff")))
            .AddAttachmentBytes("Binary/att-1", [0x49, 0x49, 0x2A, 0x00], "image/tiff");

        await Ingestor().SyncAsync(CancellationToken.None);

        var document = await _index.GetAsync("img-1", CancellationToken.None);
        Assert.Equal(ExtractionStatus.UnsupportedMediaType, document!.Status);
        Assert.Contains("image/tiff", document.StatusDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_MissingAttachment_IsRecordedNotDropped()
    {
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1")));

        await Ingestor().SyncAsync(CancellationToken.None);

        var document = await _index.GetAsync("doc-1", CancellationToken.None);
        Assert.Equal(ExtractionStatus.TooLarge, document!.Status);
    }

    [Fact]
    public async Task SyncAsync_DocumentReferenceWithNoAttachmentAtAll_IsRecorded()
    {
        _api.AddPage(FhirSamples.Bundle(
            null,
            FhirSamples.DocumentReference("doc-1", contentType: null, attachmentUrl: null)));

        await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(ExtractionStatus.NoAttachment, (await _index.GetAsync("doc-1", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task SyncAsync_OneFailingDocument_DoesNotStopTheRun()
    {
        _api.AddPage(FhirSamples.Bundle(
            null,
            FhirSamples.DocumentReference("doc-1"),
            FhirSamples.DocumentReference("doc-2")));
        _api.ThrowOnAttachment = new HttpRequestException("backend blew up");

        var report = await Ingestor().SyncAsync(CancellationToken.None);

        Assert.Equal(2, report.Examined);
        Assert.Equal(2, report.Unreadable);
        Assert.Equal(ExtractionStatus.Failed, (await _index.GetAsync("doc-1", CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task SyncAsync_CarriesMetadataOntoTheIndexedDocument()
    {
        _api.AddPage(FhirSamples.Bundle(null, FhirSamples.DocumentReference("doc-1")))
            .AddAttachment("Binary/att-1", "note");

        await Ingestor().SyncAsync(CancellationToken.None);

        var document = await _index.GetAsync("doc-1", CancellationToken.None);
        Assert.Equal("Discharge summary", document!.Title);
        Assert.Equal("Patient/123", document.SubjectReference);
        Assert.Equal("18842-5", document.TypeCode);
        Assert.Equal(new DateTimeOffset(2026, 1, 14, 9, 0, 0, TimeSpan.Zero), document.Created);
    }

    [Fact]
    public async Task SyncAsync_StopsAtThePageCap()
    {
        for (var i = 0; i < 5; i++)
            _api.AddPage(FhirSamples.Bundle($"https://fhir.example/page-{i}", FhirSamples.DocumentReference($"doc-{i}")));

        var ingestor = new DocumentIngestor(
            _api,
            _index,
            new CompositeTextExtractor(),
            OptionsWithPageCap(2),
            NullLogger<DocumentIngestor>.Instance);

        var report = await ingestor.SyncAsync(CancellationToken.None);

        Assert.Equal(2, report.Examined);
    }

    private DocumentIngestor Ingestor() => new(
        _api,
        _index,
        new CompositeTextExtractor(),
        Options(),
        NullLogger<DocumentIngestor>.Instance);

    private static FhirConnectorOptions Options() => new()
    {
        Endpoint = new Uri("https://fhir.example/"),
        AuthScheme = FhirAuthScheme.None,
        PageSize = 50,
    };

    private static FhirConnectorOptions OptionsWithPageCap(int cap) => new()
    {
        Endpoint = new Uri("https://fhir.example/"),
        AuthScheme = FhirAuthScheme.None,
        PageSize = 50,
        MaxPagesPerSync = cap,
    };

    /// <summary>A structurally valid PDF whose single page draws nothing — what a scan looks like to a parser.</summary>
    private static byte[] MinimalPdfWithoutText()
    {
        const string pdf = """
            %PDF-1.4
            1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
            2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
            3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj
            trailer<</Root 1 0 R>>
            %%EOF
            """;
        return Encoding.ASCII.GetBytes(pdf);
    }
}

public sealed class FhirConnectorTests
{
    [Fact]
    public async Task ConnectAsync_PresentsABearerToken()
    {
        var handler = FakeHttpMessageHandler.Json("""{"resourceType":"CapabilityStatement"}""");
        await using var connector = Connector(handler, FhirAuthScheme.Bearer, "secret-token");

        await connector.ConnectAsync(CancellationToken.None);
        await connector.HealthCheckAsync(CancellationToken.None);

        Assert.Equal("Bearer secret-token", Assert.Single(handler.AuthorizationHeaders));
    }

    [Fact]
    public async Task ConnectAsync_PresentsAnApiKeyHeader()
    {
        var handler = FakeHttpMessageHandler.Json("""{"resourceType":"CapabilityStatement"}""");
        await using var connector = Connector(handler, FhirAuthScheme.ApiKeyHeader, "secret-key");

        await connector.ConnectAsync(CancellationToken.None);
        await connector.HealthCheckAsync(CancellationToken.None);

        Assert.Equal("secret-key", Assert.Single(handler.ApiKeyHeaders));
        Assert.Null(Assert.Single(handler.AuthorizationHeaders));
    }

    [Fact]
    public async Task HealthCheckAsync_BeforeConnect_IsFalse()
    {
        await using var connector = Connector(FakeHttpMessageHandler.Json("{}"), FhirAuthScheme.None, null);

        Assert.False(await connector.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthCheckAsync_ServerError_IsFalse()
    {
        var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        await using var connector = Connector(handler, FhirAuthScheme.None, null);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.False(await connector.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthCheckAsync_ReachableServer_IsTrue()
    {
        var handler = FakeHttpMessageHandler.Json("""{"resourceType":"CapabilityStatement"}""");
        await using var connector = Connector(handler, FhirAuthScheme.None, null);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.True(await connector.HealthCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tools_ExposesSearchAndGet()
    {
        await using var connector = Connector(FakeHttpMessageHandler.Json("{}"), FhirAuthScheme.None, null);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.Equal(["search_documents", "get_document"], connector.Tools().Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public async Task Tools_AreDescribableBeforeConnect()
    {
        // The registry validates the tool names in registry.yaml at startup, before any
        // connector opens its backend, so Tools() must not require a live index.
        await using var connector = Connector(FakeHttpMessageHandler.Json("{}"), FhirAuthScheme.None, null);

        Assert.Equal(["search_documents", "get_document"], connector.Tools().Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public async Task ConnectAsync_IsIdempotent()
    {
        var handler = FakeHttpMessageHandler.Json("""{"resourceType":"CapabilityStatement"}""");
        await using var connector = Connector(handler, FhirAuthScheme.None, null);

        await connector.ConnectAsync(CancellationToken.None);
        await connector.ConnectAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    private static FhirConnector Connector(HttpMessageHandler transport, FhirAuthScheme scheme, string? credential) =>
        new(
            FhirConnectorOptions.FromEntry(
                new ConnectorEntry { Name = "fhir", Enabled = true, Endpoint = "https://fhir.example/fhir" },
                new FhirRuntimeSettings { AuthScheme = scheme, Credential = credential, IndexPath = ":memory:" }),
            NullLoggerFactory.Instance,
            transport);
}

public sealed class FhirConnectorOptionsTests
{
    [Fact]
    public void FromEntry_MissingEndpoint_Throws()
    {
        var exception = Assert.Throws<RegistryValidationException>(() => FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir" },
            Settings()));

        Assert.Contains("requires an 'endpoint'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEntry_PlaintextEndpoint_Throws()
    {
        var exception = Assert.Throws<RegistryValidationException>(() => FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir", Endpoint = "http://fhir.example/fhir" },
            Settings()));

        Assert.Contains("must use https", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEntry_PlaintextLocalhost_IsAllowedForDevelopment()
    {
        var options = FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir", Endpoint = "http://localhost:8080/fhir" },
            Settings());

        Assert.Equal("http://localhost:8080/fhir/", options.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void FromEntry_AppendsATrailingSlashSoThePathIsNotLost()
    {
        // Without this, Uri composition turns https://host/fhir into https://host/ for every call.
        var options = FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir", Endpoint = "https://fhir.example/fhir" },
            Settings());

        Assert.Equal("https://fhir.example/fhir/", options.Endpoint.AbsoluteUri);
    }

    [Fact]
    public void FromEntry_AuthConfiguredWithoutACredential_Throws()
    {
        var exception = Assert.Throws<RegistryValidationException>(() => FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir", Endpoint = "https://fhir.example/fhir" },
            new FhirRuntimeSettings { AuthScheme = FhirAuthScheme.Bearer, Credential = null }));

        Assert.Contains("FHIR_CREDENTIAL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEntry_NoAuthScheme_NeedsNoCredential()
    {
        var options = FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir", Endpoint = "https://fhir.example/fhir" },
            new FhirRuntimeSettings { AuthScheme = FhirAuthScheme.None, Credential = null });

        Assert.Equal(FhirAuthScheme.None, options.AuthScheme);
    }

    [Fact]
    public void FromEntry_CarriesRuntimeSettingsThrough()
    {
        var options = FhirConnectorOptions.FromEntry(
            new ConnectorEntry { Name = "fhir", Endpoint = "https://fhir.example/fhir" },
            new FhirRuntimeSettings
            {
                AuthScheme = FhirAuthScheme.ApiKeyHeader,
                ApiKeyHeaderName = "X-Custom-Key",
                Credential = "k",
                IndexPath = ":memory:",
                PageSize = 7,
                SyncOnStartup = false,
            });

        Assert.Equal("X-Custom-Key", options.ApiKeyHeaderName);
        Assert.Equal(":memory:", options.IndexPath);
        Assert.Equal(7, options.PageSize);
        Assert.False(options.SyncOnStartup);
    }

    private static FhirRuntimeSettings Settings() => new() { AuthScheme = FhirAuthScheme.None };
}

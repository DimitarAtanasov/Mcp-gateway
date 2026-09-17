using System.Text.Json;
using McpGateway.Connectors.Fhir;
using McpGateway.Connectors.Fhir.Indexing;
using McpGateway.Connectors.Fhir.Tools;
using McpGateway.Server;
using Xunit;

namespace McpGateway.Tests;

public sealed class DocumentToolsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteDocumentIndex _index = new(":memory:");

    public async Task InitializeAsync()
    {
        await _index.InitializeAsync(CancellationToken.None);

        await _index.UpsertManyAsync(
            [
                Document("doc-1", "Patient presented with acute chest pain and elevated troponin.", "Patient/1"),
                Document("doc-2", "No acute intracranial abnormality identified on CT.", "Patient/2"),
                Document("scan-1", string.Empty, "Patient/1") with
                {
                    Status = ExtractionStatus.NoTextLayer,
                    StatusDetail = "PDF parsed with 2 page(s) but no text layer; OCR would be required.",
                },
            ],
            CancellationToken.None);
    }

    public async Task DisposeAsync() => await _index.DisposeAsync();

    [Fact]
    public void SearchDocuments_DeclaresAValidatableSchema()
    {
        var tool = SearchDocumentsTool.Build(() => _index);

        Assert.Equal("search_documents", tool.Name);
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
        Assert.Equal(
            ["query"],
            tool.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task SearchDocuments_ReturnsMatchingDocuments()
    {
        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"chest pain"}"""), CancellationToken.None);

        var hits = Json(result).GetProperty("results").EnumerateArray().ToArray();

        Assert.Single(hits);
        Assert.Equal("doc-1", hits[0].GetProperty("id").GetString());
        Assert.Contains("chest", hits[0].GetProperty("snippet").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchDocuments_NoMatch_ReturnsAnEmptyListNotAnError()
    {
        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"pneumothorax"}"""), CancellationToken.None);

        Assert.Empty(Json(result).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task SearchDocuments_ReportsCoverageSoAnEmptyResultCanBeInterpreted()
    {
        // "No matches" and "most of the archive is unreadable scans" must not look identical.
        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"pneumothorax"}"""), CancellationToken.None);

        var coverage = Json(result).GetProperty("coverage");

        Assert.Equal(2, coverage.GetProperty("searchable_documents").GetInt32());
        Assert.Equal(1, coverage.GetProperty("unsearchable_documents").GetInt32());
    }

    [Fact]
    public async Task SearchDocuments_FiltersBySubject()
    {
        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"acute","subject":"Patient/2"}"""), CancellationToken.None);

        var hit = Assert.Single(Json(result).GetProperty("results").EnumerateArray().ToArray());
        Assert.Equal("doc-2", hit.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SearchDocuments_HonoursTopK()
    {
        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"acute","top_k":1}"""), CancellationToken.None);

        Assert.Single(Json(result).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task SearchDocuments_ClampsAnOversizedTopK()
    {
        for (var i = 0; i < 40; i++)
            await _index.UpsertAsync(Document($"bulk-{i}", "acute finding", "Patient/9"), CancellationToken.None);

        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"acute","top_k":9999}"""), CancellationToken.None);

        Assert.Equal(
            SearchDocumentsTool.MaxResults,
            Json(result).GetProperty("results").EnumerateArray().Count());
    }

    [Fact]
    public async Task SearchDocuments_NeverReturnsUnreadableDocuments()
    {
        var result = await SearchDocumentsTool.Build(() => _index)
            .Handler(Arguments("""{"query":"OCR"}"""), CancellationToken.None);

        Assert.Empty(Json(result).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task SearchDocuments_ArgumentsAreValidatedAgainstTheSchema()
    {
        var validator = new ToolArgumentValidator();
        var tool = SearchDocumentsTool.Build(() => _index);

        Assert.Throws<ToolArgumentException>(() => validator.Validate(tool, Arguments("{}")));
        Assert.Throws<ToolArgumentException>(() => validator.Validate(tool, Arguments("""{"query":""}""")));
        Assert.Throws<ToolArgumentException>(() => validator.Validate(tool, Arguments("""{"query":"x","top_k":99}""")));
        Assert.Throws<ToolArgumentException>(() => validator.Validate(tool, Arguments("""{"query":"x","rogue":1}""")));

        validator.Validate(tool, Arguments("""{"query":"chest","top_k":5,"subject":"Patient/1"}"""));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetDocument_ReturnsTheFullText()
    {
        var result = await GetDocumentTool.Build(() => _index)
            .Handler(Arguments("""{"id":"doc-1"}"""), CancellationToken.None);

        var document = Json(result);

        Assert.Equal("doc-1", document.GetProperty("id").GetString());
        Assert.Contains("troponin", document.GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.False(document.GetProperty("truncated").GetBoolean());
        Assert.Equal("Extracted", document.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetDocument_UnreadableDocument_ExplainsWhyItHasNoText()
    {
        var result = await GetDocumentTool.Build(() => _index)
            .Handler(Arguments("""{"id":"scan-1"}"""), CancellationToken.None);

        var document = Json(result);

        Assert.Equal("NoTextLayer", document.GetProperty("status").GetString());
        Assert.Contains("OCR", document.GetProperty("status_detail").GetString()!, StringComparison.Ordinal);
        Assert.Empty(document.GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task GetDocument_UnknownId_FailsWithAnActionableMessage()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => GetDocumentTool.Build(() => _index).Handler(Arguments("""{"id":"nope"}"""), CancellationToken.None));

        Assert.Contains("not yet be synced", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDocument_TruncatesAnOversizedDocumentAndSaysSo()
    {
        var long_ = new string('a', GetDocumentTool.MaxTextCharacters + 500);
        await _index.UpsertAsync(Document("big-1", long_, "Patient/3"), CancellationToken.None);

        var result = await GetDocumentTool.Build(() => _index)
            .Handler(Arguments("""{"id":"big-1"}"""), CancellationToken.None);

        var document = Json(result);
        Assert.True(document.GetProperty("truncated").GetBoolean());
        Assert.Equal(GetDocumentTool.MaxTextCharacters, document.GetProperty("text").GetString()!.Length);
    }

    private static FhirDocument Document(string id, string text, string subject) => new()
    {
        Id = id,
        Title = "Clinical note",
        SubjectReference = subject,
        TypeDisplay = "Discharge summary",
        Status = ExtractionStatus.Extracted,
        Text = text,
        Created = new DateTimeOffset(2026, 1, 14, 9, 0, 0, TimeSpan.Zero),
    };

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result, SerializerOptions);

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}

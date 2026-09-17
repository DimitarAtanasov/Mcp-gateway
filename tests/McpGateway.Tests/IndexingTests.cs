using McpGateway.Connectors.Fhir;
using McpGateway.Connectors.Fhir.Indexing;
using Xunit;

namespace McpGateway.Tests;

public sealed class FtsQueryBuilderTests
{
    [Fact]
    public void Build_SingleTerm_QuotesIt()
    {
        Assert.Equal("\"troponin\"", FtsQueryBuilder.Build("troponin"));
    }

    [Fact]
    public void Build_MultipleTerms_RequiresAllOfThem()
    {
        Assert.Equal("\"chest\" AND \"pain\"", FtsQueryBuilder.Build("chest pain"));
    }

    [Fact]
    public void Build_QuotedInput_KeepsThePhraseTogether()
    {
        Assert.Equal("\"chest pain\"", FtsQueryBuilder.Build("\"chest pain\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("!!! ??? ...")]
    public void Build_NothingSearchable_ReturnsNull(string? query)
    {
        Assert.Null(FtsQueryBuilder.Build(query));
    }

    [Fact]
    public void Build_FtsOperators_AreTreatedAsLiteralTerms()
    {
        // Left raw, "AND NOT" would be FTS syntax rather than words the caller typed.
        var match = FtsQueryBuilder.Build("chest AND NOT pain");

        Assert.Equal("\"chest\" AND \"and\" AND \"not\" AND \"pain\"", match);
    }

    [Fact]
    public void Build_ColumnFilterSyntax_CannotEscapeIntoTheQuery()
    {
        // `title:` would otherwise redirect the search to a different column.
        var match = FtsQueryBuilder.Build("title:secret");

        Assert.Equal("\"title\" AND \"secret\"", match);
    }

    [Fact]
    public void Build_EmbeddedQuotes_AreEscapedNotInjected()
    {
        var match = FtsQueryBuilder.Build("\"he said \"\"ouch\"\"\"");

        Assert.NotNull(match);
        Assert.StartsWith("\"", match, StringComparison.Ordinal);
        Assert.DoesNotContain("ouch\" ", match, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PrefixStar_IsStripped()
    {
        Assert.Equal("\"cardi\"", FtsQueryBuilder.Build("cardi*"));
    }

    [Fact]
    public void Build_KeepsHyphensAndUnderscoresInsideTerms()
    {
        Assert.Equal("\"covid-19\"", FtsQueryBuilder.Build("covid-19"));
    }

    [Fact]
    public void Build_LowercasesTerms()
    {
        Assert.Equal("\"troponin\"", FtsQueryBuilder.Build("TROPONIN"));
    }

    [Fact]
    public void Build_CapsTheNumberOfTerms()
    {
        var match = FtsQueryBuilder.Build(string.Join(" ", Enumerable.Range(0, 100).Select(i => $"term{i}")));

        Assert.NotNull(match);
        Assert.Equal(FtsQueryBuilder.MaxTerms, match!.Split(" AND ").Length);
    }

    [Fact]
    public void Tokenize_TruncatesAnOverlongTerm()
    {
        var term = Assert.Single(FtsQueryBuilder.Tokenize(new string('x', 500)));

        Assert.Equal(FtsQueryBuilder.MaxTermLength, term.Length);
    }
}

public sealed class SqliteDocumentIndexTests : IAsyncLifetime
{
    private readonly SqliteDocumentIndex _index = new(":memory:");

    public async Task InitializeAsync() => await _index.InitializeAsync(CancellationToken.None);

    public async Task DisposeAsync() => await _index.DisposeAsync();

    [Fact]
    public async Task SearchAsync_FindsADocumentByKeyword()
    {
        await _index.UpsertAsync(Document("doc-1", "Patient presented with acute chest pain."), CancellationToken.None);

        var hits = await _index.SearchAsync("chest pain", 10, null, CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal("doc-1", hit.Id);
        Assert.Contains("chest", hit.Snippet!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_RanksBetterMatchesHigher()
    {
        await _index.UpsertAsync(Document("doc-1", "chest pain. chest pain again. chest pain."), CancellationToken.None);
        await _index.UpsertAsync(Document("doc-2", "the patient denied chest pain among many other unrelated findings here"), CancellationToken.None);

        var hits = await _index.SearchAsync("chest pain", 10, null, CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Score >= hits[1].Score, "Results must be ordered best-first.");
    }

    [Fact]
    public async Task SearchAsync_ScoreIsHigherIsBetter()
    {
        // BM25 is natively lower-is-better and never positive; the index flips the sign so the
        // model can read the score the obvious way. A strong match must therefore outscore a
        // weak one, and no score may come back negative.
        //
        // The filler documents matter: BM25 weights a term by how rare it is, so a term present
        // in every document scores zero everywhere and nothing is distinguishable.
        for (var i = 0; i < 8; i++)
            await _index.UpsertAsync(Document($"filler-{i}", "routine follow-up with no findings"), CancellationToken.None);

        await _index.UpsertAsync(Document("strong", "troponin troponin troponin"), CancellationToken.None);
        await _index.UpsertAsync(
            Document("weak", "troponin was not measured during this otherwise unremarkable admission"),
            CancellationToken.None);

        var hits = await _index.SearchAsync("troponin", 10, null, CancellationToken.None);

        Assert.Equal("strong", hits[0].Id);
        Assert.True(hits[0].Score > hits[1].Score, $"{hits[0].Score} should beat {hits[1].Score}.");
        Assert.All(hits, hit => Assert.True(hit.Score >= 0, $"Scores must not be negative, got {hit.Score}."));
    }

    [Fact]
    public async Task SearchAsync_HonoursTheLimit()
    {
        for (var i = 0; i < 10; i++)
            await _index.UpsertAsync(Document($"doc-{i}", "chest pain"), CancellationToken.None);

        var hits = await _index.SearchAsync("chest", 3, null, CancellationToken.None);

        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task SearchAsync_FiltersBySubject()
    {
        await _index.UpsertAsync(Document("doc-1", "chest pain", subject: "Patient/1"), CancellationToken.None);
        await _index.UpsertAsync(Document("doc-2", "chest pain", subject: "Patient/2"), CancellationToken.None);

        var hits = await _index.SearchAsync("chest", 10, "Patient/2", CancellationToken.None);

        Assert.Equal("doc-2", Assert.Single(hits).Id);
    }

    [Fact]
    public async Task SearchAsync_UnreadableDocumentsAreNeverReturned()
    {
        // A scan has no text, so it must not surface as an empty-looking match.
        await _index.UpsertAsync(
            Document("scan-1", string.Empty, status: ExtractionStatus.NoTextLayer) with { Title = "chest x-ray" },
            CancellationToken.None);

        Assert.Empty(await _index.SearchAsync("chest", 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsNothingRatherThanThrowing()
    {
        await _index.UpsertAsync(Document("doc-1", "chest pain"), CancellationToken.None);

        Assert.Empty(await _index.SearchAsync("   ", 10, null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_MaliciousQuerySyntax_DoesNotThrow()
    {
        await _index.UpsertAsync(Document("doc-1", "chest pain"), CancellationToken.None);

        // Raw, each of these is either an FTS syntax error or a column redirect.
        foreach (var query in new[] { "\"unbalanced", "body:secret", "chest NEAR/5 pain", "*", "a AND (b" })
            await _index.SearchAsync(query, 10, null, CancellationToken.None);
    }

    [Fact]
    public async Task UpsertAsync_ReplacesAnExistingDocument()
    {
        await _index.UpsertAsync(Document("doc-1", "original wording"), CancellationToken.None);
        await _index.UpsertAsync(Document("doc-1", "revised wording"), CancellationToken.None);

        Assert.Empty(await _index.SearchAsync("original", 10, null, CancellationToken.None));
        Assert.Single(await _index.SearchAsync("revised", 10, null, CancellationToken.None));
        Assert.Equal(1, (await _index.GetStatisticsAsync(CancellationToken.None)).TotalDocuments);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheStoredDocument()
    {
        await _index.UpsertAsync(Document("doc-1", "full body text"), CancellationToken.None);

        var document = await _index.GetAsync("doc-1", CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal("full body text", document!.Text);
        Assert.Equal("Patient/123", document.SubjectReference);
        Assert.Equal(ExtractionStatus.Extracted, document.Status);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _index.GetAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_UnreadableDocument_KeepsItsStatusAndDetail()
    {
        await _index.UpsertAsync(
            Document("scan-1", string.Empty, status: ExtractionStatus.NoTextLayer) with
            {
                StatusDetail = "PDF parsed with 3 page(s) but no text layer; OCR would be required.",
            },
            CancellationToken.None);

        var document = await _index.GetAsync("scan-1", CancellationToken.None);

        Assert.Equal(ExtractionStatus.NoTextLayer, document!.Status);
        Assert.Contains("OCR", document.StatusDetail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersionAsync_ReturnsTheStoredVersion()
    {
        await _index.UpsertAsync(Document("doc-1", "text") with { VersionId = "7" }, CancellationToken.None);

        Assert.Equal("7", await _index.GetVersionAsync("doc-1", CancellationToken.None));
        Assert.Null(await _index.GetVersionAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task GetStatisticsAsync_SeparatesSearchableFromUnreadable()
    {
        await _index.UpsertAsync(Document("doc-1", "text"), CancellationToken.None);
        await _index.UpsertAsync(Document("scan-1", "", status: ExtractionStatus.NoTextLayer), CancellationToken.None);
        await _index.UpsertAsync(Document("scan-2", "", status: ExtractionStatus.NoTextLayer), CancellationToken.None);
        await _index.UpsertAsync(Document("img-1", "", status: ExtractionStatus.UnsupportedMediaType), CancellationToken.None);

        var statistics = await _index.GetStatisticsAsync(CancellationToken.None);

        Assert.Equal(4, statistics.TotalDocuments);
        Assert.Equal(1, statistics.SearchableDocuments);
        Assert.Equal(2, statistics.UnsearchableByReason["NoTextLayer"]);
        Assert.Equal(1, statistics.UnsearchableByReason["UnsupportedMediaType"]);
    }

    [Fact]
    public async Task Watermark_RoundTrips()
    {
        Assert.Null(await _index.GetWatermarkAsync(CancellationToken.None));

        var watermark = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        await _index.SetWatermarkAsync(watermark, CancellationToken.None);

        Assert.Equal(watermark, await _index.GetWatermarkAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpsertManyAsync_WritesABatch()
    {
        await _index.UpsertManyAsync(
            [Document("doc-1", "alpha"), Document("doc-2", "beta"), Document("doc-3", "gamma")],
            CancellationToken.None);

        Assert.Equal(3, (await _index.GetStatisticsAsync(CancellationToken.None)).TotalDocuments);
    }

    [Fact]
    public async Task UpsertManyAsync_EmptyBatch_IsANoOp()
    {
        await _index.UpsertManyAsync([], CancellationToken.None);

        Assert.Equal(0, (await _index.GetStatisticsAsync(CancellationToken.None)).TotalDocuments);
    }

    [Fact]
    public async Task UsedBeforeInitialize_Throws()
    {
        await using var index = new SqliteDocumentIndex(":memory:");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => index.GetStatisticsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FileBackedIndex_PersistsAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idx-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = new SqliteDocumentIndex(path))
            {
                await first.InitializeAsync(CancellationToken.None);
                await first.UpsertAsync(Document("doc-1", "persisted text"), CancellationToken.None);
            }

            await using var second = new SqliteDocumentIndex(path);
            await second.InitializeAsync(CancellationToken.None);

            Assert.Single(await second.SearchAsync("persisted", 10, null, CancellationToken.None));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }

    [Fact]
    public async Task TwoInMemoryIndexes_DoNotShareAStore()
    {
        // SQLite's shared-cache ":memory:" is process-global; two indexes must not collide.
        await using var other = new SqliteDocumentIndex(":memory:");
        await other.InitializeAsync(CancellationToken.None);

        await _index.UpsertAsync(Document("doc-1", "only in the first index"), CancellationToken.None);

        Assert.Equal(0, (await other.GetStatisticsAsync(CancellationToken.None)).TotalDocuments);
        Assert.Equal(1, (await _index.GetStatisticsAsync(CancellationToken.None)).TotalDocuments);
    }

    private static FhirDocument Document(
        string id,
        string text,
        ExtractionStatus status = ExtractionStatus.Extracted,
        string subject = "Patient/123") => new()
        {
            Id = id,
            Title = "Discharge summary",
            SubjectReference = subject,
            TypeDisplay = "Discharge summary",
            Status = status,
            Text = text,
            LastUpdated = DateTimeOffset.UtcNow,
        };
}

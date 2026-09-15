using System.Text.Json;
using McpGateway.Connectors;
using McpGateway.Connectors.OpenSearch.Tools;
using McpGateway.Tests.Fakes;
using Xunit;

namespace McpGateway.Tests;

public sealed class VectorSearchToolTests
{
    [Fact]
    public void Build_DescribesItselfWithAValidatableSchema()
    {
        var tool = VectorSearchTool.Build(new FakeOpenSearchBackend());

        Assert.Equal("vector_search", tool.Name);
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
        Assert.NotNull(tool.OutputSchema);
        Assert.Equal(
            ["query_embedding", "index"],
            tool.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task Handler_RejectsAnIndexOutsideTheAllowlist()
    {
        var backend = new FakeOpenSearchBackend(
            allowedIndices: new HashSet<string>(["product-docs-v1"], StringComparer.Ordinal));
        var tool = VectorSearchTool.Build(backend);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => tool.Handler(Arguments("""{"index":"secrets","query_embedding":[0.1]}"""), default));

        Assert.Contains("allowed_indices", exception.Message, StringComparison.Ordinal);
        Assert.Empty(backend.Searches);
    }

    [Fact]
    public async Task Handler_AllowsAnyIndexWhenTheAllowlistIsEmpty()
    {
        var backend = new FakeOpenSearchBackend();
        var tool = VectorSearchTool.Build(backend);

        await tool.Handler(Arguments("""{"index":"anything","query_embedding":[0.1]}"""), default);

        Assert.Equal("anything", Assert.Single(backend.Searches).Index);
    }

    [Fact]
    public async Task Handler_DefaultsTopKWhenNotSupplied()
    {
        var backend = new FakeOpenSearchBackend();
        var tool = VectorSearchTool.Build(backend);

        await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);

        var body = Assert.Single(backend.Searches).Body;
        Assert.Equal(VectorSearchTool.DefaultResults, body["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task Handler_CapsTopKAtTheHardLimit()
    {
        var backend = new FakeOpenSearchBackend();
        var tool = VectorSearchTool.Build(backend);

        await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1],"top_k":9999}"""), default);

        var body = Assert.Single(backend.Searches).Body;
        Assert.Equal(VectorSearchTool.MaxResults, body["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task Handler_BuildsAPlainKnnQueryWithoutFilters()
    {
        var backend = new FakeOpenSearchBackend();
        var tool = VectorSearchTool.Build(backend);

        await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1,0.2],"top_k":3}"""), default);

        var body = Assert.Single(backend.Searches).Body;
        var knn = body["query"]!["knn"]!["embedding"]!;
        Assert.Equal(3, knn["k"]!.GetValue<int>());
        Assert.Equal([0.1, 0.2], knn["vector"]!.AsArray().Select(value => value!.GetValue<double>()).ToArray());
        Assert.Null(body["query"]!["bool"]);
    }

    [Fact]
    public async Task Handler_WrapsTheKnnQueryInABoolFilterWhenFiltersAreSupplied()
    {
        var backend = new FakeOpenSearchBackend();
        var tool = VectorSearchTool.Build(backend);

        await tool.Handler(
            Arguments("""{"index":"docs","query_embedding":[0.1],"filters":{"tenant":"acme","version":2}}"""),
            default);

        var body = Assert.Single(backend.Searches).Body;
        var boolQuery = body["query"]!["bool"]!;

        Assert.NotNull(boolQuery["must"]!.AsArray()[0]!["knn"]);

        var filters = boolQuery["filter"]!.AsArray();
        Assert.Equal(2, filters.Count);
        Assert.Equal("acme", filters[0]!["term"]!["tenant"]!.GetValue<string>());
        Assert.Equal(2, filters[1]!["term"]!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task Handler_IgnoresAnEmptyFilterObject()
    {
        var backend = new FakeOpenSearchBackend();
        var tool = VectorSearchTool.Build(backend);

        await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1],"filters":{}}"""), default);

        var body = Assert.Single(backend.Searches).Body;
        Assert.NotNull(body["query"]!["knn"]);
        Assert.Null(body["query"]!["bool"]);
    }

    [Fact]
    public async Task Handler_MapsHitsToResults()
    {
        var backend = new FakeOpenSearchBackend("""
            {"hits":{"hits":[
              {"_id":"doc-1","_score":0.97,"_source":{"title":"First","rank":1}},
              {"_id":"doc-2","_score":0.55,"_source":{"title":"Second"}}
            ]}}
            """);
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);
        var json = Serialize(result);

        var hits = json.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, hits.Length);
        Assert.Equal("doc-1", hits[0].GetProperty("id").GetString());
        Assert.Equal(0.97, hits[0].GetProperty("score").GetDouble(), 3);
        Assert.Equal("First", hits[0].GetProperty("source").GetProperty("title").GetString());
        Assert.Equal(1, hits[0].GetProperty("source").GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task Handler_EmptyResponse_ReturnsNoResults()
    {
        var backend = new FakeOpenSearchBackend("""{"hits":{"hits":[]}}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);

        Assert.Empty(Serialize(result).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Handler_ResponseWithoutHits_ReturnsNoResults()
    {
        var backend = new FakeOpenSearchBackend("""{"took":3,"timed_out":false}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);

        Assert.Empty(Serialize(result).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Handler_MissingScore_IsReportedAsNull()
    {
        var backend = new FakeOpenSearchBackend("""{"hits":{"hits":[{"_id":"doc-1","_score":null,"_source":{}}]}}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);
        var hit = Serialize(result).GetProperty("results").EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Null, hit.GetProperty("score").ValueKind);
    }

    [Fact]
    public async Task Handler_NeverReturnsMoreThanTheHardCap()
    {
        var hits = string.Join(",", Enumerable.Range(0, 50)
            .Select(i => $$$"""{"_id":"doc-{{{i}}}","_score":0.5,"_source":{}}"""));
        var backend = new FakeOpenSearchBackend($$$"""{"hits":{"hits":[{{{hits}}}]}}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);

        Assert.Equal(
            VectorSearchTool.MaxResults,
            Serialize(result).GetProperty("results").EnumerateArray().Count());
    }

    [Fact]
    public async Task Handler_TruncatesOversizedStringFields()
    {
        var longBody = new string('x', VectorSearchTool.MaxSourceFieldChars + 500);
        var backend = new FakeOpenSearchBackend(
            $$$"""{"hits":{"hits":[{"_id":"doc-1","_score":1.0,"_source":{"body":"{{{longBody}}}"}}]}}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);
        var body = Serialize(result).GetProperty("results").EnumerateArray().Single()
            .GetProperty("source").GetProperty("body").GetString()!;

        Assert.EndsWith("...[truncated]", body, StringComparison.Ordinal);
        Assert.Equal(VectorSearchTool.MaxSourceFieldChars + "...[truncated]".Length, body.Length);
    }

    [Fact]
    public async Task Handler_LeavesShortStringsIntact()
    {
        var backend = new FakeOpenSearchBackend(
            """{"hits":{"hits":[{"_id":"doc-1","_score":1.0,"_source":{"body":"short"}}]}}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);

        Assert.Equal(
            "short",
            Serialize(result).GetProperty("results").EnumerateArray().Single()
                .GetProperty("source").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Handler_PreservesNestedSourceStructures()
    {
        var backend = new FakeOpenSearchBackend(
            """{"hits":{"hits":[{"_id":"doc-1","_score":1.0,"_source":{"meta":{"tags":["a","b"]}}}]}}""");
        var tool = VectorSearchTool.Build(backend);

        var result = await tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default);
        var tags = Serialize(result).GetProperty("results").EnumerateArray().Single()
            .GetProperty("source").GetProperty("meta").GetProperty("tags");

        Assert.Equal(["a", "b"], tags.EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public async Task Handler_PropagatesBackendFailures()
    {
        var backend = new FakeOpenSearchBackend { ThrowOnSearch = new ConnectorException("cluster unreachable") };
        var tool = VectorSearchTool.Build(backend);

        await Assert.ThrowsAsync<ConnectorException>(
            () => tool.Handler(Arguments("""{"index":"docs","query_embedding":[0.1]}"""), default));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(object result) =>
        JsonSerializer.SerializeToElement(result, SerializerOptions);

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}

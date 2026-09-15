using System.Text.Json;
using McpGateway.Connectors;
using McpGateway.Server;
using McpGateway.Tests.Fakes;
using Xunit;

namespace McpGateway.Tests;

public sealed class ToolArgumentValidatorTests
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "index": { "type": "string", "minLength": 1 },
            "top_k": { "type": "integer", "minimum": 1, "maximum": 20 },
            "query_embedding": { "type": "array", "items": { "type": "number" }, "minItems": 1 }
          },
          "required": ["index", "query_embedding"],
          "additionalProperties": false
        }
        """;

    private readonly ToolArgumentValidator _validator = new();
    private readonly ToolDefinition _tool = FakeConnector.CreateTool("vector_search", inputSchemaJson: Schema);

    [Fact]
    public void Validate_ValidArguments_DoesNotThrow()
    {
        _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.1,0.2],"top_k":5}"""));
    }

    [Fact]
    public void Validate_MissingRequiredProperty_Throws()
    {
        var exception = Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":"docs"}""")));

        Assert.Equal("vector_search", exception.ToolName);
        Assert.NotEmpty(exception.Violations);
        Assert.Contains("vector_search", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WrongType_Throws()
    {
        Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":42,"query_embedding":[0.1]}""")));
    }

    [Fact]
    public void Validate_ValueAboveMaximum_Throws()
    {
        Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.1],"top_k":9000}""")));
    }

    [Fact]
    public void Validate_ValueBelowMinimum_Throws()
    {
        Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.1],"top_k":0}""")));
    }

    [Fact]
    public void Validate_EmptyArrayViolatingMinItems_Throws()
    {
        Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[]}""")));
    }

    [Fact]
    public void Validate_UndeclaredProperty_Throws()
    {
        Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.1],"rogue":true}""")));
    }

    [Fact]
    public void Validate_EmptyArgumentsAgainstRequiredSchema_Throws()
    {
        Assert.Throws<ToolArgumentException>(() => _validator.Validate(_tool, Arguments("{}")));
    }

    [Fact]
    public void Validate_SchemaWithoutConstraints_AcceptsAnything()
    {
        var permissive = FakeConnector.CreateTool("anything");

        _validator.Validate(permissive, Arguments("""{"whatever":[1,2,3]}"""));
    }

    [Fact]
    public void Validate_ReusesTheCompiledSchemaAcrossCalls()
    {
        // Second call exercises the cache path; behaviour must be identical.
        _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.1]}"""));
        _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.2]}"""));

        Assert.Throws<ToolArgumentException>(() => _validator.Validate(_tool, Arguments("{}")));
    }

    [Fact]
    public void Validate_ViolationMessageNamesTheOffendingProperty()
    {
        var exception = Assert.Throws<ToolArgumentException>(
            () => _validator.Validate(_tool, Arguments("""{"index":"docs","query_embedding":[0.1],"top_k":9000}""")));

        Assert.Contains("top_k", string.Join(" ", exception.Violations), StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}

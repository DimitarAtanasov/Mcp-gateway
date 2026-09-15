using System.Text.Json;

namespace McpGateway.Connectors;

/// <summary>
/// Executes one tool call. Arguments arrive as raw JSON already validated against the tool's
/// input schema; the return value is serialized into the MCP result.
/// </summary>
public delegate Task<object> ToolHandler(
    IReadOnlyDictionary<string, JsonElement> arguments,
    CancellationToken cancellationToken);

/// <summary>
/// Describes a single tool exposed by a connector.
///
/// <see cref="Handler"/> is the last line of defense before a call reaches the backend and must
/// never trust raw values blindly: build queries through parameterized or allowlisted paths, never
/// by concatenating caller-supplied strings.
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>Creates a tool definition.</summary>
    public ToolDefinition(
        string name,
        string description,
        JsonElement inputSchema,
        JsonElement? outputSchema,
        ToolHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(handler);

        if (inputSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The input schema must be a JSON object.", nameof(inputSchema));

        Name = name;
        Description = description;
        InputSchema = inputSchema;
        OutputSchema = outputSchema;
        Handler = handler;
    }

    /// <summary>Tool name as advertised over MCP. Unique across the whole gateway.</summary>
    public string Name { get; }

    /// <summary>Human-readable description shown to the model.</summary>
    public string Description { get; }

    /// <summary>JSON Schema the arguments are validated against before <see cref="Handler"/> runs.</summary>
    public JsonElement InputSchema { get; }

    /// <summary>Optional JSON Schema describing the structured result.</summary>
    public JsonElement? OutputSchema { get; }

    /// <summary>The tool's implementation.</summary>
    public ToolHandler Handler { get; }
}

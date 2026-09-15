using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using McpGateway.Connectors;

namespace McpGateway.Server;

/// <summary>Validates tool arguments against the tool's declared input schema.</summary>
public interface IToolArgumentValidator
{
    /// <summary>Throws when <paramref name="arguments"/> violate the tool's input schema.</summary>
    /// <exception cref="ToolArgumentException">The arguments are invalid.</exception>
    void Validate(ToolDefinition tool, IReadOnlyDictionary<string, JsonElement> arguments);
}

/// <summary>
/// JSON Schema validation of tool arguments, run before any handler sees them.
///
/// Doing this centrally means every handler can trust the shape of its input: required
/// properties are present, types match, and bounds hold. Handlers stay free to enforce the
/// rules a schema cannot express, such as the per-connector index allowlist.
/// </summary>
public sealed class ToolArgumentValidator : IToolArgumentValidator
{
    private static readonly EvaluationOptions EvaluationOptions = new() { OutputFormat = OutputFormat.List };

    private readonly ConcurrentDictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Validate(ToolDefinition tool, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);

        var schema = _schemas.GetOrAdd(tool.Name, _ => JsonSchema.Build(tool.InputSchema));

        using var instance = JsonSerializer.SerializeToDocument(arguments);
        var results = schema.Evaluate(instance.RootElement, EvaluationOptions);

        if (results.IsValid)
            return;

        throw new ToolArgumentException(tool.Name, CollectViolations(results));
    }

    private static List<string> CollectViolations(EvaluationResults results)
    {
        var violations = new List<string>();
        Collect(results, violations);

        if (violations.Count == 0)
            violations.Add("the arguments do not match the tool's input schema");

        return violations;
    }

    private static void Collect(EvaluationResults node, List<string> violations)
    {
        if (node.Errors is { Count: > 0 })
        {
            var location = node.InstanceLocation.ToString();
            foreach (var error in node.Errors)
            {
                var message = error.Value ?? error.Key;
                violations.Add(string.IsNullOrEmpty(location) ? message : $"{location}: {message}");
            }
        }

        foreach (var child in node.Details ?? [])
            Collect(child, violations);
    }
}

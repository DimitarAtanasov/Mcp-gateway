namespace McpGateway.Server;

/// <summary>
/// The caller's arguments did not satisfy the tool's input schema. The message is returned to
/// the model so it can correct the call, so it lists the specific violations.
/// </summary>
public sealed class ToolArgumentException : Exception
{
    /// <summary>Creates the exception for <paramref name="toolName"/> from schema violations.</summary>
    public ToolArgumentException(string toolName, IReadOnlyList<string> violations)
        : base(BuildMessage(toolName, violations))
    {
        ToolName = toolName;
        Violations = violations;
    }

    /// <summary>Creates the exception with a prebuilt message.</summary>
    public ToolArgumentException(string message)
        : base(message)
    {
        ToolName = string.Empty;
        Violations = [];
    }

    /// <summary>Creates the exception with a prebuilt message and inner cause.</summary>
    public ToolArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
        ToolName = string.Empty;
        Violations = [];
    }

    /// <summary>Creates the exception with no message. Prefer the other overloads.</summary>
    public ToolArgumentException()
    {
        ToolName = string.Empty;
        Violations = [];
    }

    /// <summary>The tool whose schema was violated.</summary>
    public string ToolName { get; }

    /// <summary>The individual schema violations.</summary>
    public IReadOnlyList<string> Violations { get; }

    private static string BuildMessage(string toolName, IReadOnlyList<string> violations) =>
        violations.Count == 0
            ? $"Invalid arguments for tool '{toolName}'."
            : $"Invalid arguments for tool '{toolName}': {string.Join("; ", violations)}.";
}

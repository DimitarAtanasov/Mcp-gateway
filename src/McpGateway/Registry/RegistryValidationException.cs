namespace McpGateway.Registry;

/// <summary>
/// Thrown when <c>registry.yaml</c> is syntactically valid but describes something the gateway
/// cannot serve — an unknown connector, a tool no connector provides, a duplicate tool name.
/// Surfaced at startup so a typo never becomes a silently missing tool at runtime.
/// </summary>
public sealed class RegistryValidationException : Exception
{
    /// <summary>Creates the exception with a message describing the offending entry.</summary>
    public RegistryValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping an underlying parse failure.</summary>
    public RegistryValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message. Prefer the message overloads.</summary>
    public RegistryValidationException()
    {
    }
}

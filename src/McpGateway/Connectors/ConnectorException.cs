namespace McpGateway.Connectors;

/// <summary>
/// A backend call failed. The message is deliberately concise because it is surfaced to the
/// calling model; diagnostics from the backend (status codes, response bodies, endpoints) are
/// logged server-side instead of being returned over the wire.
/// </summary>
public sealed class ConnectorException : Exception
{
    /// <summary>Creates the exception with a caller-safe message.</summary>
    public ConnectorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying transport failure.</summary>
    public ConnectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message. Prefer the message overloads.</summary>
    public ConnectorException()
    {
    }
}

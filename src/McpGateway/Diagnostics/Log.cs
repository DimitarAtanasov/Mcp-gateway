using Microsoft.Extensions.Logging;

namespace McpGateway.Diagnostics;

/// <summary>
/// Source-generated log messages.
///
/// Every log statement in the gateway goes through this class so event ids and message templates
/// live in one place, and so the hot path allocates nothing when a level is disabled.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "FHIR connector ready for {Endpoint}: {TotalDocuments} document(s) indexed, {SearchableDocuments} searchable, index at {IndexPath}.")]
    public static partial void FhirConnectorReady(
        this ILogger logger, Uri endpoint, int totalDocuments, int searchableDocuments, string indexPath);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "FHIR health probe failed for {Endpoint}.")]
    public static partial void FhirHealthProbeFailed(this ILogger logger, Exception exception, Uri endpoint);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information,
        Message = "Document sync examined {Examined}, indexed {Indexed}, could not read {Unreadable}, skipped {Skipped} unchanged.")]
    public static partial void DocumentSyncCompleted(
        this ILogger logger, int examined, int indexed, int unreadable, int skipped);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
        Message = "Could not ingest document {DocumentId}; it is recorded as unreadable and the sync continues.")]
    public static partial void DocumentIngestFailed(this ILogger logger, Exception exception, string documentId);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Error,
        Message = "Document sync failed for connector {Connector}; the next run will retry.")]
    public static partial void DocumentSyncFailed(this ILogger logger, Exception exception, string connector);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning,
        Message = "Rejected call to tool {Tool} from unauthorized identity {Identity}.")]
    public static partial void UnauthorizedToolCall(this ILogger logger, string tool, string identity);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Tool {Tool} called by {Identity} with invalid arguments: {Reason}")]
    public static partial void InvalidToolArguments(
        this ILogger logger, string tool, string identity, string reason);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "Tool {Tool} invoked by {Identity}.")]
    public static partial void ToolInvoked(this ILogger logger, string tool, string identity);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "Tool {Tool} failed for {Identity}.")]
    public static partial void ToolFailed(this ILogger logger, Exception exception, string tool, string identity);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
        Message = "Tool {Tool} threw an unhandled exception for {Identity}. Error id {ErrorId}.")]
    public static partial void ToolUnhandledException(
        this ILogger logger, Exception exception, string tool, string identity, string errorId);

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information,
        Message = "Connecting {ConnectorCount} connector(s) exposing {ToolCount} tool(s): {Tools}.")]
    public static partial void ConnectingConnectors(
        this ILogger logger, int connectorCount, int toolCount, string tools);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Connector {Connector} is healthy.")]
    public static partial void ConnectorHealthy(this ILogger logger, string connector);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "Connector {Connector} connected but failed its health probe.")]
    public static partial void ConnectorUnhealthy(this ILogger logger, string connector);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information,
        Message = "Shutting down {ConnectorCount} connector(s).")]
    public static partial void ShuttingDownConnectors(this ILogger logger, int connectorCount);
}

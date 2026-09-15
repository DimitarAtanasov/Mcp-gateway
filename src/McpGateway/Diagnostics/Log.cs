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
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Acquired access token for scope {Scope}, expiring at {ExpiresOn}.")]
    public static partial void AccessTokenAcquired(this ILogger logger, string scope, DateTimeOffset expiresOn);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Failed to renew the access token for scope {Scope}; continuing with the previous token.")]
    public static partial void TokenRenewalFailed(this ILogger logger, Exception exception, string scope);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "OpenSearch connector ready for {Endpoint} with {AllowedIndexCount} allowed index(es).")]
    public static partial void OpenSearchConnectorReady(this ILogger logger, Uri endpoint, int allowedIndexCount);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "OpenSearch health probe failed for {Endpoint}.")]
    public static partial void OpenSearchHealthProbeFailed(this ILogger logger, Exception exception, Uri endpoint);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "OpenSearch search on index {Index} failed with status {StatusCode}. {DebugInformation}")]
    public static partial void OpenSearchSearchFailed(
        this ILogger logger, Exception? exception, string index, int? statusCode, string debugInformation);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error,
        Message = "OpenSearch returned a body that is not valid JSON for index {Index}.")]
    public static partial void OpenSearchUnreadableResponse(this ILogger logger, Exception exception, string index);

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

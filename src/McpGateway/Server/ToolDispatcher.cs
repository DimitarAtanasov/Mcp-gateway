using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using McpGateway.Auth;
using McpGateway.Connectors;
using McpGateway.Diagnostics;
using McpGateway.Registry;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpGateway.Server;

/// <summary>
/// Turns MCP <c>tools/list</c> and <c>tools/call</c> requests into registry lookups, authorization
/// checks and connector calls.
///
/// This is the whole policy surface of the gateway, deliberately kept free of transport and
/// hosting concerns so it can be tested directly.
/// </summary>
public sealed class ToolDispatcher
{
    private static readonly JsonSerializerOptions ResultSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ToolRegistry _registry;
    private readonly IToolArgumentValidator _validator;
    private readonly ILogger<ToolDispatcher> _logger;

    /// <summary>Creates the dispatcher.</summary>
    public ToolDispatcher(ToolRegistry registry, IToolArgumentValidator validator, ILogger<ToolDispatcher> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Advertises every registered tool with its schemas.</summary>
    public ListToolsResult ListTools() => new()
    {
        Tools = [.. _registry.Tools.Values.Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            OutputSchema = tool.OutputSchema,
        })],
    };

    /// <summary>
    /// Authorizes and executes one tool call.
    ///
    /// Protocol-level faults — no tool name, unknown tool, caller not authorized — throw
    /// <see cref="McpException"/>. Failures inside a tool come back as a result with
    /// <see cref="CallToolResult.IsError"/> set, which is what lets the model see what went
    /// wrong and correct itself.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        CallToolRequestParams? request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        var toolName = request?.Name;
        if (string.IsNullOrWhiteSpace(toolName))
            throw new McpException("A tool name is required.");

        if (!_registry.Tools.TryGetValue(toolName, out var tool))
            throw new McpException($"Unknown tool '{toolName}'.");

        var identity = CallerIdentity.FromPrincipal(user);
        if (!_registry.IsAllowed(identity, toolName))
        {
            _logger.UnauthorizedToolCall(toolName, identity.Primary);

            throw new McpException($"Identity '{identity.Primary}' is not authorized to call tool '{toolName}'.");
        }

        var arguments = (IReadOnlyDictionary<string, JsonElement>?)request?.Arguments
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        try
        {
            _validator.Validate(tool, arguments);
        }
        catch (ToolArgumentException ex)
        {
            _logger.InvalidToolArguments(toolName, identity.Primary, ex.Message);

            return ErrorResult(ex.Message);
        }

        _logger.ToolInvoked(toolName, identity.Primary);

        try
        {
            var result = await tool.Handler(arguments, cancellationToken).ConfigureAwait(false);
            return SuccessResult(result);
        }
        catch (Exception ex) when (ex is ConnectorException or ArgumentException)
        {
            // Expected, actionable failures: the message is written to be safe to show the model.
            _logger.ToolFailed(ex, toolName, identity.Primary);
            return ErrorResult(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected: log the detail, hand back a correlation id instead of internals.
            var errorId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            _logger.ToolUnhandledException(ex, toolName, identity.Primary, errorId);

            return ErrorResult($"The tool failed unexpectedly. Server error id: {errorId}.");
        }
    }

    private static CallToolResult SuccessResult(object result)
    {
        var structured = JsonSerializer.SerializeToElement(result, ResultSerializerOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}

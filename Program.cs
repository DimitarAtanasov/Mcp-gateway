// MCP Gateway entrypoint.
//
// Loads the tool registry from registry.yaml, connects all enabled
// connectors, and exposes their tools over MCP via the official C# SDK's
// server builder. Azure AI (or any MCP-compatible caller) talks only to
// this process - never directly to OpenSearch/Redis/DB.
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using McpGateway;
using McpGateway.Auth;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// "stdio" (default): local development. It's a trusted local pipe between
// this process and whatever spawned it, so there's no bearer token to
// check. "streamable-http": production, fronted by APIM or Container
// Apps built-in auth. Every call then carries a caller Azure AD token
// that must be validated - see the AddMicrosoftIdentityWebApi wiring
// below.
var transport = Environment.GetEnvironmentVariable("GATEWAY_TRANSPORT") ?? "stdio";
var registryPath = Environment.GetEnvironmentVariable("GATEWAY_REGISTRY_PATH") ?? "registry.yaml";

var tokenProvider = new ManagedIdentityTokenProvider();
var registry = ToolRegistry.FromYaml(registryPath, tokenProvider);

ValueTask<ListToolsResult> ListTools(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken) =>
    ValueTask.FromResult(new ListToolsResult
    {
        Tools = registry.Tools.Values.Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            OutputSchema = tool.OutputSchema,
        }).ToList(),
    });

async ValueTask<CallToolResult> CallTool(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
{
    var toolName = context.Params?.Name
        ?? throw new McpException("Tool name is required.");
    if (!registry.Tools.TryGetValue(toolName, out var tool))
        throw new McpException($"Unknown tool '{toolName}'.");

    var identity = ExtractIdentity(context);
    if (!registry.IsAllowed(identity, toolName))
        throw new McpException($"Identity '{identity}' is not authorized to call tool '{toolName}'.");

    Console.WriteLine($"[tool_call] tool={toolName} identity={identity}");

    var arguments = context.Params?.Arguments ?? new Dictionary<string, JsonElement>();
    var result = await tool.Handler(arguments, cancellationToken);
    return new CallToolResult
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }],
        StructuredContent = JsonSerializer.SerializeToElement(result),
    };
}

// Pulls the caller's validated identity out of the MCP request context.
//
// Under the streamable-http transport, ASP.NET Core's JWT bearer
// authentication - wired below via AddMicrosoftIdentityWebApi, which does
// real signature/issuer/audience/expiry validation against the tenant's
// AAD, nothing hand-rolled here - has already populated `context.User` by
// the time this runs. Under stdio there's no bearer token at all, so this
// stays "unknown" - every call then fails the authz check above, by
// design.
static string ExtractIdentity(MessageContext context)
{
    var user = context.User;
    if (user?.Identity?.IsAuthenticated != true)
        return "unknown";

    return user.FindFirst("appid")?.Value
        ?? user.FindFirst("azp")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "unknown";
}

static Implementation ServerInfo() => new() { Name = "azure-data-gateway", Version = "1.0.0" };

static async Task LogConnectorHealthAsync(ToolRegistry registry)
{
    var health = await registry.HealthCheckAllAsync(CancellationToken.None);
    foreach (var (name, ok) in health)
        Console.WriteLine(ok ? $"[connector_health] {name}: ok" : $"[connector_health] {name}: UNHEALTHY");
}

if (transport == "stdio")
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services
        .AddMcpServer(options => options.ServerInfo = ServerInfo())
        .WithStdioServerTransport()
        .WithListToolsHandler(ListTools)
        .WithCallToolHandler(CallTool);

    var host = builder.Build();
    await registry.ConnectAllAsync(CancellationToken.None);
    await LogConnectorHealthAsync(registry);
    await host.RunAsync();
}
else
{
    // Required only for the HTTP transport; fail fast if they're missing
    // rather than silently falling back to an unauthenticated server.
    var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID")
        ?? throw new InvalidOperationException("AZURE_TENANT_ID is required when GATEWAY_TRANSPORT=streamable-http.");
    var expectedAudience = Environment.GetEnvironmentVariable("GATEWAY_EXPECTED_AUDIENCE")
        ?? throw new InvalidOperationException("GATEWAY_EXPECTED_AUDIENCE is required when GATEWAY_TRANSPORT=streamable-http.");

    var builder = WebApplication.CreateBuilder(args);
    var host = Environment.GetEnvironmentVariable("GATEWAY_HOST") ?? "0.0.0.0";
    var port = Environment.GetEnvironmentVariable("GATEWAY_PORT") ?? "8000";
    builder.WebHost.UseUrls($"http://{host}:{port}");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(
            _ => { },
            identityOptions =>
            {
                identityOptions.Instance = "https://login.microsoftonline.com/";
                identityOptions.TenantId = tenantId;
                // Microsoft.Identity.Web derives the accepted `aud` values
                // (the app id itself and `api://{ClientId}`) from ClientId -
                // there's no separate Audience setting on this options type.
                identityOptions.ClientId = expectedAudience;
            });
    builder.Services.AddAuthorization();

    builder.Services
        .AddMcpServer(options => options.ServerInfo = ServerInfo())
        .WithHttpTransport()
        .WithListToolsHandler(ListTools)
        .WithCallToolHandler(CallTool);

    var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapMcp("/mcp").RequireAuthorization();

    await registry.ConnectAllAsync(CancellationToken.None);
    await LogConnectorHealthAsync(registry);
    await app.RunAsync();
}

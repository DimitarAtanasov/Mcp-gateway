using Azure.Core;
using Azure.Identity;
using McpGateway.Auth;
using McpGateway.Configuration;
using McpGateway.Hosting;
using McpGateway.Registry;
using McpGateway.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpGateway;

/// <summary>
/// Gateway entrypoint.
///
/// Loads the tool registry, connects the enabled connectors, and exposes their tools over MCP.
/// Azure AI (or any MCP-compatible caller) talks only to this process — never directly to
/// OpenSearch, Redis or a database.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the gateway on the transport selected by <c>GATEWAY_TRANSPORT</c>. Returns 0 on a
    /// clean shutdown and 1 when the configuration or registry is unusable, so an orchestrator
    /// sees a failed start rather than a crash dump.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(GatewayEnvironment.ToConfiguration());

            var options = ResolveOptions(configuration);

            return options.Transport == GatewayTransport.Stdio
                ? await RunStdioAsync(args, options).ConfigureAwait(false)
                : await RunHttpAsync(args, options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OptionsValidationException or RegistryValidationException)
        {
            await Console.Error.WriteLineAsync($"Gateway configuration error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static GatewayOptions ResolveOptions(ConfigurationManager configuration)
    {
        var options = new GatewayOptions();
        configuration.GetSection(GatewayOptions.SectionName).Bind(options);

        var validation = new GatewayOptionsValidator().Validate(Options.DefaultName, options);
        if (validation.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(GatewayOptions), validation.Failures);

        return options;
    }

    private static async Task<int> RunStdioAsync(string[] args, GatewayOptions options)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureSharedServices(builder.Configuration, builder.Services, builder.Logging, options);

        // stdout is the JSON-RPC channel on this transport: anything else written there corrupts
        // the protocol stream, so every log line goes to stderr.
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

        var accessor = new ServiceProviderAccessor();
        builder.Services.AddSingleton(accessor);
        builder.Services
            .AddMcpServer(server => server.ServerInfo = ServerInfo)
            .WithStdioServerTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(accessor.Dispatcher.ListTools()))
            .WithCallToolHandler((context, cancellationToken) =>
                new ValueTask<CallToolResult>(
                    accessor.Dispatcher.CallToolAsync(context.Params, context.User, cancellationToken)));

        var host = builder.Build();
        accessor.Services = host.Services;

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunHttpAsync(string[] args, GatewayOptions options)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddInMemoryCollection(GatewayEnvironment.ToConfiguration());
        ConfigureSharedServices(builder.Configuration, builder.Services, builder.Logging, options);

        builder.Logging.AddJsonConsole();
        builder.WebHost.UseUrls(options.BindUrl);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(
                jwtBearer => jwtBearer.RequireHttpsMetadata = true,
                identity =>
                {
                    identity.Instance = options.Instance;
                    identity.TenantId = options.TenantId;

                    // Microsoft.Identity.Web derives the accepted `aud` values (the app id itself
                    // and api://{ClientId}) from ClientId; there is no separate audience setting.
                    identity.ClientId = options.ExpectedAudience;
                });
        builder.Services.AddAuthorization();

        builder.Services
            .AddHealthChecks()
            .AddCheck<ConnectorsHealthCheck>("connectors", HealthStatus.Unhealthy, ["ready"]);

        var accessor = new ServiceProviderAccessor();
        builder.Services.AddSingleton(accessor);
        builder.Services
            .AddMcpServer(server => server.ServerInfo = ServerInfo)
            .WithHttpTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(accessor.Dispatcher.ListTools()))
            .WithCallToolHandler((context, cancellationToken) =>
                new ValueTask<CallToolResult>(
                    accessor.Dispatcher.CallToolAsync(context.Params, context.User, cancellationToken)));

        var app = builder.Build();
        accessor.Services = app.Services;

        app.UseAuthentication();
        app.UseAuthorization();

        // Probes stay anonymous: an orchestrator cannot present a caller token.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
            .AllowAnonymous();

        app.MapMcp(options.McpPath).RequireAuthorization();

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static Implementation ServerInfo => new()
    {
        Name = "azure-data-gateway",
        Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    };

    private static void ConfigureSharedServices(
        ConfigurationManager configuration,
        IServiceCollection services,
        ILoggingBuilder logging,
        GatewayOptions options)
    {
        logging.ClearProviders();

        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();

        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<IAccessTokenProvider, ManagedIdentityTokenProvider>();
        services.AddSingleton<IToolArgumentValidator, ToolArgumentValidator>();

        services.AddSingleton<IConnectorFactory>(provider => new ConnectorFactory(
            provider.GetRequiredService<IAccessTokenProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            options.OpenSearchScope));

        services.AddSingleton(provider => ToolRegistry.Create(
            RegistryLoader.Load(options.RegistryPath),
            provider.GetRequiredService<IConnectorFactory>()));

        services.AddSingleton<ToolDispatcher>();
        services.AddHostedService<ConnectorLifecycleService>();
    }
}

/// <summary>
/// Bridges the MCP handler delegates, which are registered while the container is still being
/// built, to the services that only exist once it is. Handlers cannot run before
/// <c>RunAsync</c>, by which point <see cref="Services"/> is set.
/// </summary>
internal sealed class ServiceProviderAccessor
{
    /// <summary>The built container.</summary>
    public IServiceProvider Services { get; set; } = null!;

    /// <summary>The dispatcher every MCP request is routed through.</summary>
    public ToolDispatcher Dispatcher => Services.GetRequiredService<ToolDispatcher>();
}

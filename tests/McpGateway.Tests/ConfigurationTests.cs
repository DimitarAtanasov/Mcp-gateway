using McpGateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpGateway.Tests;

public sealed class GatewayEnvironmentTests
{
    [Fact]
    public void ToConfiguration_MapsDocumentedVariablesOntoOptionKeys()
    {
        var values = GatewayEnvironment.ToConfiguration(Environment(new()
        {
            ["GATEWAY_TRANSPORT"] = "streamable-http",
            ["GATEWAY_REGISTRY_PATH"] = "/etc/gateway/registry.yaml",
            ["GATEWAY_EXPECTED_AUDIENCE"] = "api://gateway",
            ["GATEWAY_PORT"] = "9000",
            ["AZURE_TENANT_ID"] = "tenant-1",
        }));

        Assert.Equal("streamablehttp", values["Gateway:Transport"]);
        Assert.Equal("/etc/gateway/registry.yaml", values["Gateway:RegistryPath"]);
        Assert.Equal("api://gateway", values["Gateway:ExpectedAudience"]);
        Assert.Equal("9000", values["Gateway:Port"]);
        Assert.Equal("tenant-1", values["Gateway:TenantId"]);
    }

    [Fact]
    public void ToConfiguration_OmitsAbsentAndBlankVariablesSoDefaultsSurvive()
    {
        var values = GatewayEnvironment.ToConfiguration(Environment(new()
        {
            ["GATEWAY_TRANSPORT"] = "   ",
        }));

        Assert.Empty(values);
    }

    [Fact]
    public void ToConfiguration_TrimsValues()
    {
        var values = GatewayEnvironment.ToConfiguration(Environment(new()
        {
            ["GATEWAY_EXPECTED_AUDIENCE"] = "  api://gateway  ",
        }));

        Assert.Equal("api://gateway", values["Gateway:ExpectedAudience"]);
    }

    [Theory]
    [InlineData("streamable-http", GatewayTransport.StreamableHttp)]
    [InlineData("StreamableHttp", GatewayTransport.StreamableHttp)]
    [InlineData("STREAMABLE-HTTP", GatewayTransport.StreamableHttp)]
    [InlineData("stdio", GatewayTransport.Stdio)]
    [InlineData("Stdio", GatewayTransport.Stdio)]
    public void Transport_BindsFromBothWireAndEnumSpellings(string value, GatewayTransport expected)
    {
        var options = Bind(new() { ["GATEWAY_TRANSPORT"] = value });

        Assert.Equal(expected, options.Transport);
    }

    [Fact]
    public void Defaults_AreStdioOnPort8000()
    {
        var options = Bind([]);

        Assert.Equal(GatewayTransport.Stdio, options.Transport);
        Assert.Equal("registry.yaml", options.RegistryPath);
        Assert.Equal("http://0.0.0.0:8000", options.BindUrl);
        Assert.Equal("/mcp", options.McpPath);
    }

    private static Func<string, string?> Environment(Dictionary<string, string?> values) =>
        name => values.GetValueOrDefault(name);

    private static GatewayOptions Bind(Dictionary<string, string?> environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(GatewayEnvironment.ToConfiguration(Environment(environment)))
            .Build();

        var options = new GatewayOptions();
        configuration.GetSection(GatewayOptions.SectionName).Bind(options);
        return options;
    }
}

public sealed class GatewayOptionsValidatorTests
{
    private readonly GatewayOptionsValidator _validator = new();

    [Fact]
    public void Validate_StdioWithoutTenantDetails_Succeeds()
    {
        var result = _validator.Validate(Options.DefaultName, new GatewayOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_HttpWithoutTenantId_Fails()
    {
        var result = _validator.Validate(Options.DefaultName, new GatewayOptions
        {
            Transport = GatewayTransport.StreamableHttp,
            ExpectedAudience = "api://gateway",
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("AZURE_TENANT_ID", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_HttpWithoutAudience_Fails()
    {
        var result = _validator.Validate(Options.DefaultName, new GatewayOptions
        {
            Transport = GatewayTransport.StreamableHttp,
            TenantId = "tenant-1",
        });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("GATEWAY_EXPECTED_AUDIENCE", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_HttpWithEverythingRequired_Succeeds()
    {
        var result = _validator.Validate(Options.DefaultName, HttpOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NonHttpsInstance_Fails()
    {
        var options = HttpOptions();
        options.Instance = "http://login.microsoftonline.com/";

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("AZURE_AD_INSTANCE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Validate_PortOutOfRange_Fails(int port)
    {
        var options = HttpOptions();
        options.Port = port;

        Assert.True(_validator.Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public void Validate_McpPathWithoutLeadingSlash_Fails()
    {
        var options = HttpOptions();
        options.McpPath = "mcp";

        Assert.True(_validator.Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public void Validate_EmptyRegistryPath_Fails()
    {
        var options = new GatewayOptions { RegistryPath = "  " };

        Assert.True(_validator.Validate(Options.DefaultName, options).Failed);
    }

    [Fact]
    public void Validate_ReportsEveryFailureAtOnce()
    {
        var result = _validator.Validate(Options.DefaultName, new GatewayOptions
        {
            Transport = GatewayTransport.StreamableHttp,
            RegistryPath = string.Empty,
            Port = 0,
        });

        Assert.True(result.Failed);
        Assert.True(result.Failures!.Count() >= 4);
    }

    private static GatewayOptions HttpOptions() => new()
    {
        Transport = GatewayTransport.StreamableHttp,
        TenantId = "tenant-1",
        ExpectedAudience = "api://gateway",
    };
}

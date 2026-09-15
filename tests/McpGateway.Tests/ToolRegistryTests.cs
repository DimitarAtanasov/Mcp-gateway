using System.Security.Claims;
using McpGateway.Auth;
using McpGateway.Registry;
using McpGateway.Tests.Fakes;
using Xunit;

namespace McpGateway.Tests;

public sealed class RegistryLoaderTests
{
    [Fact]
    public void Parse_ReadsUnderscoredKeysIntoTypedEntries()
    {
        var document = RegistryLoader.Parse("""
            connectors:
              - name: opensearch
                enabled: true
                endpoint: "https://search.example"
                allowed_indices:
                  - product-docs-v1
                  - kb-articles-v1
                tools:
                  - vector_search
            authz:
              - identity: "11111111-1111-1111-1111-111111111111"
                allowed_tools:
                  - vector_search
            """);

        var connector = Assert.Single(document.Connectors);
        Assert.Equal("opensearch", connector.Name);
        Assert.True(connector.Enabled);
        Assert.Equal("https://search.example", connector.Endpoint);
        Assert.Equal(["product-docs-v1", "kb-articles-v1"], connector.AllowedIndices);
        Assert.Equal(["vector_search"], connector.Tools);

        var authz = Assert.Single(document.Authz);
        Assert.Equal("11111111-1111-1111-1111-111111111111", authz.Identity);
        Assert.Equal(["vector_search"], authz.AllowedTools);
    }

    [Fact]
    public void Parse_EmptyDocument_YieldsEmptyRegistry()
    {
        var document = RegistryLoader.Parse(string.Empty);

        Assert.Empty(document.Connectors);
        Assert.Empty(document.Authz);
    }

    [Fact]
    public void Parse_InvalidYaml_ThrowsRegistryValidationException()
    {
        var exception = Assert.Throws<RegistryValidationException>(
            () => RegistryLoader.Parse("connectors: [unclosed"));

        Assert.Contains("registry.yaml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ThrowsWithThePathItLookedAt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yaml");

        var exception = Assert.Throws<RegistryValidationException>(() => RegistryLoader.Load(path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReadsAFileFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"registry-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "connectors:\n  - name: opensearch\n    enabled: false\n");

        try
        {
            var document = RegistryLoader.Load(path);
            Assert.Single(document.Connectors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class ToolRegistryTests
{
    [Fact]
    public void Create_RegistersToolsFromEnabledConnectors()
    {
        var connector = new FakeConnector("search", "vector_search", "keyword_search");
        var registry = ToolRegistry.Create(
            Document(Connector("search", enabled: true, tools: ["vector_search"])),
            new FakeConnectorFactory(connector));

        Assert.Equal(["vector_search"], registry.Tools.Keys);
        Assert.Same(connector, Assert.Single(registry.Connectors).Value);
    }

    [Fact]
    public void Create_SkipsDisabledConnectors()
    {
        var registry = ToolRegistry.Create(
            Document(Connector("search", enabled: false, tools: ["vector_search"])),
            new FakeConnectorFactory(new FakeConnector("search", "vector_search")));

        Assert.Empty(registry.Connectors);
        Assert.Empty(registry.Tools);
    }

    [Fact]
    public void Create_UnknownConnector_Throws()
    {
        var exception = Assert.Throws<RegistryValidationException>(() => ToolRegistry.Create(
            Document(Connector("redis", enabled: true, tools: [])),
            new FakeConnectorFactory(new FakeConnector("opensearch"))));

        Assert.Contains("Unknown connector 'redis'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ToolTheConnectorDoesNotProvide_Throws()
    {
        // A typo in registry.yaml must fail startup rather than silently drop the tool.
        var exception = Assert.Throws<RegistryValidationException>(() => ToolRegistry.Create(
            Document(Connector("search", enabled: true, tools: ["vectro_search"])),
            new FakeConnectorFactory(new FakeConnector("search", "vector_search"))));

        Assert.Contains("does not provide a tool named 'vectro_search'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_DuplicateToolAcrossConnectors_Throws()
    {
        var document = Document(
            Connector("search", enabled: true, tools: ["shared_tool"]),
            Connector("cache", enabled: true, tools: ["shared_tool"]));

        var exception = Assert.Throws<RegistryValidationException>(() => ToolRegistry.Create(
            document,
            new FakeConnectorFactory(
                new FakeConnector("search", "shared_tool"),
                new FakeConnector("cache", "shared_tool"))));

        Assert.Contains("Duplicate tool name 'shared_tool'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ConnectorListedTwice_Throws()
    {
        var document = Document(
            Connector("search", enabled: true, tools: []),
            Connector("search", enabled: true, tools: []));

        Assert.Throws<RegistryValidationException>(() => ToolRegistry.Create(
            document,
            new FakeConnectorFactory(new FakeConnector("search"))));
    }

    [Fact]
    public void Create_AuthzReferencingAnUnservedTool_Throws()
    {
        var document = Document(Connector("search", enabled: true, tools: ["vector_search"]));
        document.Authz.Add(new AuthzEntry { Identity = "app-1", AllowedTools = ["nope"] });

        var exception = Assert.Throws<RegistryValidationException>(() => ToolRegistry.Create(
            document,
            new FakeConnectorFactory(new FakeConnector("search", "vector_search"))));

        Assert.Contains("which no enabled connector exposes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_DuplicateIdentity_Throws()
    {
        var document = Document(Connector("search", enabled: true, tools: ["vector_search"]));
        document.Authz.Add(new AuthzEntry { Identity = "app-1", AllowedTools = ["vector_search"] });
        document.Authz.Add(new AuthzEntry { Identity = "app-1", AllowedTools = [] });

        Assert.Throws<RegistryValidationException>(() => ToolRegistry.Create(
            document,
            new FakeConnectorFactory(new FakeConnector("search", "vector_search"))));
    }

    [Fact]
    public void IsAllowed_ListedIdentityAndTool_IsTrue()
    {
        var registry = BuildRegistryAllowing("app-1", "vector_search");

        Assert.True(registry.IsAllowed(Identity(("appid", "app-1")), "vector_search"));
    }

    [Fact]
    public void IsAllowed_UnlistedTool_IsFalse()
    {
        var document = Document(Connector("search", enabled: true, tools: ["vector_search", "keyword_search"]));
        document.Authz.Add(new AuthzEntry { Identity = "app-1", AllowedTools = ["vector_search"] });
        var registry = ToolRegistry.Create(
            document,
            new FakeConnectorFactory(new FakeConnector("search", "vector_search", "keyword_search")));

        Assert.False(registry.IsAllowed(Identity(("appid", "app-1")), "keyword_search"));
    }

    [Fact]
    public void IsAllowed_UnknownIdentity_IsFalse()
    {
        var registry = BuildRegistryAllowing("app-1", "vector_search");

        Assert.False(registry.IsAllowed(Identity(("appid", "app-2")), "vector_search"));
    }

    [Fact]
    public void IsAllowed_UnauthenticatedCaller_IsFalse()
    {
        var registry = BuildRegistryAllowing("app-1", "vector_search");

        Assert.False(registry.IsAllowed(CallerIdentity.Unknown, "vector_search"));
    }

    [Fact]
    public void IsAllowed_MatchesOnObjectIdWhenTheAllowlistUsesIt()
    {
        var registry = BuildRegistryAllowing("object-id", "vector_search");
        var identity = Identity(("appid", "app-1"), ("oid", "object-id"));

        Assert.True(registry.IsAllowed(identity, "vector_search"));
    }

    [Fact]
    public void IsAllowed_EmptyToolName_IsFalse()
    {
        var registry = BuildRegistryAllowing("app-1", "vector_search");

        Assert.False(registry.IsAllowed(Identity(("appid", "app-1")), string.Empty));
    }

    [Fact]
    public async Task ConnectAllAsync_ConnectsEveryConnector()
    {
        var connector = new FakeConnector("search", "vector_search");
        var registry = ToolRegistry.Create(
            Document(Connector("search", enabled: true, tools: ["vector_search"])),
            new FakeConnectorFactory(connector));

        await registry.ConnectAllAsync(CancellationToken.None);

        Assert.Equal(1, connector.ConnectCount);
    }

    [Fact]
    public async Task ConnectAllAsync_PropagatesFailuresSoStartupFails()
    {
        var connector = new FakeConnector("search", "vector_search")
        {
            ThrowOnConnect = new InvalidOperationException("no credentials"),
        };
        var registry = ToolRegistry.Create(
            Document(Connector("search", enabled: true, tools: ["vector_search"])),
            new FakeConnectorFactory(connector));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ConnectAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthCheckAllAsync_ReportsPerConnectorStatus()
    {
        var healthy = new FakeConnector("search", "vector_search") { Healthy = true };
        var unhealthy = new FakeConnector("cache", "cache_get") { Healthy = false };

        var registry = ToolRegistry.Create(
            Document(
                Connector("search", enabled: true, tools: ["vector_search"]),
                Connector("cache", enabled: true, tools: ["cache_get"])),
            new FakeConnectorFactory(healthy, unhealthy));

        var health = await registry.HealthCheckAllAsync(CancellationToken.None);

        Assert.True(health["search"]);
        Assert.False(health["cache"]);
    }

    [Fact]
    public async Task HealthCheckAllAsync_TreatsAThrownProbeAsUnhealthy()
    {
        var connector = new FakeConnector("search", "vector_search")
        {
            ThrowOnHealthCheck = new HttpRequestException("connection refused"),
        };
        var registry = ToolRegistry.Create(
            Document(Connector("search", enabled: true, tools: ["vector_search"])),
            new FakeConnectorFactory(connector));

        var health = await registry.HealthCheckAllAsync(CancellationToken.None);

        Assert.False(health["search"]);
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryConnector()
    {
        var connector = new FakeConnector("search", "vector_search");
        var registry = ToolRegistry.Create(
            Document(Connector("search", enabled: true, tools: ["vector_search"])),
            new FakeConnectorFactory(connector));

        await registry.DisposeAsync();

        Assert.Equal(1, connector.DisposeCount);
        Assert.Empty(registry.Connectors);
    }

    private static ToolRegistry BuildRegistryAllowing(string identity, string toolName)
    {
        var document = Document(Connector("search", enabled: true, tools: [toolName]));
        document.Authz.Add(new AuthzEntry { Identity = identity, AllowedTools = [toolName] });
        return ToolRegistry.Create(document, new FakeConnectorFactory(new FakeConnector("search", toolName)));
    }

    private static CallerIdentity Identity(params (string Type, string Value)[] claims) =>
        CallerIdentity.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)),
            authenticationType: "Bearer")));

    private static RegistryDocument Document(params ConnectorEntry[] connectors) =>
        new() { Connectors = [.. connectors] };

    private static ConnectorEntry Connector(string name, bool enabled, string[] tools) =>
        new() { Name = name, Enabled = enabled, Tools = [.. tools], Endpoint = "https://backend.example" };
}

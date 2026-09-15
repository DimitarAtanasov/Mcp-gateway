using Azure.Core;
using McpGateway.Auth;
using McpGateway.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenSearch.Net;
using Xunit;

namespace McpGateway.Tests;

public sealed class ManagedIdentityTokenProviderTests
{
    private const string Scope = "https://opensearch.azure.com/.default";

    [Fact]
    public async Task GetTokenAsync_FetchesOnFirstCall()
    {
        var time = new FakeTimeProvider();
        var credential = new FakeTokenCredential(call => Token($"token-{call}", time.GetUtcNow().AddHours(1)));
        using var provider = new ManagedIdentityTokenProvider(credential, NullLogger<ManagedIdentityTokenProvider>.Instance, time);

        var token = await provider.GetTokenAsync(Scope, CancellationToken.None);

        Assert.Equal("token-1", token);
        Assert.Equal(Scope, Assert.Single(credential.RequestedScopes));
    }

    [Fact]
    public async Task GetTokenAsync_ServesACachedTokenWhileItIsFresh()
    {
        var time = new FakeTimeProvider();
        var credential = new FakeTokenCredential(call => Token($"token-{call}", time.GetUtcNow().AddHours(1)));
        using var provider = new ManagedIdentityTokenProvider(credential, NullLogger<ManagedIdentityTokenProvider>.Instance, time);

        await provider.GetTokenAsync(Scope, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.GetTokenAsync(Scope, CancellationToken.None);

        Assert.Equal("token-1", second);
        Assert.Equal(1, credential.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_RenewsInsideTheRenewalWindow()
    {
        var time = new FakeTimeProvider();
        var credential = new FakeTokenCredential(call => Token($"token-{call}", time.GetUtcNow().AddHours(1)));
        using var provider = new ManagedIdentityTokenProvider(credential, NullLogger<ManagedIdentityTokenProvider>.Instance, time);

        await provider.GetTokenAsync(Scope, CancellationToken.None);

        // Move to within the renewal window of the first token's expiry.
        time.Advance(TimeSpan.FromHours(1) - ManagedIdentityTokenProvider.RenewalWindow + TimeSpan.FromSeconds(1));
        var renewed = await provider.GetTokenAsync(Scope, CancellationToken.None);

        Assert.Equal("token-2", renewed);
        Assert.Equal(2, credential.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_CachesPerScope()
    {
        var time = new FakeTimeProvider();
        var credential = new FakeTokenCredential(call => Token($"token-{call}", time.GetUtcNow().AddHours(1)));
        using var provider = new ManagedIdentityTokenProvider(credential, NullLogger<ManagedIdentityTokenProvider>.Instance, time);

        var first = await provider.GetTokenAsync(Scope, CancellationToken.None);
        var second = await provider.GetTokenAsync("api://other/.default", CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(2, credential.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_RejectsABlankScope()
    {
        var credential = new FakeTokenCredential(_ => Token("t", DateTimeOffset.UtcNow.AddHours(1)));
        using var provider = new ManagedIdentityTokenProvider(credential, NullLogger<ManagedIdentityTokenProvider>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await provider.GetTokenAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task GetTokenAsync_ConcurrentCallersShareOneFetch()
    {
        var time = new FakeTimeProvider();
        var credential = new FakeTokenCredential(call => Token($"token-{call}", time.GetUtcNow().AddHours(1)));
        using var provider = new ManagedIdentityTokenProvider(credential, NullLogger<ManagedIdentityTokenProvider>.Instance, time);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => provider.GetTokenAsync(Scope, CancellationToken.None).AsTask()));

        Assert.All(tokens, token => Assert.Equal("token-1", token));
        Assert.Equal(1, credential.CallCount);
    }

    private static AccessToken Token(string value, DateTimeOffset expiresOn) => new(value, expiresOn);
}

public sealed class AadAuthConnectionTests
{
    private const string Scope = "https://opensearch.azure.com/.default";

    [Fact]
    public async Task Request_BeforeInitialize_Throws()
    {
        var transport = new RecordingConnection();
        await using var connection = new AadAuthConnection(
            new FakeTokenProvider(), Scope, NullLogger<AadAuthConnection>.Instance, transport);

        Assert.Throws<InvalidOperationException>(
            () => connection.Request<StringResponse>(RequestData(connection)));
    }

    [Fact]
    public async Task InitializeAsync_FetchesTheFirstTokenEagerly()
    {
        var tokenProvider = new FakeTokenProvider();
        await using var connection = new AadAuthConnection(
            tokenProvider, Scope, NullLogger<AadAuthConnection>.Instance, new RecordingConnection());

        await connection.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, tokenProvider.CallCount);
    }

    [Fact]
    public async Task InitializeAsync_PropagatesTheFirstFetchFailure()
    {
        var tokenProvider = new FakeTokenProvider { ThrowOnNextCall = new UnauthorizedAccessException("denied") };
        await using var connection = new AadAuthConnection(
            tokenProvider, Scope, NullLogger<AadAuthConnection>.Instance, new RecordingConnection());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => connection.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestAsync_StampsTheCurrentBearerToken()
    {
        var transport = new RecordingConnection();
        await using var connection = new AadAuthConnection(
            new FakeTokenProvider(), Scope, NullLogger<AadAuthConnection>.Instance, transport);
        await connection.InitializeAsync(CancellationToken.None);

        await connection.RequestAsync<StringResponse>(RequestData(connection), CancellationToken.None);

        Assert.Equal("Bearer token-1", Assert.Single(transport.AuthorizationHeaders));
    }

    [Fact]
    public async Task Request_SynchronousPathAlsoStampsTheToken()
    {
        var transport = new RecordingConnection();
        await using var connection = new AadAuthConnection(
            new FakeTokenProvider(), Scope, NullLogger<AadAuthConnection>.Instance, transport);
        await connection.InitializeAsync(CancellationToken.None);

        connection.Request<StringResponse>(RequestData(connection));

        Assert.Equal("Bearer token-1", Assert.Single(transport.AuthorizationHeaders));
    }

    [Fact]
    public async Task RefreshLoop_RenewsTheTokenOnTheConfiguredInterval()
    {
        var time = new FakeTimeProvider();
        var tokenProvider = new FakeTokenProvider();
        var transport = new RecordingConnection();
        await using var connection = new AadAuthConnection(
            tokenProvider,
            Scope,
            NullLogger<AadAuthConnection>.Instance,
            transport,
            TimeSpan.FromMinutes(5),
            time);

        await connection.InitializeAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => tokenProvider.CallCount >= 2);

        await connection.RequestAsync<StringResponse>(RequestData(connection), CancellationToken.None);

        Assert.Equal("Bearer token-2", Assert.Single(transport.AuthorizationHeaders));
    }

    [Fact]
    public async Task RefreshLoop_KeepsTheLastGoodTokenWhenRenewalFails()
    {
        var time = new FakeTimeProvider();
        var tokenProvider = new FakeTokenProvider();
        var transport = new RecordingConnection();
        await using var connection = new AadAuthConnection(
            tokenProvider,
            Scope,
            NullLogger<AadAuthConnection>.Instance,
            transport,
            TimeSpan.FromMinutes(5),
            time);

        await connection.InitializeAsync(CancellationToken.None);
        tokenProvider.ThrowOnNextCall = new HttpRequestException("IMDS unreachable");
        time.Advance(TimeSpan.FromMinutes(5));
        await WaitForAsync(() => tokenProvider.CallCount >= 2);

        await connection.RequestAsync<StringResponse>(RequestData(connection), CancellationToken.None);

        Assert.Equal("Bearer token-1", Assert.Single(transport.AuthorizationHeaders));
    }

    [Fact]
    public async Task DisposeAsync_StopsTheRenewalLoop()
    {
        var time = new FakeTimeProvider();
        var tokenProvider = new FakeTokenProvider();
        var connection = new AadAuthConnection(
            tokenProvider,
            Scope,
            NullLogger<AadAuthConnection>.Instance,
            new RecordingConnection(),
            TimeSpan.FromMinutes(5),
            time);

        await connection.InitializeAsync(CancellationToken.None);
        await connection.DisposeAsync();

        var callsAtShutdown = tokenProvider.CallCount;
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, CancellationToken.None);

        Assert.Equal(callsAtShutdown, tokenProvider.CallCount);
    }

    [Fact]
    public async Task DisposeAsync_IsSafeToCallTwice()
    {
        var connection = new AadAuthConnection(
            new FakeTokenProvider(), Scope, NullLogger<AadAuthConnection>.Instance, new RecordingConnection());
        await connection.InitializeAsync(CancellationToken.None);

        await connection.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public void Constructor_RejectsABlankScope()
    {
        Assert.Throws<ArgumentException>(() => new AadAuthConnection(
            new FakeTokenProvider(), "  ", NullLogger<AadAuthConnection>.Instance, new RecordingConnection()));
    }

    private static RequestData RequestData(IConnection connection)
    {
        var settings = new ConnectionConfiguration(new Uri("https://search.example"), connection);
        return new RequestData(
            OpenSearch.Net.HttpMethod.GET,
            "/_search",
            PostData.String("{}"),
            settings,
            local: null,
            RecyclableMemoryStreamFactory.Default);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10, CancellationToken.None);

        Assert.True(condition(), "Condition was not met before the timeout.");
    }
}

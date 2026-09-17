using System.Net;
using System.Text;
using System.Text.Json;
using McpGateway.Connectors.Fhir;

namespace McpGateway.Tests.Fakes;

/// <summary>Serves canned FHIR bundles and attachments, recording what was asked for.</summary>
internal sealed class FakeFhirApi : IFhirApi
{
    private readonly Queue<FhirSearchPage> _pages = new();
    private readonly Dictionary<string, (byte[] Content, string? MediaType)> _attachments = new(StringComparer.Ordinal);

    public bool IsHealthy { get; set; } = true;

    public List<IReadOnlyDictionary<string, string>> SearchRequests { get; } = [];

    public List<string> NextPageRequests { get; } = [];

    public List<string> AttachmentRequests { get; } = [];

    public Exception? ThrowOnAttachment { get; set; }

    public FakeFhirApi AddPage(string bundleJson)
    {
        using var document = JsonDocument.Parse(bundleJson);
        _pages.Enqueue(FhirApi.ParseBundle(document.RootElement));
        return this;
    }

    public FakeFhirApi AddAttachment(string url, string content, string mediaType = "text/plain")
    {
        _attachments[url] = (Encoding.UTF8.GetBytes(content), mediaType);
        return this;
    }

    public FakeFhirApi AddAttachmentBytes(string url, byte[] content, string mediaType)
    {
        _attachments[url] = (content, mediaType);
        return this;
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(IsHealthy);

    public Task<FhirSearchPage> SearchAsync(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        SearchRequests.Add(parameters);
        return Task.FromResult(NextPage());
    }

    public Task<FhirSearchPage> SearchNextAsync(string nextPageUrl, CancellationToken cancellationToken)
    {
        NextPageRequests.Add(nextPageUrl);
        return Task.FromResult(NextPage());
    }

    public Task<(ReadOnlyMemory<byte> Content, string? MediaType)?> FetchAttachmentAsync(
        string url,
        CancellationToken cancellationToken)
    {
        AttachmentRequests.Add(url);

        if (ThrowOnAttachment is { } failure)
            throw failure;

        if (!_attachments.TryGetValue(url, out var attachment))
            return Task.FromResult<(ReadOnlyMemory<byte>, string?)?>(null);

        return Task.FromResult<(ReadOnlyMemory<byte>, string?)?>(
            (new ReadOnlyMemory<byte>(attachment.Content), attachment.MediaType));
    }

    private FhirSearchPage NextPage() =>
        _pages.Count > 0 ? _pages.Dequeue() : new FhirSearchPage([], null);
}

/// <summary>Answers HTTP requests from a scripted map, recording headers so auth can be asserted.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    public static FakeHttpMessageHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
        });

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> AuthorizationHeaders { get; } = [];

    public List<string?> ApiKeyHeaders { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
        ApiKeyHeaders.Add(request.Headers.TryGetValues("X-API-Key", out var values)
            ? string.Join(",", values)
            : null);

        return Task.FromResult(_responder(request));
    }
}

/// <summary>Bundle and resource JSON used across the FHIR tests.</summary>
internal static class FhirSamples
{
    public static string Bundle(string? nextUrl, params string[] resources)
    {
        var entries = string.Join(",", resources.Select(resource => "{\"resource\":" + resource + "}"));
        var link = nextUrl is null
            ? "[]"
            : "[{\"relation\":\"next\",\"url\":\"" + nextUrl + "\"}]";

        return "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"link\":" + link +
               ",\"entry\":[" + entries + "]}";
    }

    public static string DocumentReference(
        string id,
        string? versionId = "1",
        string? lastUpdated = "2026-01-15T10:00:00Z",
        string? description = "Discharge summary",
        string? subject = "Patient/123",
        string? contentType = "text/plain",
        string? attachmentUrl = "Binary/att-1",
        string? inlineBase64 = null)
    {
        var metaFields = new List<string>();
        if (versionId is not null)
            metaFields.Add("\"versionId\":\"" + versionId + "\"");
        if (lastUpdated is not null)
            metaFields.Add("\"lastUpdated\":\"" + lastUpdated + "\"");

        var meta = metaFields.Count == 0 ? "" : "\"meta\":{" + string.Join(",", metaFields) + "},";

        var attachment = new List<string>();
        if (contentType is not null)
            attachment.Add($"\"contentType\":\"{contentType}\"");
        if (attachmentUrl is not null)
            attachment.Add($"\"url\":\"{attachmentUrl}\"");
        if (inlineBase64 is not null)
            attachment.Add($"\"data\":\"{inlineBase64}\"");

        var descriptionJson = description is null ? "" : $"\"description\":\"{description}\",";
        var subjectJson = subject is null ? "" : "\"subject\":{\"reference\":\"" + subject + "\"},";
        var attachmentJson = "\"content\":[{\"attachment\":{" + string.Join(",", attachment) + "}}]";

        return "{\"resourceType\":\"DocumentReference\",\"id\":\"" + id + "\"," +
               meta +
               descriptionJson +
               subjectJson +
               "\"type\":{\"coding\":[{\"code\":\"18842-5\",\"display\":\"Discharge summary\"}]}," +
               "\"date\":\"2026-01-14T09:00:00Z\"," +
               attachmentJson + "}";
    }
}

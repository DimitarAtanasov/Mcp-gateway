using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace McpGateway.Connectors.Fhir;

/// <summary>One page of a FHIR search, plus the link that continues it.</summary>
/// <param name="Entries">The raw resources on this page.</param>
/// <param name="NextPageUrl">Absolute URL of the next page, or null on the last page.</param>
public sealed record FhirSearchPage(IReadOnlyList<JsonElement> Entries, string? NextPageUrl);

/// <summary>
/// The slice of the FHIR REST API this gateway uses. Abstracted so ingest can be tested against
/// canned bundles instead of a live server holding real patient data.
/// </summary>
public interface IFhirApi
{
    /// <summary>Reads the server's CapabilityStatement, used as a liveness probe.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken);

    /// <summary>Searches a resource type, returning the first page.</summary>
    Task<FhirSearchPage> SearchAsync(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);

    /// <summary>Follows a Bundle's <c>next</c> link.</summary>
    Task<FhirSearchPage> SearchNextAsync(string nextPageUrl, CancellationToken cancellationToken);

    /// <summary>Fetches attachment bytes from a Binary resource or an attachment URL.</summary>
    /// <returns>The bytes and the media type the server reported, or null when not retrievable.</returns>
    Task<(ReadOnlyMemory<byte> Content, string? MediaType)?> FetchAttachmentAsync(
        string url,
        CancellationToken cancellationToken);
}

/// <summary>
/// Talks FHIR REST over plain HTTP and JSON.
///
/// The gateway reads a narrow, stable slice of DocumentReference, so a full FHIR model library
/// would be a large dependency for eight fields — and reading the JSON directly keeps this
/// working across R4, R4B and R5, where those fields are unchanged.
/// </summary>
public sealed class FhirApi : IFhirApi
{
    private const string FhirJsonMediaType = "application/fhir+json";

    private readonly HttpClient _httpClient;
    private readonly long _maxAttachmentBytes;

    /// <summary>Creates the client over an <see cref="HttpClient"/> already carrying auth and base address.</summary>
    public FhirApi(HttpClient httpClient, long maxAttachmentBytes)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _maxAttachmentBytes = maxAttachmentBytes;
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "metadata");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonMediaType));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public Task<FhirSearchPage> SearchAsync(
        string resourceType,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentNullException.ThrowIfNull(parameters);

        var query = string.Join("&", parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

        var path = query.Length == 0 ? resourceType : $"{resourceType}?{query}";
        return ReadPageAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FhirSearchPage> SearchNextAsync(string nextPageUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextPageUrl);
        return ReadPageAsync(nextPageUrl, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(ReadOnlyMemory<byte> Content, string? MediaType)?> FetchAttachmentAsync(
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Ask for the raw bytes; a FHIR server hands back a Binary resource otherwise.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectorException(
                $"The FHIR server returned {(int)response.StatusCode} fetching an attachment.");
        }

        if (response.Content.Headers.ContentLength > _maxAttachmentBytes)
            return null;

        var mediaType = response.Content.Headers.ContentType?.ToString();
        var bytes = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;

        // A FHIR server may answer a Binary read with the wrapped resource rather than raw bytes.
        if (mediaType is not null && mediaType.Contains("fhir+json", StringComparison.OrdinalIgnoreCase))
            return UnwrapBinaryResource(bytes.Value);

        return (bytes.Value, mediaType);
    }

    private async Task<FhirSearchPage> ReadPageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJsonMediaType));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectorException(
                $"The FHIR server returned {(int)response.StatusCode} for a search request.");
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(payload);
            return ParseBundle(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException("The FHIR server returned a search result that is not valid JSON.", ex);
        }
    }

    /// <summary>Pulls the resources and the next link out of a searchset Bundle.</summary>
    internal static FhirSearchPage ParseBundle(JsonElement bundle)
    {
        var entries = new List<JsonElement>();
        if (bundle.TryGetProperty("entry", out var entryArray) && entryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entryArray.EnumerateArray())
            {
                if (entry.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
                    entries.Add(resource.Clone());
            }
        }

        string? next = null;
        if (bundle.TryGetProperty("link", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("relation", out var relation) &&
                    relation.ValueKind == JsonValueKind.String &&
                    string.Equals(relation.GetString(), "next", StringComparison.OrdinalIgnoreCase) &&
                    link.TryGetProperty("url", out var url) &&
                    url.ValueKind == JsonValueKind.String)
                {
                    next = url.GetString();
                    break;
                }
            }
        }

        return new FhirSearchPage(entries, next);
    }

    private async Task<ReadOnlyMemory<byte>?> ReadCappedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            // Servers do not always send Content-Length, so the cap is also enforced while reading.
            if (buffer.Length + read > _maxAttachmentBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static (ReadOnlyMemory<byte> Content, string? MediaType)? UnwrapBinaryResource(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                return null;

            var mediaType = root.TryGetProperty("contentType", out var contentType) &&
                            contentType.ValueKind == JsonValueKind.String
                ? contentType.GetString()
                : null;

            return (Convert.FromBase64String(data.GetString()!), mediaType);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Formats an instant the way FHIR search prefixes expect.</summary>
    internal static string FormatInstant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

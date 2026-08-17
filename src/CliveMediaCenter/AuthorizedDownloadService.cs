using System.Net.Http.Headers;
using System.Text.Json;

namespace CliveMediaCenter;

/// <summary>
/// Downloads a user-supplied, authorized HTTP(S) link. Real-Debrid is used only
/// to resolve that link through its official unrestrict endpoint; this class has
/// no index search, torrent discovery, or magnet handling.
/// </summary>
internal sealed class AuthorizedDownloadService
{
    private static readonly Uri RealDebridUnrestrictEndpoint = new("https://api.real-debrid.com/rest/1.0/unrestrict/link");
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<string> ResolveAsync(string sourceUrl, string realDebridToken, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Only direct HTTP(S) links are supported.");
        if (sourceUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Magnet links are not supported by the authorized-link downloader.");
        if (string.IsNullOrWhiteSpace(realDebridToken)) return sourceUrl;

        using var request = new HttpRequestMessage(HttpMethod.Post, RealDebridUnrestrictEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", realDebridToken.Trim());
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["link"] = sourceUrl });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Real-Debrid rejected the link (HTTP {(int)response.StatusCode}).");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("download", out var download) || string.IsNullOrWhiteSpace(download.GetString()))
            throw new InvalidOperationException("Real-Debrid returned no downloadable URL.");
        return download.GetString()!;
    }

    public async Task DownloadAsync(string url, string destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            progress?.Report(total is > 0 ? written * 100 / total.Value : written);
        }
    }
}

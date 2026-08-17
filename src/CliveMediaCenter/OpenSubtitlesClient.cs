using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CliveMediaCenter;

/// <summary>
/// Small client for the official OpenSubtitles.com REST API. It deliberately sends only parsed
/// title metadata; media files never leave the user's network.
/// </summary>
internal sealed class OpenSubtitlesClient : ISubtitleProvider
{
    private const string ApiRoot = "https://api.opensubtitles.com/api/v1";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly AppSettings settings;
    private string? bearerToken;
    public string LastError { get; private set; } = "";
    public bool LastFailureWasServiceError { get; private set; }
    public string Name => "OpenSubtitles";

    public OpenSubtitlesClient(AppSettings settings)
    {
        this.settings = settings;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HighSeasMedia/1.0");
        http.DefaultRequestHeaders.Add("Api-Key", settings.OpenSubtitlesApiKey);
    }

    public async Task<bool> DownloadEnglishSubtitleAsync(MediaItem media, CancellationToken cancellationToken)
    {
        LastError = "";
        LastFailureWasServiceError = false;
        if (string.IsNullOrWhiteSpace(settings.OpenSubtitlesApiKey)) { LastError = "OpenSubtitles API key is missing."; return false; }
        await EnsureLoginAsync(cancellationToken);

        var query = media.Type == "Show" && media.Series.Length > 0 ? media.Series : media.Title;
        var parameters = new Dictionary<string, string>
        {
            ["languages"] = "en",
            ["order_by"] = "download_count",
            ["order_direction"] = "desc",
            ["query"] = query
        };
        if (media.Year.Length == 4) parameters["year"] = media.Year;
        if (media.Type == "Show" && media.SeasonNumber > 0)
        {
            parameters["season_number"] = media.SeasonNumber.ToString();
            parameters["episode_number"] = media.EpisodeNumber.ToString();
        }

        var url = ApiRoot + "/subtitles?" + string.Join("&", parameters.OrderBy(x => x.Key).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        using var search = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!search.IsSuccessStatusCode)
        {
            LastFailureWasServiceError = (int)search.StatusCode >= 500 || (int)search.StatusCode is 401 or 403 or 429;
            LastError = $"Search returned HTTP {(int)search.StatusCode} ({search.ReasonPhrase}).";
            return false;
        }
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStreamAsync(cancellationToken));
        var fileIds = GetCandidateFileIds(searchJson.RootElement).Take(8).ToList();
        if (fileIds.Count == 0) { LastError = "No ranked English subtitle matched this episode."; return false; }

        foreach (var fileId in fileIds)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var download = await SendAsync(HttpMethod.Post, ApiRoot + "/download", new { file_id = fileId }, cancellationToken);
                if (download.IsSuccessStatusCode)
                {
                    using var downloadJson = JsonDocument.Parse(await download.Content.ReadAsStreamAsync(cancellationToken));
                    if (!downloadJson.RootElement.TryGetProperty("link", out var linkNode) || string.IsNullOrWhiteSpace(linkNode.GetString())) break;
                    try
                    {
                        var subtitleBytes = await http.GetByteArrayAsync(linkNode.GetString()!, cancellationToken);
                        if (subtitleBytes.Length == 0) break;
                        var destination = Path.ChangeExtension(media.FullPath, ".srt");
                        await File.WriteAllBytesAsync(destination, subtitleBytes, cancellationToken);
                        LastError = "";
                        LastFailureWasServiceError = false;
                        return true;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    {
                        LastError = $"Subtitle file transfer failed: {exception.Message}";
                        LastFailureWasServiceError = true;
                        break;
                    }
                }

                var status = (int)download.StatusCode;
                var transient = status == 429 || status >= 500;
                LastError = $"Download ticket returned HTTP {status} ({download.ReasonPhrase}).";
                LastFailureWasServiceError = transient || status is 401 or 403;
                if (!transient) break;
                var retryDelay = download.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(700 * (attempt + 1));
                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return false;
    }

    private async Task EnsureLoginAsync(CancellationToken cancellationToken)
    {
        if (bearerToken != null || string.IsNullOrWhiteSpace(settings.OpenSubtitlesUsername) || string.IsNullOrWhiteSpace(settings.OpenSubtitlesPassword)) return;
        using var response = await SendAsync(HttpMethod.Post, ApiRoot + "/login", new { username = settings.OpenSubtitlesUsername, password = settings.OpenSubtitlesPassword }, cancellationToken, includeBearer: false);
        if (!response.IsSuccessStatusCode) return;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        bearerToken = json.RootElement.TryGetProperty("token", out var tokenNode) ? tokenNode.GetString() : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken, bool includeBearer = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = JsonContent.Create(body);
        if (includeBearer && bearerToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static IEnumerable<int> GetCandidateFileIds(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        var seen = new HashSet<int>();
        foreach (var result in data.EnumerateArray())
        {
            if (!result.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (file.TryGetProperty("file_id", out var id) && id.TryGetInt32(out var fileId) && seen.Add(fileId)) yield return fileId;
            }
        }
    }
}

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CliveMediaCenter;

/// <summary>
/// SubDL's documented search/download API. It is deliberately opt-in because SubDL issues
/// personal API keys and applies its own request/download quotas.
/// </summary>
internal sealed class SubdlClient : ISubtitleProvider
{
    private const string SearchEndpoint = "https://api.subdl.com/api/v1/subtitles";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(35) };
    private readonly string apiKey;

    public string Name => "SubDL";
    public string LastError { get; private set; } = "";
    public bool LastFailureWasServiceError { get; private set; }

    public SubdlClient(string apiKey)
    {
        this.apiKey = apiKey.Trim();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HighSeasMedia/1.0");
    }

    public async Task<bool> DownloadEnglishSubtitleAsync(MediaItem media, CancellationToken cancellationToken)
    {
        LastError = "";
        LastFailureWasServiceError = false;
        if (apiKey.Length == 0) { LastError = "SubDL API key is missing."; return false; }

        var query = new List<string>
        {
            "api_key=" + Uri.EscapeDataString(apiKey),
            "film_name=" + Uri.EscapeDataString(media.Type == "Show" && media.Series.Length > 0 ? media.Series : media.Title),
            "type=" + (media.Type == "Show" ? "tv" : "movie"),
            "languages=EN",
            "subs_per_page=30",
            "releases=1",
            "unpack=1",
            "client=custom_integration"
        };
        if (media.Year.Length == 4) query.Add("year=" + Uri.EscapeDataString(media.Year));
        if (media.Type == "Show" && media.SeasonNumber > 0)
        {
            query.Add("season_number=" + media.SeasonNumber);
            query.Add("episode_number=" + media.EpisodeNumber);
        }

        try
        {
            using var response = await http.GetAsync(SearchEndpoint + "?" + string.Join('&', query), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LastFailureWasServiceError = (int)response.StatusCode >= 500 || response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
                LastError = $"Search returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
            {
                LastError = document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "SubDL returned an error." : "SubDL returned no match.";
                return false;
            }
            if (!document.RootElement.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
            {
                LastError = "No English subtitle matched this title.";
                return false;
            }

            var candidates = subtitles.EnumerateArray()
                .Where(x => IsEnglish(x) && HasMatchingEpisode(x, media))
                .OrderByDescending(x => ReleaseScore(x, media))
                .ToList();
            foreach (var candidate in candidates)
            {
                if (!candidate.TryGetProperty("url", out var urlNode) || string.IsNullOrWhiteSpace(urlNode.GetString())) continue;
                if (await DownloadCandidateAsync(urlNode.GetString()!, media, cancellationToken)) return true;
            }

            LastError = candidates.Count == 0 ? "No English subtitle matched this title or episode." : "SubDL subtitle downloads were empty or unusable.";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception)
        {
            LastFailureWasServiceError = true;
            LastError = $"SubDL request failed: {exception.Message}";
            return false;
        }
        catch (JsonException exception)
        {
            LastFailureWasServiceError = true;
            LastError = $"SubDL returned invalid data: {exception.Message}";
            return false;
        }
    }

    private async Task<bool> DownloadCandidateAsync(string url, MediaItem media, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0) return false;

            var extension = ".srt";
            byte[] subtitleBytes = bytes;
            if (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
            {
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
                var entry = archive.Entries
                    .Where(x => IsSubtitleExtension(Path.GetExtension(x.Name)))
                    .OrderBy(x => x.Name.Contains("forced", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault();
                if (entry == null) return false;
                extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                await using var input = entry.Open();
                using var output = new MemoryStream();
                await input.CopyToAsync(output, cancellationToken);
                subtitleBytes = output.ToArray();
            }

            if (subtitleBytes.Length < 16) return false;
            var destination = Path.ChangeExtension(media.FullPath, extension);
            await File.WriteAllBytesAsync(destination, subtitleBytes, cancellationToken);
            LastError = "";
            return true;
        }
        catch (IOException exception) { LastError = $"Could not save SubDL subtitle: {exception.Message}"; return false; }
        catch (HttpRequestException exception) { LastFailureWasServiceError = true; LastError = $"SubDL download failed: {exception.Message}"; return false; }
        catch (InvalidDataException) { LastError = "SubDL returned a damaged subtitle archive."; return false; }
    }

    private static bool IsEnglish(JsonElement subtitle)
    {
        if (!subtitle.TryGetProperty("language", out var language)) return true;
        var value = language.GetString() ?? "";
        return value.Equals("EN", StringComparison.OrdinalIgnoreCase) || value.Equals("English", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMatchingEpisode(JsonElement subtitle, MediaItem media)
    {
        if (media.Type != "Show") return true;
        if (subtitle.TryGetProperty("season", out var season) && season.TryGetInt32(out var seasonNumber) && seasonNumber > 0 && seasonNumber != media.SeasonNumber) return false;
        if (subtitle.TryGetProperty("episode", out var episode) && episode.TryGetInt32(out var episodeNumber) && episodeNumber > 0 && episodeNumber != media.EpisodeNumber) return false;
        return true;
    }

    private static int ReleaseScore(JsonElement subtitle, MediaItem media)
    {
        var release = subtitle.TryGetProperty("release_name", out var node) ? node.GetString() ?? "" : "";
        var score = 0;
        foreach (var token in RegexTokens(media.FullPath)) if (release.Contains(token, StringComparison.OrdinalIgnoreCase)) score++;
        if (subtitle.TryGetProperty("fps", out var fps) && fps.ValueKind != JsonValueKind.Null) score++;
        return score;
    }

    private static IEnumerable<string> RegexTokens(string path) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(path).Replace('.', ' '), @"(?i)\b(19|20)\d{2}\b", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3 && !Regex.IsMatch(x, @"(?i)^(bluray|webrip|webdl|brrip|x26[45]|hevc|aac|h264|h265)$"));

    private static bool IsSubtitleExtension(string extension) => extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase);
}

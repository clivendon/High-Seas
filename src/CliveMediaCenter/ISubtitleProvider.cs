namespace CliveMediaCenter;

/// <summary>Common contract used by the subtitle audit's ordered fallback chain.</summary>
internal interface ISubtitleProvider
{
    string Name { get; }
    string LastError { get; }
    bool LastFailureWasServiceError { get; }
    Task<bool> DownloadEnglishSubtitleAsync(MediaItem media, CancellationToken cancellationToken);
}

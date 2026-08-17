using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System.Runtime.InteropServices;

namespace CliveMediaCenter;

/// <summary>
/// Full-screen, in-app playback surface. LibVLC provides broad codec support, but users never
/// need the separate VLC desktop program or see its window.
/// </summary>
internal sealed class HighSeasPlayerForm : Form
{
    private static readonly Color Hull = Color.FromArgb(6, 13, 10);
    private static readonly Color Deck = Color.FromArgb(12, 28, 21);
    private static readonly Color Emerald = Color.FromArgb(45, 190, 105);
    private static readonly Color Sage = Color.FromArgb(171, 194, 180);

    private readonly LibVLC libVlc;
    private readonly MediaPlayer player;
    private readonly VideoView videoView;
    private readonly Panel controls;
    private readonly TrackBar timeline;
    private readonly Label elapsed;
    private readonly Label remaining;
    private readonly Label volumeStatus;
    private readonly Label title;
    private readonly Button playPause;
    private readonly Button captionButton;
    private readonly System.Windows.Forms.Timer uiTimer;
    private bool changingTimeline;
    private bool cursorHidden;
    private Point lastCursorPosition = Cursor.Position;
    private DateTime lastInteraction = DateTime.UtcNow;
    private readonly bool subtitlesEnabledAtStart;

    internal event EventHandler? PlaybackEnded;
    internal event EventHandler? NextEpisodeRequested;
    internal event EventHandler? PreviousEpisodeRequested;

    /// <summary>Used by release verification to prove the bundled native engine can load.</summary>
    internal static void VerifyPlaybackEngine()
    {
        Core.Initialize(FindBundledLibVlcDirectory());
        using var engine = new LibVLC("--quiet");
    }

    public HighSeasPlayerForm(string mediaPath, string? subtitlePath, Screen destination, bool subtitlesEnabled = false)
    {
        subtitlesEnabledAtStart = subtitlesEnabled;
        Core.Initialize(FindBundledLibVlcDirectory());
        libVlc = new LibVLC("--no-video-title-show", "--quiet");
        player = new MediaPlayer(libVlc);

        Text = $"High Seas Media — {Path.GetFileNameWithoutExtension(mediaPath)}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        BackColor = Color.Black;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = destination.Bounds;
        KeyPreview = true;

        videoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black, MediaPlayer = player };
        Controls.Add(videoView);

        // Keep the HUD as a sibling of the native video surface. Native HWND video controls can
        // cover managed children, which previously made timing information disappear on some PCs.
        controls = new Panel { Dock = DockStyle.Bottom, Height = 142, BackColor = Deck };
        Controls.Add(controls);
        controls.BringToFront();

        title = new Label
        {
            Text = Path.GetFileNameWithoutExtension(mediaPath),
            Location = new Point(20, 10),
            Size = new Size(Math.Max(500, destination.Bounds.Width - 40), 28),
            Font = new Font("Segoe UI Semibold", 12f),
            ForeColor = Color.White,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        controls.Controls.Add(title);

        timeline = new TrackBar
        {
            Location = new Point(12, 40),
            Width = Math.Max(500, destination.Bounds.Width - 24),
            Height = 28,
            Minimum = 0,
            Maximum = 10_000,
            TickStyle = TickStyle.None,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        timeline.MouseDown += (_, _) => changingTimeline = true;
        timeline.MouseUp += (_, _) => { player.Position = timeline.Value / 10_000f; changingTimeline = false; MarkInteraction(); };
        controls.Controls.Add(timeline);

        elapsed = new Label { Text = "0:00", Location = new Point(20, 69), Size = new Size(120, 22), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10f), TextAlign = ContentAlignment.MiddleLeft };
        controls.Controls.Add(elapsed);

        // Reserve a dedicated area immediately left of EXIT.  The previous label stretched
        // underneath the EXIT button, leaving only a thin sliver of the remaining-time text
        // visible on some resolutions.  Keeping this label to its own 220px lane makes the
        // countdown readable without competing with the transport controls.
        remaining = new Label
        {
            Text = "−0:00 / 0:00",
            Location = new Point(destination.Bounds.Width - 318, 69),
            Size = new Size(218, 22),
            ForeColor = Sage,
            Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        controls.Controls.Add(remaining);

        playPause = MakeControlButton("Ⅱ", 20, 101, 62);
        playPause.Click += (_, _) => TogglePlayPause();
        var back = MakeControlButton("−10", 92, 101, 62); back.Click += (_, _) => SeekBy(-10_000);
        var forward = MakeControlButton("+10", 164, 101, 62); forward.Click += (_, _) => SeekBy(10_000);
        captionButton = MakeControlButton("CC ON", 236, 101, 76); captionButton.Click += (_, _) => ToggleSubtitles();
        var previousEpisode = MakeControlButton("PREV", 322, 101, 66); previousEpisode.Click += (_, _) => PreviousEpisodeRequested?.Invoke(this, EventArgs.Empty);
        var nextEpisode = MakeControlButton("NEXT", 394, 101, 66); nextEpisode.Click += (_, _) => NextEpisodeRequested?.Invoke(this, EventArgs.Empty);
        var mute = MakeControlButton("MUTE", 466, 101, 76); mute.Click += (_, _) => ToggleMute();
        volumeStatus = new Label { Text = "VOL 100%", Location = new Point(554, 104), Size = new Size(110, 24), ForeColor = Sage, TextAlign = ContentAlignment.MiddleLeft };
        controls.Controls.Add(volumeStatus);
        var exit = MakeControlButton("EXIT", destination.Bounds.Width - 98, 101, 76); exit.Anchor = AnchorStyles.Top | AnchorStyles.Right; exit.Click += (_, _) => Close();

        player.EndReached += (_, _) => BeginInvoke(() =>
        {
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
            if (PlaybackEnded == null) Close();
        });
        player.Playing += (_, _) => BeginInvoke(() => playPause.Text = "Ⅱ");
        player.Paused += (_, _) => BeginInvoke(() => playPause.Text = "▶");

        player.Playing += (_, _) => BeginInvoke(() =>
        {
            ApplySubtitlePreference();
            MarkInteraction();
        });

        uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        uiTimer.Tick += (_, _) => UpdatePlaybackUi();
        uiTimer.Start();

        KeyDown += HandlePlayerKey;
        MouseMove += (_, _) => MarkInteraction();
        videoView.MouseMove += (_, _) => MarkInteraction();
        videoView.Click += (_, _) => MarkInteraction();
        controls.MouseMove += (_, _) => MarkInteraction();
        FormClosed += (_, _) => DisposePlayback();

        Shown += (_, _) =>
        {
            using var media = new Media(libVlc, mediaPath, FromType.FromPath);
            if (!string.IsNullOrWhiteSpace(subtitlePath)) media.AddOption($":sub-file={subtitlePath}");
            player.Play(media);
            Activate();
        };
    }

    public void TogglePlayPause()
    {
        if (player.IsPlaying) player.Pause();
        else player.Play();
        MarkInteraction();
    }

    public void StopPlayback() => Close();
    public void SeekBackward() => SeekBy(-10_000);
    public void SeekForward() => SeekBy(10_000);
    public void RequestPreviousEpisode() { PreviousEpisodeRequested?.Invoke(this, EventArgs.Empty); MarkInteraction(); }
    public void RequestNextEpisode() { NextEpisodeRequested?.Invoke(this, EventArgs.Empty); MarkInteraction(); }
    public void ToggleMute() { player.Mute = !player.Mute; MarkInteraction(); }
    public void VolumeBy(int amount) { player.Volume = Math.Clamp(player.Volume + amount, 0, 125); MarkInteraction(); }

    private Button MakeControlButton(string text, int x, int y, int width)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(27, 49, 39),
            ForeColor = Color.White,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Emerald;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 92, 60);
        controls.Controls.Add(button);
        return button;
    }

    private static string FindBundledLibVlcDirectory()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("High Seas playback requires a supported Windows processor.")
        };

        // Native files from a single-file .NET app are extracted outside AppContext.BaseDirectory.
        // NATIVE_DLL_SEARCH_DIRECTORIES is the supported runtime hint to that extraction root.
        var runtimeSearchPaths = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var roots = runtimeSearchPaths.Prepend(AppContext.BaseDirectory);
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "libvlc", architecture);
            if (File.Exists(Path.Combine(candidate, "libvlc.dll"))) return candidate;
        }
        throw new FileNotFoundException("The High Seas playback engine is missing from this installation.");
    }

    private void HandlePlayerKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Space: TogglePlayPause(); break;
            case Keys.Left: SeekBackward(); break;
            case Keys.Right: SeekForward(); break;
            case Keys.Up: VolumeBy(5); break;
            case Keys.Down: VolumeBy(-5); break;
            case Keys.M: ToggleMute(); break;
            case Keys.C: ToggleSubtitles(); break;
            case Keys.PageUp:
            case Keys.P: RequestPreviousEpisode(); break;
            case Keys.PageDown:
            case Keys.N: RequestNextEpisode(); break;
            case Keys.Escape:
            case Keys.Back: Close(); break;
            default: return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void SeekBy(long milliseconds)
    {
        if (player.Length > 0) player.Time = Math.Clamp(player.Time + milliseconds, 0, player.Length);
        MarkInteraction();
    }

    private void ToggleSubtitles()
    {
        var enable = player.Spu < 0;
        if (enable)
        {
            var trackId = FindSubtitleTrackId();
            if (trackId < 0) { captionButton.Text = "CC N/A"; MarkInteraction(); return; }
            player.SetSpu(trackId);
        }
        else player.SetSpu(-1);
        captionButton.Text = enable ? "CC ON" : "CC OFF";
        MarkInteraction();
    }

    private void ApplySubtitlePreference()
    {
        if (!subtitlesEnabledAtStart) { player.SetSpu(-1); captionButton.Text = "CC OFF"; return; }
        var trackId = FindSubtitleTrackId();
        if (trackId >= 0 && player.SetSpu(trackId)) captionButton.Text = "CC ON";
        else captionButton.Text = "CC N/A";
    }

    private int FindSubtitleTrackId()
    {
        try
        {
            var tracks = player.SpuDescription;
            if (tracks == null || tracks.Length == 0) return -1;
            var english = tracks.FirstOrDefault(track => track.Id >= 0 && (track.Name?.Contains("English", StringComparison.OrdinalIgnoreCase) ?? false));
            return english.Id >= 0 ? english.Id : tracks.FirstOrDefault(track => track.Id >= 0).Id;
        }
        catch { return -1; }
    }

    private void MarkInteraction()
    {
        lastInteraction = DateTime.UtcNow;
        controls.Visible = true;
        controls.BringToFront();
        if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
    }

    private void UpdatePlaybackUi()
    {
        var cursorPosition = Cursor.Position;
        if (cursorPosition != lastCursorPosition && Bounds.Contains(cursorPosition)) MarkInteraction();
        lastCursorPosition = cursorPosition;

        if (!changingTimeline && player.Length > 0) timeline.Value = Math.Clamp((int)(player.Position * 10_000), 0, 10_000);
        var length = Math.Max(0, player.Length);
        var current = Math.Clamp(player.Time, 0, length > 0 ? length : long.MaxValue);
        elapsed.Text = FormatTime(current);
        remaining.Text = length > 0 ? $"−{FormatTime(length - current)} / {FormatTime(length)}" : "Loading duration…";
        volumeStatus.Text = player.Mute ? "MUTED" : $"VOL {player.Volume}%";
        if (DateTime.UtcNow - lastInteraction > TimeSpan.FromSeconds(4) && player.IsPlaying)
        {
            controls.Visible = false;
            if (!cursorHidden) { Cursor.Hide(); cursorHidden = true; }
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
    }

    private void DisposePlayback()
    {
        if (cursorHidden) Cursor.Show();
        uiTimer.Stop();
        player.Stop();
        player.Dispose();
        libVlc.Dispose();
    }
}

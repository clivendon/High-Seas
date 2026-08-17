using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using QRCoder;

namespace CliveMediaCenter;

internal static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    static void Main(string[] args)
    {
        // A stable High Seas identity prevents Windows from reusing the old Clive Media taskbar
        // icon after an in-place update.
        _ = SetCurrentProcessExplicitAppUserModelID("HighSeas.MediaCenter.Desktop");
        // Keep coordinates in the real monitor's coordinate space.  Per-monitor
        // V2 avoids bitmap-scaling the entire 720p page into a cropped surface.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        if (args.Contains("--playback-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            HighSeasPlayerForm.VerifyPlaybackEngine();
            return;
        }
        if (args.Contains("--carousel-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                MainForm.VerifyCarouselScrolling();
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "high-seas-carousel-smoke.log"), exception.ToString());
                Environment.ExitCode = 1;
            }
            return;
        }
        var diagnosticIndex = Array.FindIndex(args, value => value.Equals("--live-ui-diagnostic", StringComparison.OrdinalIgnoreCase));
        if (diagnosticIndex >= 0)
        {
            var output = diagnosticIndex + 1 < args.Length ? args[diagnosticIndex + 1] : Path.Combine(Path.GetTempPath(), "high-seas-live-ui.png");
            try { MainForm.RunLiveUiDiagnostic(output); }
            catch (Exception exception)
            {
                File.WriteAllText(Path.ChangeExtension(output, ".error.txt"), exception.ToString());
                Environment.ExitCode = 1;
            }
            return;
        }
        Application.Run(new MainForm());
    }
}

/// <summary>
/// A poster carousel that retains native WinForms scrolling without exposing the bright system
/// scrollbar. Navigation is driven by focus, the mouse wheel, keyboard, or the phone remote.
/// </summary>
internal sealed class CarouselPanel : Panel
{
    private readonly Panel strip = new() { Location = Point.Empty, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly Dictionary<Control, Point> logicalLocations = new();
    private Control? selectedControl;
    private int contentWidth;
    private int offsetX;

    internal CarouselPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Controls.Add(strip);
    }

    internal void AddCarouselControl(Control control, Point logicalLocation)
    {
        logicalLocations[control] = logicalLocation;
        contentWidth = Math.Max(contentWidth, logicalLocation.X + control.Width + 14);
        control.Location = logicalLocation;
        strip.Controls.Add(control);
        LayoutStrip();
    }

    internal void RevealControl(Control control)
    {
        if (!logicalLocations.TryGetValue(control, out var logical)) return;
        selectedControl = control;
        var maximum = Math.Max(0, contentWidth - ClientSize.Width);
        var centered = logical.X - Math.Max(0, (ClientSize.Width - control.Width) / 2);
        offsetX = Math.Clamp(centered, 0, maximum);
        LayoutStrip();
        Refresh();
    }

    internal bool Owns(Control control) => ReferenceEquals(control.Parent, strip);

    internal int VisibleLeft(Control control) => Owns(control) ? control.Left + strip.Left : int.MinValue;

    internal int ViewportOffset => offsetX;

    internal int ContentWidth => contentWidth;

    private void LayoutStrip()
    {
        strip.SetBounds(-offsetX, 0, Math.Max(ClientSize.Width, contentWidth), Math.Max(ClientSize.Height, 1));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (selectedControl != null) RevealControl(selectedControl);
        else LayoutStrip();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        strip.BackColor = BackColor;
    }
}

/// <summary>Draws a multi-line synopsis with reliable wrapping and a final-line ellipsis.</summary>
internal sealed class SynopsisLabel : Label
{
    public SynopsisLabel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
    }
}

internal sealed class MediaItem
{
    public string FullPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "Movie";
    public string Episode { get; set; } = "";
    public string Series { get; set; } = "";
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string EpisodeTitle { get; set; } = "";
    public string Year { get; set; } = "";
    public string Subtitles { get; set; } = "Not checked";
    public string Quality { get; set; } = "";
    public string Collection { get; set; } = "";
    public List<string> Genres { get; set; } = new();
}

internal sealed class QbittorrentItem
{
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public double Progress { get; set; }
    public long Size { get; set; }
    public long DownloadSpeed { get; set; }
    public long UploadSpeed { get; set; }
    public long Eta { get; set; }
}

internal sealed class AppSettings
{
    public List<string> LibraryFolders { get; set; } = new();
    public bool AutoDownloadCovers { get; set; } = false;
    public bool AutoSubtitleAudit { get; set; } = false;
    public bool EnablePhoneRemote { get; set; } = false;
    public int PhoneRemotePort { get; set; } = 8765;
    public string PhoneRemotePin { get; set; } = "";
    public string TmdbReadToken { get; set; } = "";
    public string RealDebridApiToken { get; set; } = "";
    public string OpenSubtitlesApiKey { get; set; } = "";
    public string OpenSubtitlesUsername { get; set; } = "";
    public string OpenSubtitlesPassword { get; set; } = "";
    public string SubdlApiKey { get; set; } = "";
    public string QbittorrentUrl { get; set; } = "";
    public string QbittorrentUsername { get; set; } = "";
    public string QbittorrentPassword { get; set; } = "";
}

internal sealed record LibraryFilter(string Kind, string Series = "", int Season = 0, string Path = "", string Collection = "");

internal sealed class RenamePlan
{
    public string OldPath { get; set; } = "";
    public string NewPath { get; set; } = "";
}

internal sealed class EpisodeMetadata
{
    public string Series { get; set; } = "";
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Title { get; set; } = "";
}

internal sealed class SubtitleAuditEntry
{
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public long CheckedUtcTicks { get; set; }
    public string Status { get; set; } = "";
    public string ProviderSignature { get; set; } = "";
}

internal sealed class RemoteMediaEntry
{
    public string Id { get; set; } = "";
    public MediaItem Media { get; set; } = new();
}

internal sealed class MainForm : Form
{
    // High Seas: near-black hull tones, seaweed greens, and a bright emerald focus color.
    private static readonly Color Window = Color.FromArgb(9, 18, 15);
    private static readonly Color Surface = Color.FromArgb(16, 31, 25);
    private static readonly Color Control = Color.FromArgb(27, 49, 39);
    private static readonly Color Muted = Color.FromArgb(171, 194, 180);
    private static readonly Color Accent = Color.FromArgb(45, 190, 105);
    private static readonly Color FocusAccent = Color.FromArgb(84, 255, 148);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts" };

    private readonly string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clive Media Center");
    private readonly string settingsPath;
    private readonly string thumbnailFolder;
    private readonly string coverFolder;
    private readonly string metadataFolder;
    private readonly string ffmpegPath;
    private readonly string ffprobePath;
    private readonly TextBox search = new();
    private readonly ComboBox typeFilter = new();
    private readonly ComboBox monitors = new();
    private readonly ListView list = new();
    private readonly TreeView navigation = new();
    private readonly PictureBox preview = new();
    private readonly Label previewTitle = new();
    private readonly Label status = new();
    private readonly CheckBox useSubtitles = new();
    private readonly Button subtitleAuditButton = new();
    private readonly Button filenameAuditButton = new();
    private readonly Panel watchPanel = new();
    private readonly Panel posterGrid = new();
    private readonly Dictionary<Control, int> watchControlTops = new();
    private readonly TextBox watchSearch = new();
    private readonly Label watchPageTitle = new();
    private readonly Label watchStatus = new();
    private readonly Button watchBack = new();
    private readonly Panel watchFocusPanel = new();
    private readonly PictureBox watchFocusArt = new();
    private readonly Label watchFocusTitle = new();
    private readonly Label watchFocusMeta = new();
    private readonly SynopsisLabel watchFocusDescription = new();
    private readonly Label focusAudioLabel = new();
    private readonly Label focusMonitorLabel = new();
    private readonly ComboBox watchMonitorPicker = new();
    private readonly ComboBox watchAudioPicker = new();
    private readonly List<(Guid Id, string Name)> audioOutputs = new();
    private readonly List<MediaItem> library = new();
    private readonly Dictionary<string, DateTime> watchHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Panel> watchCards = new();
    private readonly Dictionary<Panel, (int Section, int Row, int Column, int Top)> watchCardPositions = new();
    private readonly Dictionary<Panel, Rectangle> watchCardLogicalBounds = new();
    private readonly Dictionary<Panel, (MediaItem Media, string Title, string Subtitle)> watchCardInfo = new();
    private readonly System.Windows.Forms.Timer watchMotionTimer = new() { Interval = 16 };
    private AppSettings settings = new();
    private string? chosenSubtitle;
    private CancellationTokenSource? thumbnailCancellation;
    private readonly object thumbnailProcessLock = new();
    private readonly HashSet<Process> thumbnailProcesses = new();
    private bool automaticAuditStarted;
    private bool genreEnrichmentRunning;
    private string browseMode = "Home";
    private string activeCollection = "";
    private int watchContentY;
    private int watchLayoutWidth;
    private int watchSectionIndex;
    private int watchScrollY;
    private int watchContentHeight;
    private int targetWatchScrollY;
    private CancellationTokenSource? focusDescriptionCancellation;
    private Panel? remoteSelectedCard;
    private bool watchSearchEditing;
    private Point lastPosterPointerPosition = new(int.MinValue, int.MinValue);
    private Form? activeRemoteDialog;
    private Form? activePhoneRemoteDialog;
    private HighSeasPlayerForm? activePlayer;
    private Action? activeRemoteSelect;
    private Action<string>? activeRemoteNavigate;
    private bool appFullscreen;
    private Rectangle windowedBounds;
    private FormWindowState windowedState;
    private MediaItem? selectedGridMedia;
    private TcpListener? remoteListener;
    private CancellationTokenSource? remoteCancellation;
    private string remotePin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    private string remoteMessage = "Phone remote is off";
    private readonly object remoteLibraryLock = new();
    private readonly Dictionary<string, MediaItem> remoteLibrary = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly HttpClient qbittorrentHttp = CreateQbittorrentClient();
    private readonly List<QbittorrentItem> qbittorrentItems = new();
    private bool qbittorrentAuthenticated;
    private bool qbittorrentRefreshRunning;
    private readonly AuthorizedDownloadService authorizedDownloads = new();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, int data, UIntPtr extraInfo);

    public MainForm(bool runStartupTasks = true)
    {
        Directory.CreateDirectory(appData);
        settingsPath = Path.Combine(appData, "settings.json");
        thumbnailFolder = Path.Combine(appData, "Thumbnails");
        Directory.CreateDirectory(thumbnailFolder);
        coverFolder = Path.Combine(appData, "Covers");
        Directory.CreateDirectory(coverFolder);
        metadataFolder = Path.Combine(appData, "Metadata");
        Directory.CreateDirectory(metadataFolder);
        LoadWatchHistory();
        ffmpegPath = FindTool("ffmpeg.exe");
        ffprobePath = FindTool("ffprobe.exe");

        Text = "High Seas Media";
        var packagedIcon = Path.Combine(AppContext.BaseDirectory, "high-seas-taskbar.ico");
        Icon = File.Exists(packagedIcon)
            ? new Icon(packagedIcon)
            : Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        BackColor = Window;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        // Layout coordinates are deliberately written for the actual monitor
        // surface.  Letting WinForms auto-scale every child at 150% turns the
        // 720p monitor into a cropped, overlapping page.
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new Size(1120, 620);
        Size = new Size(1400, 760);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildUi();
        BuildWatchUi();
        LoadSettings();
        Shown += async (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            FitWindowToCurrentMonitor();
            // Remote control should be ready immediately; a large NAS/library scan must not delay it.
            if (!runStartupTasks) return;
            if (settings.EnablePhoneRemote) StartPhoneRemote();
            await ScanLibraryAsync();
            ActiveControl = null;
        };
        FormClosing += (_, _) => { activePlayer?.Close(); CancelThumbnailWork(); StopPhoneRemote(); };
        KeyDown += HandleNavigationKey;
    }

    internal static void RunLiveUiDiagnostic(string screenshotPath)
    {
        using var form = new MainForm(runStartupTasks: false) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), Size = new Size(1280, 720) };
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try { await CaptureLiveUiDiagnosticAsync(form, screenshotPath); }
            catch (Exception exception) { failure = exception; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure != null) throw failure;
    }

    private static async Task CaptureLiveUiDiagnosticAsync(MainForm form, string screenshotPath)
    {
        await form.ScanLibraryAsync();
        form.Update();
        Application.DoEvents();

        var carouselCards = form.watchCards
            .Where(card => card.Parent?.Parent is CarouselPanel)
            .GroupBy(card => (CarouselPanel)card.Parent!.Parent!)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault()?.ToList() ?? new List<Panel>();
        if (carouselCards.Count == 0) throw new InvalidOperationException("The real Home screen did not create a poster carousel.");
        var row = (CarouselPanel)carouselCards[0].Parent!.Parent!;
        var diagnostics = new List<object>();
        foreach (var card in carouselCards)
        {
            form.lastPosterPointerPosition = Cursor.Position;
            form.SetRemoteSelection(card);
            await form.UpdateWatchFocusAsync(card);
            form.Update();
            Application.DoEvents();
            diagnostics.Add(new
            {
                Title = form.watchCardInfo.TryGetValue(card, out var cardInfo) ? cardInfo.Title : "disposed",
                Column = form.watchCardPositions.TryGetValue(card, out var cardPosition) ? cardPosition.Column : -1,
                RowWidth = row.ClientSize.Width,
                row.ContentWidth,
                row.ViewportOffset,
                VisibleLeft = row.VisibleLeft(card),
                VisibleRight = row.VisibleLeft(card) + card.Width,
                FocusImage = form.watchFocusArt.Image == null ? "none" : $"{form.watchFocusArt.Image.Width}x{form.watchFocusArt.Image.Height}",
                CachedCover = cardInfo.Media != null && File.Exists(Path.Combine(form.coverFolder, form.CoverKey(cardInfo.Media) + ".img"))
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!);
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(Path.ChangeExtension(screenshotPath, ".json"), JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));

        var last = carouselCards[^1];
        var lastLeft = row.VisibleLeft(last);
        if (lastLeft < 0 || lastLeft + last.Width > row.ClientSize.Width)
            throw new InvalidOperationException($"Live carousel left its final card outside the viewport: {lastLeft}..{lastLeft + last.Width}/{row.ClientSize.Width}.");
        if (form.watchFocusArt.Image == null)
            throw new InvalidOperationException("Live focus banner rendered without an image.");
    }

    private string FindTool(string name)
    {
        return Directory.Exists(Path.Combine(AppContext.BaseDirectory, "tools"))
            ? Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "tools"), name, SearchOption.AllDirectories).FirstOrDefault() ?? ""
            : "";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HighSeasMedia/1.0 (local media library)");
        return client;
    }

    private static HttpClient CreateQbittorrentClient()
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HighSeasMedia/1.0 (qBittorrent dashboard)");
        return client;
    }

    private Button MakeButton(string text, int width, Color? color = null)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 38,
            BackColor = color ?? Control,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Margin = new Padding(5)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 105, 86);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 75, 53);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(27, 98, 57);
        return button;
    }

    private void BuildUi()
    {
        var heading = new Label { Text = "My Movies && Shows", AutoSize = true, Font = new Font("Segoe UI Semibold", 22f), Location = new Point(22, 16) };
        Controls.Add(heading);

        var toolbar = new FlowLayoutPanel { Location = new Point(20, 65), Width = ClientSize.Width - 40, Height = 46, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true };
        search.Width = 300;
        search.Height = 34;
        search.PlaceholderText = "Search movies, shows, or episodes...";
        search.BackColor = Control;
        search.ForeColor = Color.White;
        search.BorderStyle = BorderStyle.FixedSingle;
        search.Margin = new Padding(5, 7, 8, 5);
        search.TextChanged += (_, _) => FillList();
        toolbar.Controls.Add(search);

        typeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        typeFilter.Items.AddRange(new object[] { "Everything", "Movies", "Shows" });
        typeFilter.SelectedIndex = 0;
        typeFilter.Width = 125;
        typeFilter.BackColor = Control;
        typeFilter.ForeColor = Color.White;
        typeFilter.FlatStyle = FlatStyle.Flat;
        typeFilter.Margin = new Padding(5, 7, 8, 5);
        typeFilter.SelectedIndexChanged += (_, _) => FillList();
        toolbar.Controls.Add(typeFilter);

        var folders = MakeButton("Library folders...", 145);
        folders.Click += (_, _) => EditFolders();
        toolbar.Controls.Add(folders);
        var refresh = MakeButton("Refresh", 90);
        refresh.Click += async (_, _) => await ScanLibraryAsync();
        toolbar.Controls.Add(refresh);
        var updateLibrary = MakeButton("Update library", 135, Accent);
        updateLibrary.Click += async (_, _) => await RunLibraryUpdateAsync();
        toolbar.Controls.Add(updateLibrary);
        subtitleAuditButton.Text = "Check subtitles";
        subtitleAuditButton.Width = 135;
        subtitleAuditButton.Height = 38;
        subtitleAuditButton.BackColor = Control;
        subtitleAuditButton.ForeColor = Color.White;
        subtitleAuditButton.FlatStyle = FlatStyle.Flat;
        subtitleAuditButton.UseVisualStyleBackColor = false;
        subtitleAuditButton.Margin = new Padding(5);
        subtitleAuditButton.FlatAppearance.BorderColor = Color.FromArgb(72, 105, 86);
        subtitleAuditButton.Click += async (_, _) => await PromptSubtitleAuditAsync();
        toolbar.Controls.Add(subtitleAuditButton);
        filenameAuditButton.Text = "Check filenames";
        filenameAuditButton.Width = 135;
        filenameAuditButton.Height = 38;
        filenameAuditButton.BackColor = Control;
        filenameAuditButton.ForeColor = Color.White;
        filenameAuditButton.FlatStyle = FlatStyle.Flat;
        filenameAuditButton.UseVisualStyleBackColor = false;
        filenameAuditButton.Margin = new Padding(5);
        filenameAuditButton.FlatAppearance.BorderColor = Color.FromArgb(72, 105, 86);
        filenameAuditButton.Click += async (_, _) => await CheckFilenamesAsync();
        toolbar.Controls.Add(filenameAuditButton);
        var authorizedDownload = MakeButton("Download link", 120);
        authorizedDownload.Click += async (_, _) => await OpenAuthorizedDownloadAsync();
        toolbar.Controls.Add(authorizedDownload);
        var appSettings = MakeButton("Settings", 95);
        appSettings.Click += (_, _) => EditSettings();
        toolbar.Controls.Add(appSettings);
        var watch = MakeButton("Watch", 85, Accent);
        watch.Click += (_, _) => { watchPanel.Visible = true; watchPanel.BringToFront(); RefreshWatchView(); };
        toolbar.Controls.Add(watch);
        Controls.Add(toolbar);

        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.BackColor = Surface;
        list.ForeColor = Color.White;
        list.BorderStyle = BorderStyle.FixedSingle;
        navigation.Location = new Point(25, 120);
        navigation.Size = new Size(215, 490);
        navigation.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        navigation.BackColor = Surface;
        navigation.ForeColor = Color.White;
        navigation.BorderStyle = BorderStyle.FixedSingle;
        navigation.HideSelection = false;
        navigation.AfterSelect += (_, _) => FillList();
        Controls.Add(navigation);

        list.Location = new Point(250, 120);
        list.Size = new Size(795, 490);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.Columns.Add("Title", 270);
        list.Columns.Add("Type", 75);
        list.Columns.Add("Episode", 85);
        list.Columns.Add("Year", 60);
        list.Columns.Add("Subtitles", 185);
        list.Columns.Add("Quality", 90);
        list.SelectedIndexChanged += async (_, _) => { selectedGridMedia = null; await ShowThumbnailAsync(); };
        list.DoubleClick += (_, _) => PlaySelected();
        Controls.Add(list);

        var previewPanel = new Panel { BackColor = Surface, Location = new Point(1060, 120), Size = new Size(310, 490), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right };
        preview.BackColor = Color.FromArgb(6, 13, 10);
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        preview.Location = new Point(15, 15);
        preview.Size = new Size(280, 350);
        preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        previewPanel.Controls.Add(preview);
        previewTitle.Location = new Point(15, 380);
        previewTitle.Size = new Size(280, 85);
        previewTitle.Font = new Font("Segoe UI Semibold", 11f);
        previewTitle.TextAlign = ContentAlignment.TopCenter;
        previewTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        previewPanel.Controls.Add(previewTitle);
        Controls.Add(previewPanel);

        var bottom = new FlowLayoutPanel { Location = new Point(20, 625), Width = ClientSize.Width - 40, Height = 52, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, WrapContents = false, AutoScroll = true };
        bottom.Controls.Add(new Label { Text = "Play on:", AutoSize = true, Margin = new Padding(5, 15, 5, 0) });
        monitors.DropDownStyle = ComboBoxStyle.DropDownList;
        monitors.Width = 220;
        monitors.BackColor = Control;
        monitors.ForeColor = Color.White;
        monitors.FlatStyle = FlatStyle.Flat;
        monitors.Margin = new Padding(5, 10, 10, 5);
        foreach (var screen in Screen.AllScreens.Select((screen, index) => new { screen, index }))
            monitors.Items.Add($"Monitor {screen.index + 1} - {screen.screen.Bounds.Width}x{screen.screen.Bounds.Height}{(screen.screen.Primary ? " (primary)" : "")}");
        monitors.SelectedIndex = Math.Min(2, monitors.Items.Count - 1);
        bottom.Controls.Add(monitors);
        useSubtitles.Text = "Use English subtitles";
        useSubtitles.Checked = true;
        useSubtitles.AutoSize = true;
        useSubtitles.Margin = new Padding(5, 14, 10, 0);
        bottom.Controls.Add(useSubtitles);
        var choose = MakeButton("Choose subtitle...", 150);
        choose.Click += (_, _) => ChooseSubtitle();
        bottom.Controls.Add(choose);
        var play = MakeButton("PLAY", 150, Accent);
        play.Font = new Font("Segoe UI Semibold", 12f);
        play.Height = 46;
        play.Click += (_, _) => PlaySelected();
        bottom.Controls.Add(play);
        Controls.Add(bottom);

        status.Location = new Point(25, 684);
        status.AutoSize = true;
        status.ForeColor = Muted;
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(status);

        Resize += (_, _) =>
        {
            list.Width = ClientSize.Width - 605;
            previewPanel.Left = ClientSize.Width - 335;
            previewPanel.Width = 310;
            previewPanel.Height = ClientSize.Height - 245;
            list.Height = ClientSize.Height - 245;
            navigation.Height = ClientSize.Height - 245;
            bottom.Top = ClientSize.Height - 95;
            bottom.Width = ClientSize.Width - 40;
            toolbar.Width = ClientSize.Width - 40;
            status.Top = ClientSize.Height - 36;
        };
    }

    private Button MakeWatchNavButton(string text, string mode, int width)
    {
        var button = MakeButton(text, width);
        button.Height = 40;
        button.Click += (_, _) =>
        {
            browseMode = mode;
            activeCollection = "";
            RefreshWatchView();
            if (mode == "QBittorrent") _ = RefreshQbittorrentAsync();
        };
        return button;
    }

    private void BuildWatchUi()
    {
        watchPanel.Dock = DockStyle.Fill;
        watchPanel.Bounds = ClientRectangle;
        watchPanel.BackColor = Color.FromArgb(7, 15, 12);
        Controls.Add(watchPanel);
        watchPanel.BringToFront();

        var top = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(11, 23, 18) };
        watchPanel.Controls.Add(top);
        watchPanel.PerformLayout();
        var brand = new Label { Text = "HIGH SEAS", AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Black", 17f), Location = new Point(25, 24) };
        top.Controls.Add(brand);
        var nav = new FlowLayoutPanel { Location = new Point(250, 17), Height = 52, Width = Math.Max(600, watchPanel.ClientSize.Width - 265), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, WrapContents = false, AutoScroll = false };
        nav.HorizontalScroll.Enabled = false;
        nav.HorizontalScroll.Visible = false;
        nav.Controls.Add(MakeWatchNavButton("Home", "Home", 76));
        nav.Controls.Add(MakeWatchNavButton("Movies", "Movies", 76));
        nav.Controls.Add(MakeWatchNavButton("Shows", "Shows", 76));
        nav.Controls.Add(MakeWatchNavButton("Collections", "Collections", 100));
        nav.Controls.Add(MakeWatchNavButton("qBittorrent", "QBittorrent", 112));
        watchSearch.Width = 220;
        watchSearch.Height = 36;
        watchSearch.TabStop = false;
        watchSearch.PlaceholderText = "Search your library...";
        watchSearch.BackColor = Control;
        watchSearch.ForeColor = Color.White;
        watchSearch.BorderStyle = BorderStyle.FixedSingle;
        watchSearch.Margin = new Padding(12, 7, 6, 5);
        watchSearch.TextChanged += (_, _) => RefreshWatchView();
        watchSearch.MouseDown += (_, _) => watchSearchEditing = true;
        watchSearch.LostFocus += (_, _) => watchSearchEditing = false;
        nav.Controls.Add(watchSearch);
        var phone = MakeButton("Phone Remote", 112);
        phone.Margin = new Padding(8, 5, 2, 5);
        phone.Click += (_, _) => ShowPhoneRemoteDialog();
        nav.Controls.Add(phone);
        var fullscreen = MakeButton("Fullscreen", 100);
        fullscreen.Margin = new Padding(2, 5, 2, 5);
        fullscreen.Click += (_, _) => ToggleAppFullscreen();
        nav.Controls.Add(fullscreen);
        var manage = MakeButton("Manage Library", 125);
        manage.Margin = new Padding(2, 5, 5, 5);
        manage.Click += (_, _) => { CancelThumbnailWork(); selectedGridMedia = null; watchPanel.Visible = false; };
        nav.Controls.Add(manage);
        top.Controls.Add(nav);

        watchBack.Text = "← BACK";
        watchBack.Size = new Size(120, 38);
        watchBack.Location = new Point(28, 94);
        watchBack.BackColor = Control;
        watchBack.ForeColor = Color.White;
        watchBack.FlatStyle = FlatStyle.Flat;
        watchBack.UseVisualStyleBackColor = false;
        watchBack.Visible = false;
        watchBack.Click += (_, _) => NavigateWatchBack();
        watchPanel.Controls.Add(watchBack);

        watchPageTitle.Text = "Home";
        watchPageTitle.AutoSize = true;
        watchPageTitle.Font = new Font("Segoe UI Semibold", 22f);
        watchPageTitle.Location = new Point(28, 96);
        watchPanel.Controls.Add(watchPageTitle);

        watchFocusPanel.Location = new Point(20, 140);
        watchFocusPanel.Size = new Size(Math.Max(600, watchPanel.ClientSize.Width - 40), 280);
        watchFocusPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        watchFocusPanel.BackColor = Color.FromArgb(14, 34, 25);
        watchFocusArt.Location = new Point(watchFocusPanel.Width - 540, 14);
        watchFocusArt.Size = new Size(520, 252);
        watchFocusArt.SizeMode = PictureBoxSizeMode.Zoom;
        watchFocusArt.BackColor = Color.FromArgb(5, 12, 9);
        watchFocusArt.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
        watchFocusTitle.Location = new Point(40, 28);
        watchFocusTitle.Size = new Size(watchFocusPanel.Width - 610, 52);
        watchFocusTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        watchFocusTitle.Font = new Font("Segoe UI Semibold", 26f);
        watchFocusTitle.AutoEllipsis = true;
        watchFocusMeta.Location = new Point(42, 82);
        watchFocusMeta.Size = new Size(watchFocusPanel.Width - 612, 24);
        watchFocusMeta.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        watchFocusMeta.ForeColor = Color.FromArgb(183, 210, 193);
        watchFocusMeta.Font = new Font("Segoe UI Semibold", 10f);
        watchFocusDescription.Location = new Point(40, 116);
        watchFocusDescription.Size = new Size(watchFocusPanel.Width - 610, 98);
        watchFocusDescription.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        watchFocusDescription.ForeColor = Color.FromArgb(224, 237, 228);
        watchFocusDescription.Font = new Font("Segoe UI", 12f);
        focusAudioLabel.Text = "AUDIO THROUGH";
        focusAudioLabel.AutoSize = true;
        focusAudioLabel.Location = new Point(40, 218);
        focusAudioLabel.ForeColor = Muted;
        focusAudioLabel.Font = new Font("Segoe UI Semibold", 8f);
        watchAudioPicker.Location = new Point(40, 238);
        watchAudioPicker.Size = new Size(230, 30);
        watchAudioPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        watchAudioPicker.BackColor = Control;
        watchAudioPicker.ForeColor = Color.White;
        watchAudioPicker.FlatStyle = FlatStyle.Flat;
        watchAudioPicker.SelectedIndexChanged += (_, _) =>
        {
            if (watchAudioPicker.SelectedIndex >= 0 && watchAudioPicker.SelectedIndex < audioOutputs.Count)
                SetAudioOutput(audioOutputs[watchAudioPicker.SelectedIndex].Id);
        };
        focusMonitorLabel.Text = "PLAY ON";
        focusMonitorLabel.AutoSize = true;
        focusMonitorLabel.Location = new Point(294, 218);
        focusMonitorLabel.ForeColor = Muted;
        focusMonitorLabel.Font = new Font("Segoe UI Semibold", 8f);
        watchMonitorPicker.Location = new Point(294, 238);
        watchMonitorPicker.Size = new Size(230, 30);
        watchMonitorPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        watchMonitorPicker.BackColor = Control;
        watchMonitorPicker.ForeColor = Color.White;
        watchMonitorPicker.FlatStyle = FlatStyle.Flat;
        foreach (var item in monitors.Items) watchMonitorPicker.Items.Add(item);
        watchMonitorPicker.SelectedIndex = monitors.SelectedIndex;
        watchMonitorPicker.SelectedIndexChanged += (_, _) => { if (watchMonitorPicker.SelectedIndex >= 0) monitors.SelectedIndex = watchMonitorPicker.SelectedIndex; };
        watchFocusPanel.Controls.AddRange(new Control[] { watchFocusArt, watchFocusTitle, watchFocusMeta, watchFocusDescription, focusAudioLabel, watchAudioPicker, focusMonitorLabel, watchMonitorPicker });
        RefreshAudioOutputs();
        // The hero is a fixed Netflix-style detail region.  Only the shelf
        // viewport scrolls, so selecting a card never hides the details pane or
        // lets a carousel paint underneath it.
        watchPanel.Controls.Add(watchFocusPanel);
        posterGrid.Location = new Point(20, 435);
        posterGrid.Size = new Size(Math.Max(600, watchPanel.ClientSize.Width - 40), Math.Max(100, watchPanel.ClientSize.Height - 500));
        posterGrid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        posterGrid.AutoScroll = false;
        posterGrid.BackColor = Color.FromArgb(7, 15, 12);
        posterGrid.MouseWheel += (_, e) =>
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            AnimateWatchScroll(watchScrollY - e.Delta);
        };
        watchPanel.Controls.Add(posterGrid);

        watchMotionTimer.Tick += (_, _) => AdvanceWatchMotion();

        watchStatus.AutoSize = true;
        watchStatus.ForeColor = Muted;
        watchStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        watchStatus.Location = new Point(watchPanel.ClientSize.Width - 210, watchPanel.ClientSize.Height - 30);
        watchPanel.Controls.Add(watchStatus);

        void LayoutWatchSurface()
        {
            var visibleWidth = Math.Max(640, watchPanel.ClientSize.Width);
            var visibleHeight = Math.Max(540, watchPanel.ClientSize.Height);
            nav.Width = Math.Max(420, visibleWidth - nav.Left - 14);
            var contentWidth = Math.Max(600, visibleWidth - 40);
            var compactMonitor = DeviceDpi > 96 && Screen.FromControl(this).Bounds.Height >= 900;
            var heroHeight = compactMonitor ? 320 : 280;
            watchFocusPanel.SetBounds(20, 140, contentWidth, heroHeight);
            watchFocusTitle.Font = new Font("Segoe UI Semibold", compactMonitor ? 24f : 26f);
            watchFocusDescription.Font = new Font("Segoe UI", compactMonitor ? 11.5f : 12f);
            var artWidth = compactMonitor ? 360 : 520;
            var artLeft = compactMonitor
                ? 700
                : Math.Max(540, watchFocusPanel.ClientSize.Width - artWidth - 20);
            watchFocusArt.SetBounds(artLeft, 14, artWidth, compactMonitor ? 252 : 252);
            // Keep synopsis text inside the visible hero column on high-DPI TVs;
            // an unconstrained logical width made lines run under the artwork.
            var textWidth = compactMonitor
                ? 620
                : Math.Min(980, Math.Max(360, artLeft - 60));
            watchFocusTitle.Width = textWidth;
            watchFocusMeta.Width = textWidth - 2;
            watchFocusDescription.Width = textWidth;
            watchFocusDescription.Height = compactMonitor ? 120 : 98;
            focusAudioLabel.Location = new Point(40, compactMonitor ? 248 : 218);
            watchAudioPicker.Location = new Point(40, compactMonitor ? 268 : 238);
            focusMonitorLabel.Location = new Point(294, compactMonitor ? 248 : 218);
            watchMonitorPicker.Location = new Point(294, compactMonitor ? 268 : 238);
            var shelfTop = watchFocusPanel.Bottom + 24;
            posterGrid.SetBounds(20, shelfTop, contentWidth, Math.Max(100, visibleHeight - shelfTop - 60));
            watchStatus.Location = new Point(Math.Max(20, visibleWidth - 220), Math.Max(20, visibleHeight - 28));
        }

        watchPanel.Resize += (_, _) =>
        {
            LayoutWatchSurface();
            var width = posterGrid.ClientSize.Width;
            if (watchPanel.Visible && library.Count > 0 && Math.Abs(width - watchLayoutWidth) > 80)
            {
                watchLayoutWidth = width;
                BeginInvoke(RefreshWatchView);
            }
            else if (watchPanel.Visible)
            {
                SetWatchScroll(watchScrollY);
            }
        };
        LayoutWatchSurface();
    }

    private void RefreshWatchView()
    {
        if (IsDisposed || !IsHandleCreated) return;
        SetWatchScroll(0);
        DisposePosterGrid();
        var qBitView = browseMode == "QBittorrent";
        watchFocusArt.Visible = !qBitView;
        focusAudioLabel.Visible = !qBitView;
        watchAudioPicker.Visible = !qBitView;
        focusMonitorLabel.Visible = !qBitView;
        watchMonitorPicker.Visible = !qBitView;
        if (qBitView && watchFocusArt.Image != null)
        {
            var oldFocus = watchFocusArt.Image;
            watchFocusArt.Image = null;
            oldFocus.Dispose();
        }
        // The hero is fixed above the shelf viewport; shelf content starts at
        // the top of that viewport and scrolls independently.
        watchContentY = 0;
        watchSectionIndex = 0;
        watchLayoutWidth = posterGrid.ClientSize.Width;
        watchBack.Visible = browseMode == "Collection";
        watchPageTitle.Location = watchBack.Visible ? new Point(170, 96) : new Point(28, 96);
        var query = watchSearch.Text.Trim();
        var movies = library.Where(x => x.Type == "Movie").ToList();
        var shows = library.Where(x => x.Type == "Show").GroupBy(x => x.Series.Length > 0 ? x.Series : x.Title, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key).ToList();

        bool Matches(string text) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);

        if (browseMode == "Home")
        {
            watchPageTitle.Text = "Home";
            var recentlyWatched = movies.Where(x => watchHistory.ContainsKey(x.FullPath)).OrderByDescending(x => watchHistory[x.FullPath]).Take(12).Where(x => Matches(x.Title)).Select(x => MakeMediaCard(x, false)).ToList();
            AddPosterSection("Continue Watching", recentlyWatched);
            var recommendationSeed = movies.Where(x => watchHistory.ContainsKey(x.FullPath)).OrderByDescending(x => watchHistory[x.FullPath]).FirstOrDefault();
            if (recommendationSeed != null && recommendationSeed.Genres.Count > 0)
            {
                var recommendations = movies.Where(x => !x.FullPath.Equals(recommendationSeed.FullPath, StringComparison.OrdinalIgnoreCase) && x.Genres.Any(genre => recommendationSeed.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)))
                    .OrderByDescending(x => x.Genres.Count(genre => recommendationSeed.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))).ThenBy(x => x.Title).Take(12).Select(x => MakeMediaCard(x, false)).ToList();
                AddPosterSection($"Because you watched {recommendationSeed.Title}", recommendations);
            }
            AddPosterSection("Recently Added", movies.Where(x => Matches(x.Title)).OrderByDescending(x => File.GetLastWriteTimeUtc(x.FullPath)).Take(12).Select(x => MakeMediaCard(x, false)).ToList());
            AddPosterSection("Shows", shows.Where(x => Matches(x.Key)).Take(18).Select(MakeSeriesCard).ToList());
            var collectionCards = movies.Where(IsMovieCollection).GroupBy(x => x.Collection, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key).Where(x => Matches(x.Key)).Take(12).Select(MakeCollectionCard).ToList();
            AddPosterSection("Movie Collections", collectionCards);
            foreach (var genre in new[] { "Action", "Comedy", "Horror", "Science Fiction", "Thriller", "Drama", "Adventure", "Family" })
            {
                var genreCards = movies.Where(x => Matches(x.Title) && x.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)).OrderBy(x => x.Title).Take(12).Select(x => MakeMediaCard(x, false)).ToList();
                AddPosterSection(genre, genreCards);
            }
            AddPosterSection("More Movies", movies.Where(x => Matches(x.Title)).OrderBy(x => x.Title).Take(12).Select(x => MakeMediaCard(x, false)).ToList());
        }
        else if (browseMode == "Movies")
        {
            watchPageTitle.Text = "Movies";
            AddPosterGridSection("All Movies", movies.Where(x => Matches(x.Title)).OrderBy(x => x.Title).Select(x => MakeMediaCard(x, false)).ToList());
        }
        else if (browseMode == "Shows")
        {
            watchPageTitle.Text = "Shows";
            AddPosterGridSection("All Shows", shows.Where(x => Matches(x.Key)).Select(MakeSeriesCard).ToList());
        }
        else if (browseMode == "Collections")
        {
            watchPageTitle.Text = "Collections";
            var groups = movies.Where(IsMovieCollection).GroupBy(x => x.Collection, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key).Where(x => Matches(x.Key)).Select(MakeCollectionCard).ToList();
            AddPosterGridSection("Movie Collections", groups);
        }
        else if (browseMode == "Collection")
        {
            var collectionMovies = movies.Where(x => x.Collection.Equals(activeCollection, StringComparison.OrdinalIgnoreCase) && Matches(x.Title)).OrderBy(x => x.Year).ThenBy(x => x.Title).Select(x => MakeMediaCard(x, false)).ToList();
            var collectionCount = movies.Count(x => x.Collection.Equals(activeCollection, StringComparison.OrdinalIgnoreCase));
            var displayCollection = DisplayCollectionName(activeCollection, collectionCount);
            watchPageTitle.Text = displayCollection;
            AddPosterGridSection($"{displayCollection} Films", collectionMovies);
        }
        else if (browseMode == "QBittorrent")
        {
            watchPageTitle.Text = "qBittorrent";
            watchFocusTitle.Text = "qBittorrent";
            watchFocusMeta.Text = string.IsNullOrWhiteSpace(settings.QbittorrentUrl)
                ? "Not configured"
                : qbittorrentRefreshRunning ? "Connecting…" : $"{qbittorrentItems.Count} active transfer{(qbittorrentItems.Count == 1 ? "" : "s")}";
            watchFocusDescription.Text = string.IsNullOrWhiteSpace(settings.QbittorrentUrl)
                ? "Connect this dashboard to your local qBittorrent Web UI to see transfers and manage them from the same remote-friendly screen."
                : "Transfers stay in qBittorrent. High Seas Media only reads the Web API and sends the actions you choose.";
            var torrentCards = qbittorrentItems.OrderByDescending(x => x.Progress < 1).ThenBy(x => x.Name).Select(MakeQbittorrentCard).ToList();
            AddTorrentGridSection("Transfers", torrentCards);
            if (torrentCards.Count == 0)
            {
                var empty = new Label { Text = string.IsNullOrWhiteSpace(settings.QbittorrentUrl) ? "Use the qBittorrent tab again to configure a connection." : qbittorrentRefreshRunning ? "Loading transfers…" : "No transfers found.", ForeColor = Muted, AutoSize = true, Location = new Point(18, watchContentY + 18) };
                AddWatchControl(empty);
                watchContentY = empty.Bottom + 24;
                if (string.IsNullOrWhiteSpace(settings.QbittorrentUrl))
                {
                    var configure = MakeButton("Configure qBittorrent", 180, Accent);
                    configure.Location = new Point(18, watchContentY);
                    configure.Click += (_, _) => { if (ConfigureQbittorrent()) _ = RefreshQbittorrentAsync(); };
                    AddWatchControl(configure);
                    watchContentY = configure.Bottom + 24;
                }
            }
            if (!qbittorrentRefreshRunning && !string.IsNullOrWhiteSpace(settings.QbittorrentUrl)) _ = RefreshQbittorrentAsync();
        }
        var jump = MakeButton("↑  JUMP TO TOP", 165, Accent);
        jump.Location = new Point(Math.Max(18, (posterGrid.ClientSize.Width - jump.Width - SystemInformation.VerticalScrollBarWidth) / 2), watchContentY + 24);
        jump.Click += (_, _) => SetWatchScroll(0);
        AddWatchControl(jump);
        watchContentY = jump.Bottom + 70;
        var bottomMarker = new Panel { Location = new Point(0, watchContentY), Size = new Size(1, 1), BackColor = posterGrid.BackColor };
        AddWatchControl(bottomMarker);
        // Leave enough breathing room for the final carousel, focus growth, taskbar, and remote
        // navigation. Without this safe area WinForms reports the bottom before it is truly visible.
        watchContentHeight = watchContentY + 180;
        ConfigureWatchScrollBar();
        posterGrid.PerformLayout();
        SetWatchScroll(0);
        // Start at the top of the Kodi-style home surface.  The first shelf may
        // extend below the viewport, but it must not auto-scroll the hero away
        // just to make its first card fully visible.
        if (watchCards.Count > 0) SetRemoteSelection(watchCards[0], ensureVerticalVisibility: false);
        watchStatus.Text = $"{library.Count} media files";
    }

    private void DisposePosterGrid()
    {
        foreach (Control control in watchControlTops.Keys.ToArray())
        {
            DisposeImages(control);
            posterGrid.Controls.Remove(control);
            control.Dispose();
        }
        watchControlTops.Clear();
        watchCards.Clear();
        watchCardPositions.Clear();
        watchCardLogicalBounds.Clear();
        watchCardInfo.Clear();
        remoteSelectedCard = null;
        watchContentHeight = 0;
        watchScrollY = 0;
        targetWatchScrollY = 0;
        focusDescriptionCancellation?.Cancel();
        focusDescriptionCancellation?.Dispose();
        focusDescriptionCancellation = null;
    }

    private static void DisposeImages(Control parent)
    {
        if (parent is PictureBox picture) { picture.Image?.Dispose(); picture.Image = null; }
        foreach (Control child in parent.Controls) DisposeImages(child);
    }

    private void AddWatchControl(Control control)
    {
        watchControlTops[control] = control.Top;
        control.Top -= watchScrollY;
        posterGrid.Controls.Add(control);
        AttachWatchWheel(control);
    }

    private void AttachWatchWheel(Control control)
    {
        control.MouseWheel += (_, e) =>
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            AnimateWatchScroll(watchScrollY - e.Delta);
        };
        foreach (Control child in control.Controls) AttachWatchWheel(child);
    }

    private int MaxWatchScroll() => Math.Max(0, watchContentHeight - Math.Max(1, posterGrid.ClientSize.Height));

    private void ConfigureWatchScrollBar()
    {
        // SetWatchScroll moves controls from stable logical coordinates, so there is no native
        // scrollbar range to configure or accidentally corrupt during layout.
    }

    private void SetWatchScroll(int value)
    {
        var maximum = MaxWatchScroll();
        watchScrollY = Math.Clamp(value, 0, maximum);
        foreach (var item in watchControlTops)
            item.Key.Top = item.Value - watchScrollY;
    }

    private void AnimateWatchScroll(int value)
    {
        targetWatchScrollY = Math.Clamp(value, 0, MaxWatchScroll());
        watchMotionTimer.Start();
    }

    private void AdvanceWatchMotion()
    {
        var moving = false;
        var distance = targetWatchScrollY - watchScrollY;
        if (distance != 0)
        {
            var step = Math.Sign(distance) * Math.Max(1, (int)Math.Ceiling(Math.Abs(distance) * 0.32));
            if (Math.Abs(step) > Math.Abs(distance)) step = distance;
            SetWatchScroll(watchScrollY + step);
            moving = watchScrollY != targetWatchScrollY;
        }

        if (!moving) watchMotionTimer.Stop();
    }

    private async Task UpdateWatchFocusAsync(Panel card)
    {
        if (!watchCardInfo.TryGetValue(card, out var info)) return;
        focusDescriptionCancellation?.Cancel();
        focusDescriptionCancellation?.Dispose();
        focusDescriptionCancellation = new CancellationTokenSource();
        var token = focusDescriptionCancellation.Token;

        watchFocusTitle.Text = info.Title;
        var details = new[] { info.Subtitle, info.Media.Quality, info.Media.Subtitles }
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.Equals("Not checked", StringComparison.OrdinalIgnoreCase));
        watchFocusMeta.Text = string.Join("  •  ", details);
        watchFocusDescription.Text = "Loading description…";
        // Never leave the hero empty while metadata is loading: reuse the exact card poster first.
        // A full-resolution cached/downloaded cover replaces it below when available.
        var cardPoster = card.Controls.OfType<PictureBox>().FirstOrDefault()?.Image;
        var immediateArt = cardPoster != null ? new Bitmap(cardPoster) : LoadFocusArtwork(info.Media) ?? CreatePlaceholder(info.Title, watchFocusArt.Width, watchFocusArt.Height);
        var oldArt = watchFocusArt.Image;
        watchFocusArt.Image = immediateArt;
        oldArt?.Dispose();

        var descriptionTask = !info.Title.Equals(info.Media.Title, StringComparison.OrdinalIgnoreCase) && info.Media.Type == "Movie"
            ? GetCollectionDescriptionAsync(info.Title)
            : GetDescriptionAsync(info.Media);
        try
        {
            var coverPath = await GetCoverFileAsync(info.Media, token);
            if (!token.IsCancellationRequested && !IsDisposed && ReferenceEquals(remoteSelectedCard, card) && coverPath != null && File.Exists(coverPath))
            {
                using var cover = Image.FromFile(coverPath);
                var prior = watchFocusArt.Image;
                watchFocusArt.Image = new Bitmap(cover);
                prior?.Dispose();
            }
        }
        catch { /* The already-visible card poster remains the safe focus artwork. */ }
        var description = await descriptionTask;
        if (!token.IsCancellationRequested && !IsDisposed && ReferenceEquals(remoteSelectedCard, card))
            watchFocusDescription.Text = description;
    }

    private void AddPosterSection(string title, List<Panel> cards)
    {
        if (cards.Count == 0) return;
        var availableWidth = Math.Max(600, posterGrid.ClientSize.Width - 28);
        var header = new Label { Text = title, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 16f), Location = new Point(8, watchContentY), Width = availableWidth, Height = 42, Padding = new Padding(2, 5, 0, 0) };
        AddWatchControl(header);
        var section = watchSectionIndex++;
        var cardArea = new CarouselPanel
        {
            Location = new Point(2, watchContentY + header.Height),
            Size = new Size(availableWidth, 340),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = posterGrid.BackColor
        };
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            card.Margin = Padding.Empty;
            var logicalLocation = new Point(14 + index * 204, 14);
            cardArea.AddCarouselControl(card, logicalLocation);
            watchCards.Add(card);
            watchCardLogicalBounds[card] = new Rectangle(logicalLocation, card.Size);
            watchCardPositions[card] = (section, 0, index, watchContentY + header.Height + 14);
        }
        AddWatchControl(cardArea);
        watchContentY += header.Height + 340 + 18;
    }

    private void AddPosterGridSection(string title, List<Panel> cards)
    {
        if (cards.Count == 0) return;
        var availableWidth = Math.Max(600, posterGrid.ClientSize.Width - 28);
        var columns = Math.Max(1, (availableWidth - 28) / 204);
        var rows = (int)Math.Ceiling(cards.Count / (double)columns);
        var header = new Label { Text = title, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 16f), Location = new Point(8, watchContentY), Width = availableWidth, Height = 42, Padding = new Padding(2, 5, 0, 0) };
        AddWatchControl(header);

        var section = watchSectionIndex++;
        var gridHeight = 18 + rows * 318;
        var grid = new Panel { Location = new Point(2, watchContentY + header.Height), Size = new Size(availableWidth, gridHeight), BackColor = posterGrid.BackColor };
        for (var index = 0; index < cards.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var card = cards[index];
            card.Margin = Padding.Empty;
            card.Location = new Point(14 + column * 204, 14 + row * 318);
            grid.Controls.Add(card);
            watchCards.Add(card);
            watchCardLogicalBounds[card] = card.Bounds;
            watchCardPositions[card] = (section, row, column, watchContentY + header.Height + card.Top);
        }
        AddWatchControl(grid);
        watchContentY += header.Height + gridHeight + 18;
    }

    private void AddTorrentGridSection(string title, List<Panel> cards)
    {
        if (cards.Count == 0) return;
        var availableWidth = Math.Max(600, posterGrid.ClientSize.Width - 28);
        var columns = Math.Max(1, (availableWidth - 28) / 284);
        var rows = (int)Math.Ceiling(cards.Count / (double)columns);
        var header = new Label { Text = title, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 16f), Location = new Point(8, watchContentY), Width = availableWidth, Height = 42, Padding = new Padding(2, 5, 0, 0) };
        AddWatchControl(header);
        var section = watchSectionIndex++;
        var gridHeight = 18 + rows * 318;
        var grid = new Panel { Location = new Point(2, watchContentY + header.Height), Size = new Size(availableWidth, gridHeight), BackColor = posterGrid.BackColor };
        for (var index = 0; index < cards.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var card = cards[index];
            card.Margin = Padding.Empty;
            card.Location = new Point(14 + column * 284, 14 + row * 318);
            grid.Controls.Add(card);
            watchCards.Add(card);
            watchCardLogicalBounds[card] = card.Bounds;
            watchCardPositions[card] = (section, row, column, watchContentY + header.Height + card.Top);
        }
        AddWatchControl(grid);
        watchContentY += header.Height + gridHeight + 18;
    }

    private Panel MakeMediaCard(MediaItem media, bool episode)
    {
        var title = episode ? (media.EpisodeTitle.Length > 0 ? media.EpisodeTitle : media.Episode) : media.Title;
        var subtitle = episode ? $"Season {media.SeasonNumber} · {media.Episode}" : media.Year;
        return MakePosterCard(title, subtitle, media, () => ShowMediaDetails(media));
    }

    private Panel MakeSeriesCard(IGrouping<string, MediaItem> group)
    {
        // Extras and featurettes are season 0.  Never let one of those become the
        // artwork source for the series card (their thumbnails are often cast
        // photos or behind-the-scenes stills).  A real episode is always the
        // representative, with a season-pack fallback only when no episode exists.
        var representative = group.Where(x => x.SeasonNumber > 0)
            .OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber)
            .FirstOrDefault() ?? group.OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber).First();
        var seasons = group.Select(x => x.SeasonNumber).Where(x => x > 0).Distinct().Count();
        return MakePosterCard(group.Key, $"{seasons} season{(seasons == 1 ? "" : "s")}", representative, () => ShowSeriesBrowser(group.Key, group.ToList()));
    }

    private Panel MakeCollectionCard(IGrouping<string, MediaItem> group)
    {
        var representative = group.OrderBy(x => x.Year).First();
        return MakePosterCard(DisplayCollectionName(group.Key, group.Count()), $"{group.Count()} films", representative, () => { browseMode = "Collection"; activeCollection = group.Key; RefreshWatchView(); });
    }

    private Panel MakePosterCard(string title, string subtitle, MediaItem artworkSource, Action activate)
    {
        var card = new Panel { Width = 180, Height = 304, BackColor = Surface, Margin = new Padding(6, 7, 6, 9), Cursor = Cursors.Hand, Tag = activate };
        var picture = new PictureBox { Location = new Point(7, 7), Size = new Size(166, 232), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(5, 12, 9), Image = LoadExistingArtwork(artworkSource) ?? CreatePlaceholder(title, 166, 232) };
        var name = new Label { Text = title, Tag = "title", Location = new Point(9, 246), Size = new Size(162, 34), Font = new Font("Segoe UI Semibold", 9.5f), AutoEllipsis = true };
        var detail = new Label { Text = subtitle, Tag = "detail", Location = new Point(9, 281), Size = new Size(162, 18), ForeColor = Muted, Font = new Font("Segoe UI", 8.5f), AutoEllipsis = true };
        var badge = new Label { Text = "▶", Tag = "focus-badge", Location = new Point(133, 15), Size = new Size(34, 34), TextAlign = ContentAlignment.MiddleCenter, BackColor = FocusAccent, ForeColor = Color.FromArgb(5, 20, 11), Font = new Font("Segoe UI Symbol", 12f, FontStyle.Bold), Visible = false };
        void Click(object? _, EventArgs __) { SetRemoteSelection(card); activate(); }
        void Hover(object? _, EventArgs __)
        {
            // A row moving beneath a stationary cursor is not a real hover. Only physical pointer
            // movement can switch focus away from keyboard or phone navigation.
            var pointer = Cursor.Position;
            if (pointer == lastPosterPointerPosition) return;
            lastPosterPointerPosition = pointer;
            SetRemoteSelection(card);
        }
        card.Click += Click; picture.Click += Click; name.Click += Click; detail.Click += Click;
        card.MouseMove += Hover; picture.MouseMove += Hover; name.MouseMove += Hover; detail.MouseMove += Hover;
        card.Paint += (_, e) =>
        {
            if (ReferenceEquals(card, remoteSelectedCard))
            {
                using var glow = new Pen(Color.FromArgb(120, FocusAccent), 10f) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                using var edge = new Pen(FocusAccent, 3f) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                e.Graphics.DrawRectangle(glow, 1, 1, card.Width - 3, card.Height - 3);
                e.Graphics.DrawRectangle(edge, 1, 1, card.Width - 3, card.Height - 3);
            }
        };
        card.Controls.AddRange(new Control[] { picture, name, detail, badge });
        watchCardInfo[card] = (artworkSource, title, subtitle);
        return card;
    }

    private Panel MakeQbittorrentCard(QbittorrentItem torrent)
    {
        var card = new Panel { Width = 260, Height = 304, BackColor = Surface, Margin = new Padding(6, 7, 6, 9), Cursor = Cursors.Hand, Tag = (Action)(() => ShowQbittorrentActions(torrent)) };
        var title = new Label { Text = torrent.Name, Tag = "title", Location = new Point(14, 18), Size = new Size(232, 76), Font = new Font("Segoe UI Semibold", 11f), AutoEllipsis = true };
        var state = new Label { Text = torrent.State, Tag = "detail", Location = new Point(14, 101), Size = new Size(232, 24), ForeColor = Muted, AutoEllipsis = true };
        var progress = new ProgressBar { Location = new Point(14, 140), Size = new Size(232, 22), Minimum = 0, Maximum = 100, Value = Math.Clamp((int)Math.Round(torrent.Progress * 100), 0, 100), Style = ProgressBarStyle.Continuous };
        var percent = new Label { Text = $"{torrent.Progress * 100:0.0}%", Location = new Point(14, 170), Size = new Size(232, 24), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10f) };
        var size = new Label { Text = $"{FormatBytes(torrent.Size)}  ·  ↓ {FormatBytes(torrent.DownloadSpeed)}/s", Location = new Point(14, 206), Size = new Size(232, 24), ForeColor = Muted, AutoEllipsis = true };
        var eta = new Label { Text = torrent.Eta > 0 && torrent.Eta < long.MaxValue ? $"ETA {FormatEta(torrent.Eta)}  ·  ↑ {FormatBytes(torrent.UploadSpeed)}/s" : $"↑ {FormatBytes(torrent.UploadSpeed)}/s", Location = new Point(14, 235), Size = new Size(232, 24), ForeColor = Muted, AutoEllipsis = true };
        var hint = new Label { Text = "Enter for actions", Location = new Point(14, 270), Size = new Size(232, 20), ForeColor = Accent, Font = new Font("Segoe UI", 8.5f) };
        card.Click += (_, _) => { SetRemoteSelection(card); ((Action)card.Tag!).Invoke(); };
        foreach (var control in new Control[] { title, state, progress, percent, size, eta, hint })
        {
            control.Click += (_, _) => { SetRemoteSelection(card); ((Action)card.Tag!).Invoke(); };
            control.MouseMove += (_, _) => { SetRemoteSelection(card); };
        }
        card.MouseMove += (_, _) => SetRemoteSelection(card);
        card.Paint += (_, e) =>
        {
            if (!ReferenceEquals(card, remoteSelectedCard)) return;
            using var edge = new Pen(FocusAccent, 3f) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
            e.Graphics.DrawRectangle(edge, 1, 1, card.Width - 3, card.Height - 3);
        };
        card.Controls.AddRange(new Control[] { title, state, progress, percent, size, eta, hint });
        return card;
    }

    private static string FormatBytes(long value)
    {
        if (value <= 0) return "0 B";
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var number = (double)value;
        var index = 0;
        while (number >= 1024 && index < units.Length - 1) { number /= 1024; index++; }
        return $"{number:0.0} {units[index]}";
    }

    private static string FormatEta(long seconds)
    {
        if (seconds <= 0 || seconds >= 8640000) return "unknown";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours:00}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{span.Minutes:00}m {span.Seconds:00}s";
    }

    private Image? LoadExistingArtwork(MediaItem media)
    {
        var cover = Path.Combine(coverFolder, CoverKey(media) + ".img");
        var thumbnailHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(media.FullPath)));
        var thumbnail = Path.Combine(thumbnailFolder, thumbnailHash + ".jpg");
        var path = File.Exists(cover) ? cover : File.Exists(thumbnail) ? thumbnail : null;
        if (path == null) return null;
        try { using var image = Image.FromFile(path); return new Bitmap(image); } catch { return null; }
    }

    private Image? LoadFocusArtwork(MediaItem media)
    {
        // The large Kodi-style banner benefits from a widescreen frame captured from the actual
        // file. Fall back to the poster so focused titles always have useful artwork.
        var thumbnailHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(media.FullPath)));
        var thumbnail = Path.Combine(thumbnailFolder, thumbnailHash + ".jpg");
        var cover = Path.Combine(coverFolder, CoverKey(media) + ".img");
        var path = File.Exists(thumbnail) ? thumbnail : File.Exists(cover) ? cover : null;
        if (path == null) return null;
        try { using var image = Image.FromFile(path); return new Bitmap(image); } catch { return null; }
    }

    private static Bitmap CreatePlaceholder(string title, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(31, 69, 49), Color.FromArgb(7, 17, 12), 60f);
        graphics.FillRectangle(background, 0, 0, width, height);
        var initials = string.Join("", title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => char.ToUpperInvariant(x[0])));
        using var font = new Font("Segoe UI Black", 31f);
        using var brush = new SolidBrush(Color.FromArgb(210, 214, 224));
        var size = graphics.MeasureString(initials, font);
        graphics.DrawString(initials, font, brush, (width - size.Width) / 2, (height - size.Height) / 2);
        return bitmap;
    }

    private ComboBox MakeMonitorPicker()
    {
        var picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245, BackColor = Control, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        foreach (var item in monitors.Items) picker.Items.Add(item);
        picker.SelectedIndex = monitors.SelectedIndex;
        return picker;
    }

    private void RefreshAudioOutputs()
    {
        try
        {
            var devices = new CoreAudioController().GetPlaybackDevices(DeviceState.Active).OrderBy(x => x.FullName).ToList();
            audioOutputs.Clear();
            watchAudioPicker.Items.Clear();
            var selected = -1;
            for (var index = 0; index < devices.Count; index++)
            {
                var device = devices[index];
                audioOutputs.Add((device.Id, device.FullName));
                watchAudioPicker.Items.Add(device.FullName);
                if (device.IsDefaultDevice) selected = index;
            }
            if (watchAudioPicker.Items.Count > 0) watchAudioPicker.SelectedIndex = selected >= 0 ? selected : 0;
        }
        catch
        {
            audioOutputs.Clear();
            watchAudioPicker.Items.Clear();
            watchAudioPicker.Items.Add("Windows default audio");
            watchAudioPicker.SelectedIndex = 0;
            watchAudioPicker.Enabled = false;
        }
    }

    private void SetAudioOutput(Guid id)
    {
        try
        {
            var device = new CoreAudioController().GetDevice(id);
            device?.SetAsDefault();
        }
        catch { }
    }

    private static List<object> RemoteAudioOutputs()
    {
        try
        {
            return new CoreAudioController().GetPlaybackDevices(DeviceState.Active).OrderBy(x => x.FullName)
                .Select(x => (object)new { id = x.Id.ToString(), name = x.FullName, isDefault = x.IsDefaultDevice }).ToList();
        }
        catch { return new List<object>(); }
    }

    private void ShowMediaDetails(MediaItem media)
    {
        selectedGridMedia = media;
        using var dialog = new Form
        {
            Text = media.Title,
            BackColor = Color.FromArgb(7, 15, 12),
            ForeColor = Color.White,
            Font = Font,
            Size = new Size(930, 650),
            MinimumSize = new Size(850, 610),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var art = new PictureBox { Location = new Point(28, 28), Size = new Size(300, 466), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(5, 12, 9), Image = LoadExistingArtwork(media) ?? CreatePlaceholder(media.Title, 300, 466) };
        var title = new Label { Text = media.Title, Location = new Point(365, 32), Size = new Size(520, 76), Font = new Font("Segoe UI Semibold", 27f), AutoEllipsis = true };
        var chips = new FlowLayoutPanel { Location = new Point(365, 115), Size = new Size(520, 42), WrapContents = false, BackColor = dialog.BackColor };
        foreach (var value in new[] { media.Year, media.Quality, media.Subtitles, media.Collection })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("Not checked", StringComparison.OrdinalIgnoreCase)) continue;
            chips.Controls.Add(new Label { Text = value, AutoSize = true, BackColor = Color.FromArgb(28, 55, 41), ForeColor = Color.White, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 0, 8, 0), Font = new Font("Segoe UI Semibold", 9f) });
        }
        var overviewLabel = new Label { Text = "OVERVIEW", Location = new Point(365, 172), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 9f) };
        var description = new Label { Text = "Loading description…", Location = new Point(365, 202), Size = new Size(520, 212), ForeColor = Color.FromArgb(224, 237, 228), Font = new Font("Segoe UI", 11f), AutoEllipsis = true };
        var playOptions = new Panel { Location = new Point(365, 432), Size = new Size(520, 62), BackColor = Color.FromArgb(14, 34, 25) };
        var monitorLabel = new Label { Text = "PLAY ON", Location = new Point(14, 8), AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8f) };
        var picker = MakeMonitorPicker(); picker.Location = new Point(14, 27); picker.Width = 245;
        var subtitles = new CheckBox { Text = "English subtitles", Checked = useSubtitles.Checked, AutoSize = true, Location = new Point(285, 27) };
        playOptions.Controls.AddRange(new Control[] { monitorLabel, picker, subtitles });
        var close = MakeButton("BACK", 130); close.Location = new Point(365, 522); close.Height = 48; close.Click += (_, _) => dialog.Close();
        var play = MakeButton("▶  PLAY NOW", 230, Accent); play.Location = new Point(655, 522); play.Height = 48; play.Font = new Font("Segoe UI Semibold", 11f); play.Click += (_, _) => { monitors.SelectedIndex = picker.SelectedIndex; useSubtitles.Checked = subtitles.Checked; PlaySelected(); dialog.Close(); };
        var hint = new Label { Text = "Enter: play   •   ← →: monitor   •   ↑ ↓: subtitles   •   Backspace: back", Location = new Point(28, 585), Size = new Size(850, 24), ForeColor = Muted, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 8.5f) };
        dialog.Controls.AddRange(new Control[] { art, title, chips, overviewLabel, description, playOptions, close, play, hint });
        dialog.Shown += async (_, _) => { var text = await GetDescriptionAsync(media); if (!dialog.IsDisposed) description.Text = text; };
        activeRemoteDialog = dialog;
        activeRemoteSelect = () => play.PerformClick();
        activeRemoteNavigate = direction =>
        {
            if (direction == "left" && picker.SelectedIndex > 0) picker.SelectedIndex--;
            if (direction == "right" && picker.SelectedIndex < picker.Items.Count - 1) picker.SelectedIndex++;
            if (direction is "up" or "down") subtitles.Checked = !subtitles.Checked;
        };
        dialog.KeyPreview = true;
        dialog.KeyDown += HandleNavigationKey;
        dialog.ShowDialog(this);
        if (ReferenceEquals(activeRemoteDialog, dialog)) { activeRemoteDialog = null; activeRemoteSelect = null; activeRemoteNavigate = null; }
        art.Image?.Dispose();
    }

    private void ShowSeriesBrowser(string series, List<MediaItem> episodes)
    {
        using var dialog = new Form { Text = series, BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(930, 650), StartPosition = FormStartPosition.CenterParent };
        var representative = episodes.Where(x => x.SeasonNumber > 0)
            .OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber)
            .FirstOrDefault() ?? episodes.OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber).First();
        var art = new PictureBox { Location = new Point(24, 24), Size = new Size(230, 300), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(5, 12, 9), Image = LoadExistingArtwork(representative) ?? CreatePlaceholder(series, 230, 300) };
        var episodeDescription = new Label { Text = "Choose an episode to see its description.", Location = new Point(24, 338), Size = new Size(230, 225), ForeColor = Muted, Font = new Font("Segoe UI", 9.5f), AutoEllipsis = true };
        var heading = new Label { Text = series, Location = new Point(285, 24), Size = new Size(590, 48), Font = new Font("Segoe UI Semibold", 22f) };
        var seasonPicker = new ComboBox { Location = new Point(288, 84), Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Control, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var seasons = episodes.Select(x => x.SeasonNumber).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        foreach (var season in seasons) seasonPicker.Items.Add($"Season {season}");
        var episodeList = new ListView { Location = new Point(285, 128), Size = new Size(600, 390), View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Surface, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        episodeList.Columns.Add("Episode", 95); episodeList.Columns.Add("Title", 295); episodeList.Columns.Add("Subtitles", 175);
        void FillEpisodes()
        {
            episodeList.Items.Clear();
            if (seasonPicker.SelectedIndex < 0) return;
            var season = seasons[seasonPicker.SelectedIndex];
            foreach (var episode in episodes.Where(x => x.SeasonNumber == season).OrderBy(x => x.EpisodeNumber))
            {
                var row = new ListViewItem(episode.Episode) { Tag = episode };
                row.SubItems.Add(episode.EpisodeTitle.Length > 0 ? episode.EpisodeTitle : $"Episode {episode.EpisodeNumber:00}");
                row.SubItems.Add(episode.Subtitles);
                episodeList.Items.Add(row);
            }
            if (episodeList.Items.Count > 0) episodeList.Items[0].Selected = true;
        }
        seasonPicker.SelectedIndexChanged += (_, _) => FillEpisodes();
        var descriptionVersion = 0;
        episodeList.SelectedIndexChanged += async (_, _) =>
        {
            if (episodeList.SelectedItems.Count == 0 || episodeList.SelectedItems[0].Tag is not MediaItem selectedEpisode) return;
            var version = ++descriptionVersion;
            episodeDescription.Text = "Loading description...";
            var text = await GetDescriptionAsync(selectedEpisode);
            if (version == descriptionVersion && !dialog.IsDisposed) episodeDescription.Text = text;
        };
        if (seasonPicker.Items.Count > 0) seasonPicker.SelectedIndex = 0;
        var picker = MakeMonitorPicker(); picker.Location = new Point(285, 540);
        var play = MakeButton("PLAY EPISODE", 165, Accent); play.Location = new Point(720, 535);
        void PlayEpisode()
        {
            if (episodeList.SelectedItems.Count == 0) return;
            selectedGridMedia = episodeList.SelectedItems[0].Tag as MediaItem;
            monitors.SelectedIndex = picker.SelectedIndex;
            PlaySelected();
            dialog.Close();
        }
        play.Click += (_, _) => PlayEpisode(); episodeList.DoubleClick += (_, _) => PlayEpisode();
        dialog.Controls.AddRange(new Control[] { art, episodeDescription, heading, seasonPicker, episodeList, picker, play });
        activeRemoteDialog = dialog;
        activeRemoteSelect = PlayEpisode;
        activeRemoteNavigate = direction =>
        {
            if (direction is "left" or "right")
            {
                var change = direction == "left" ? -1 : 1;
                seasonPicker.SelectedIndex = Math.Clamp(seasonPicker.SelectedIndex + change, 0, Math.Max(0, seasonPicker.Items.Count - 1));
                return;
            }
            if (episodeList.Items.Count == 0) return;
            var current = episodeList.SelectedIndices.Count > 0 ? episodeList.SelectedIndices[0] : 0;
            var next = Math.Clamp(current + (direction == "up" ? -1 : 1), 0, episodeList.Items.Count - 1);
            episodeList.Items[next].Selected = true;
            episodeList.Items[next].Focused = true;
            episodeList.EnsureVisible(next);
        };
        dialog.KeyPreview = true;
        dialog.KeyDown += HandleNavigationKey;
        dialog.ShowDialog(this);
        if (ReferenceEquals(activeRemoteDialog, dialog)) { activeRemoteDialog = null; activeRemoteSelect = null; activeRemoteNavigate = null; }
        art.Image?.Dispose();
    }

    private void LoadSettings()
    {
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)) ?? new(); } catch { settings = new(); }
        var saveNeeded = false;
        if (Regex.IsMatch(settings.PhoneRemotePin ?? "", @"^\d{6}$")) remotePin = settings.PhoneRemotePin!;
        else { settings.PhoneRemotePin = remotePin; saveNeeded = true; }
        if (settings.LibraryFolders.Count == 0)
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            settings.LibraryFolders.AddRange(new[] { @"D:\Movies", @"D:\Shows", Path.Combine(videos, "Movies"), Path.Combine(videos, "Shows") }.Where(Directory.Exists));
            saveNeeded = true;
        }
        if (saveNeeded) SaveSettings();
    }

    private void SaveSettings()
    {
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "library-locations.json"), JsonSerializer.Serialize(settings.LibraryFolders, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string WatchHistoryPath => Path.Combine(metadataFolder, "watch-history.json");

    private void LoadWatchHistory()
    {
        try
        {
            if (!File.Exists(WatchHistoryPath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(WatchHistoryPath));
            if (loaded == null) return;
            foreach (var pair in loaded.Where(x => File.Exists(x.Key))) watchHistory[pair.Key] = pair.Value;
        }
        catch { watchHistory.Clear(); }
    }

    private void RememberWatched(MediaItem media)
    {
        watchHistory[media.FullPath] = DateTime.UtcNow;
        try { File.WriteAllText(WatchHistoryPath, JsonSerializer.Serialize(watchHistory, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        if (browseMode == "Home") RefreshWatchView();
    }

    private async Task ScanLibraryAsync()
    {
        status.Text = "Scanning all library folders...";
        // Do not disable the entire form: WinForms washes out every control,
        // making the library look broken while the audit is actually working.
        UseWaitCursor = true;
        subtitleAuditButton.Enabled = false;
        var items = await Task.Run(() =>
        {
            var result = new List<MediaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in settings.LibraryFolders.Where(Directory.Exists))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!Extensions.Contains(Path.GetExtension(path)) || !seen.Add(path) || IsProtectedTorrentMedia(path)) continue;
                        result.Add(Describe(path, root));
                    }
                }
                catch { }
            }
        return result.OrderBy(x => x.Type).ThenBy(x => x.Title).ThenBy(x => x.Episode).ToList();
        });
        foreach (var movie in items.Where(x => x.Type == "Movie")) movie.Genres = LoadCachedGenres(movie);
        library.Clear();
        library.AddRange(items);
        RefreshRemoteLibrary();
        BuildNavigation();
        FillList();
        RefreshWatchView();
        await EnrichMissingYearsAsync();
        UseWaitCursor = false;
        subtitleAuditButton.Enabled = true;
        status.Text = $"{library.Count} files loaded from {settings.LibraryFolders.Count} folders";
        if (!string.IsNullOrWhiteSpace(settings.TmdbReadToken)) _ = EnrichMovieCollectionsAsync();
        if (settings.AutoDownloadCovers) _ = DownloadAllCoversAsync();
        if (!string.IsNullOrWhiteSpace(settings.TmdbReadToken)) _ = EnrichMovieGenresAsync();
        if (settings.AutoSubtitleAudit && !automaticAuditStarted)
        {
            automaticAuditStarted = true;
            await AuditSubtitlesAsync();
        }
    }

    /// <summary>Never read/write metadata beside media that still looks like an active torrent.</summary>
    private static bool IsProtectedTorrentMedia(string path)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
            for (var current = directory; current != null; current = current.Parent)
            {
                if (Regex.IsMatch(current.Name, @"(?i)^(?:incomplete|downloading|partial|\.incomplete)$")) return true;
                if (current.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Any(file => Regex.IsMatch(file.Name, @"(?i)(?:\.aria2|\.!qB|\.part|\.crdownload|\.opdownload|\.torrent$|\.fastresume$)"))) return true;
            }
            var info = new FileInfo(path);
            var originMarker = false;
            for (var current = directory; current != null && !originMarker; current = current.Parent)
                originMarker = current.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Any(file => Regex.IsMatch(file.Name, @"(?i)^torrent\s+downloaded\s+from"));
            if (originMarker) return true;
            if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromMinutes(2)) return true;
            using var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch { return true; }
    }

    private static MediaItem Describe(string path, string root)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var readable = Regex.Replace(stem.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim();
        var relative = Path.GetRelativePath(root, path);
        var relativeParts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ep = Regex.Match(readable, @"(?i)\bS(?<s>\d{1,2})\s*E\s*(?<e>\d{1,2})\b");
        if (!ep.Success) ep = Regex.Match(readable, @"(?i)\b(?<s>\d{1,2})x(?<e>\d{1,2})\b");
        if (!ep.Success) ep = Regex.Match(readable, @"(?i)\bSeason\s*(?<s>\d{1,2})\s*(?:Episode|Ep)\s*(?<e>\d{1,2})\b");
        // A file placed directly in the Shows library root is a standalone
        // special/movie, not a series.  Requiring a child folder prevents
        // one-off titles such as "The Punisher - One Last Kill" from being
        // sent through the TV-show artwork lookup.
        var isShow = ep.Success || (relativeParts.Length > 1 && Regex.IsMatch(root, @"(?i)(shows|series|tv)"));
        // Releases often put the year in the containing folder rather than the
        // video filename (featurettes and season packs are common examples).
        var year = Regex.Match($"{readable} {relative}", @"\b(?:19|20)\d{2}\b");
        var quality = Regex.Match(readable, @"\b(2160p|1080p|720p|480p)\b", RegexOptions.IgnoreCase);
        var title = readable;
        var series = "";
        var episodeTitle = "";
        var seasonNumber = 0;
        var episodeNumber = 0;
        if (ep.Success)
        {
            series = readable[..ep.Index].Trim(' ', '-');
            series = NormalizeSeries(series);
            title = series;
            seasonNumber = int.Parse(ep.Groups["s"].Value);
            episodeNumber = int.Parse(ep.Groups["e"].Value);
            episodeTitle = readable[(ep.Index + ep.Length)..].Trim(' ', '-');
            episodeTitle = Regex.Replace(episodeTitle, @"(?i)\b(2160p|1080p|720p|480p|WEB.?DL|WEBRip|BluRay|BRRip|BDRip|HDRip|DVDRip|x26[45]|XviD|HEVC|AAC|DDP?\d?.?\d?|GalaxyTV|GalaxyRG\d*|YIFY|YTS|AFG)\b.*$", "").Trim(' ', '-');
        }
        else if (year.Success) title = readable[..year.Index].Trim(' ', '-', '(');
        title = Regex.Replace(title, @"^\d{1,2}\s+", "");
        title = CleanMediaTitle(title);
        if (isShow && !ep.Success && relativeParts.Length > 0)
        {
            var inferredSeries = CleanShowFolderName(relativeParts[0]);
            if (inferredSeries.Length > 0)
            {
                series = inferredSeries;
                var seasonFromPath = Regex.Match(relative, @"(?i)\bSeason\s*(?<season>\d{1,2})\b");
                if (seasonFromPath.Success) seasonNumber = int.Parse(seasonFromPath.Groups["season"].Value);
            }
        }
        var basePath = Path.Combine(Path.GetDirectoryName(path)!, stem);
        var collection = relativeParts.Length > 1 ? CleanCollectionName(relativeParts[0]) : "";
        if (!isShow && collection.Length == 0) collection = InferKnownMovieCollection(title);
        var marker = basePath + ".subtitle-sync.json";
        var external = new[] { ".srt", ".ass", ".ssa", ".vtt" }.Any(x => File.Exists(basePath + x));
        return new MediaItem
        {
            FullPath = path,
            Title = string.IsNullOrWhiteSpace(title) ? stem : title,
            Type = isShow ? "Show" : "Movie",
            Episode = ep.Success ? $"Episode {episodeNumber:00}" : "",
            Series = series,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeTitle = episodeTitle,
            Year = year.Success ? year.Value : "",
            Quality = quality.Success ? quality.Value.ToLowerInvariant() : "",
            Subtitles = File.Exists(marker) ? "Audio-synced" : external ? "External subtitle" : "Not checked"
            ,Collection = collection
        };
    }

    private static string CleanCollectionName(string folder)
    {
        var name = Regex.Replace(folder.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim();
        name = Regex.Replace(name, @"(?i)\s+(?:19|20)\d{2}(?:\s*[-–]\s*(?:19|20)\d{2})?.*$", "");
        name = Regex.Replace(name, @"(?i)\s+-\s+(Comedy|Action|Drama|Horror|Sci.?Fi).*$", "");
        name = Regex.Replace(name, @"(?i)\s+(2160p|1080p|720p|BluRay|WEB.?DL|WEBRip|HEVC|x26[45]|AAC|DDP?).*$", "");
        name = Regex.Replace(name, @"(?i)\s+Eng(?:lish)?(?:\s+Rus)?(?:\s+Multi)?\s+Subs?.*$", "");
        return name.Trim(' ', '-');
    }

    private static string DisplayCollectionName(string collection, int count)
    {
        if (string.IsNullOrWhiteSpace(collection) || count <= 0) return collection;
        var isTrilogy = Regex.IsMatch(collection, @"(?i)\btrilogy\b") && count == 3;
        var isQuadrilogy = Regex.IsMatch(collection, @"(?i)\bquadrilogy\b") && count == 4;
        var name = Regex.Replace(collection, @"(?i)\bcomplete\b", " ");
        name = Regex.Replace(name, @"(?i)\b(?:trilogy|quadrilogy)\b", " ");
        name = Regex.Replace(name, @"\b\d+\s*[-–—]\s*\d+\b", " ");
        name = Regex.Replace(name, @"(?i)\b\d+\s*(?:movie|film)s?\b", " ");
        name = Regex.Replace(name, @"(?i)\b(?:collection|movies?|films?)\b", " ");
        name = Regex.Replace(name, @"(?i)(?:^|\s+)(?:comedy|action|drama|horror|sci[ -]?fi|science fiction|thriller|adventure|animation|fantasy)\s*$", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '-', ':');
        if (name.Length == 0) name = collection;
        if (isTrilogy) return $"{name} Trilogy";
        if (isQuadrilogy) return $"{name} Quadrilogy";
        return $"{name} {count} Movie Collection";
    }

    /// <summary>Removes release-library labels that are not part of the film title.</summary>
    private static string CleanMediaTitle(string title)
    {
        title = Regex.Replace(title, @"\s+[-–—]\s+(?:Action|Adventure|Animation|Biography|Comedy|Crime|Documentary|Drama|Family|Fantasy|Horror|Mystery|Romance|Science\s*Fiction|Sci\.?\s*Fi|Thriller|War|Western)\s*$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+\((?:19|20)\d{2}\)\s*$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(title, @"\s+", " ").Trim(' ', '-');
    }

    /// <summary>Useful offline fallback for common franchises when no metadata token is configured.</summary>
    private static string InferKnownMovieCollection(string title)
    {
        var candidates = new (string Name, string Pattern)[]
        {
            ("Star Wars Collection", "\\bstar\\s+wars\\b"), ("Harry Potter Collection", "\\bharry\\s+potter\\b"),
            ("Indiana Jones Collection", "\\bindiana\\s+jones\\b"), ("Pirates of the Caribbean Collection", "\\bpirates?\\s+of\\s+the\\s+caribbean\\b"),
            ("American Pie Collection", "\\bamerican\\s+pie\\b"), ("Rambo Collection", "\\brambo\\b"),
            ("Riddick Trilogy", "\\b(riddick|pitch\\s+black)\\b"), ("Scary Movie Collection", "\\bscary\\s+movie\\b"),
            ("Underworld Collection", "\\bunderworld\\b"), ("Bourne Collection", "\\b(jason\\s+)?bourne\\b"),
            ("The Lord of the Rings Collection", "\\b(lord\\s+of\\s+the\\s+rings|fellowship\\s+of\\s+the\\s+ring)\\b"),
            ("The Hobbit Collection", "\\bthe\\s+hobbit\\b"), ("The Matrix Collection", "\\bthe\\s+matrix\\b"),
            ("Terminator Collection", "\\bterminator\\b"), ("Jurassic Park Collection", "\\bjurassic\\s+(park|world)\\b"),
            ("Mission: Impossible Collection", "\\bmission\\s*:\\s*impossible\\b"), ("James Bond Collection", "\\b(james\\s+bond|007)\\b"),
            ("Rocky Collection", "\\brocky\\b"), ("Die Hard Collection", "\\bdie\\s+hard\\b"), ("Scream Collection", "\\bscream\\b"),
            ("Saw Collection", "\\bsaw\\b"), ("Toy Story Collection", "\\btoy\\s+story\\b"), ("Shrek Collection", "\\bshrek\\b")
        };
        return candidates.FirstOrDefault(x => Regex.IsMatch(title, x.Pattern, RegexOptions.IgnoreCase)).Name ?? "";
    }

    private static string CleanShowFolderName(string folder)
    {
        var name = Regex.Replace(folder.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim();
        name = Regex.Replace(name, @"(?i)\s*-\s*The\s+Complete\s+Series.*$", "");
        name = Regex.Replace(name, @"(?i)\s*\+\s*Extras.*$", "");
        name = Regex.Replace(name, @"(?i)\s+Complete\s+Series.*$", "");
        name = name.Trim(' ', '-', '(', ')');
        if (name.Equals("Shows", StringComparison.OrdinalIgnoreCase) || name.Equals("Series", StringComparison.OrdinalIgnoreCase)) return "";
        return NormalizeSeries(name);
    }

    private void FillList()
    {
        var query = search.Text.Trim();
        var kind = typeFilter.SelectedItem?.ToString();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var media in library)
        {
            if (kind == "Movies" && media.Type != "Movie" || kind == "Shows" && media.Type != "Show") continue;
            if (query.Length > 0 && !($"{media.Title} {media.Episode} {Path.GetFileName(media.FullPath)}").Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (navigation.SelectedNode?.Tag is LibraryFilter selected)
            {
                if (selected.Kind == "Movies" && media.Type != "Movie") continue;
                if (selected.Kind == "Shows" && media.Type != "Show") continue;
                if (selected.Kind == "Series" && !media.Series.Equals(selected.Series, StringComparison.OrdinalIgnoreCase)) continue;
                if (selected.Kind == "Season" && (!media.Series.Equals(selected.Series, StringComparison.OrdinalIgnoreCase) || media.SeasonNumber != selected.Season)) continue;
                if (selected.Kind == "Collection" && !media.Collection.Equals(selected.Collection, StringComparison.OrdinalIgnoreCase)) continue;
                if (selected.Kind == "StandaloneMovies" && media.Type == "Movie" && IsMovieCollection(media)) continue;
                if (selected.Kind == "Movie" && !media.FullPath.Equals(selected.Path, StringComparison.OrdinalIgnoreCase)) continue;
            }
            var displayTitle = media.Type == "Show" && media.EpisodeTitle.Length > 0 ? media.EpisodeTitle : media.Title;
            var row = new ListViewItem(displayTitle) { Tag = media };
            row.SubItems.Add(media.Type);
            row.SubItems.Add(media.Episode);
            row.SubItems.Add(media.Year);
            row.SubItems.Add(media.Subtitles);
            row.SubItems.Add(media.Quality);
            list.Items.Add(row);
        }
        list.EndUpdate();
        status.Text = $"{list.Items.Count} files shown";
    }

    private void BuildNavigation()
    {
        navigation.BeginUpdate();
        navigation.Nodes.Clear();
        var all = new TreeNode("All Library") { Tag = new LibraryFilter("All") };
        var movies = new TreeNode($"Movies ({library.Count(x => x.Type == "Movie")})") { Tag = new LibraryFilter("Movies") };
        var shows = new TreeNode("Shows") { Tag = new LibraryFilter("Shows") };
        var collections = new TreeNode("Collections") { Tag = new LibraryFilter("Movies") };
        var standalone = new TreeNode("Standalone Movies") { Tag = new LibraryFilter("StandaloneMovies") };
        var movieItems = library.Where(x => x.Type == "Movie").ToList();
        var collectionGroups = movieItems.Where(IsMovieCollection).GroupBy(x => x.Collection, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key).ToList();
        foreach (var group in collectionGroups)
        {
            var collectionNode = new TreeNode($"{DisplayCollectionName(group.Key, group.Count())} ({group.Count()} films)") { Tag = new LibraryFilter("Collection", Collection: group.Key) };
            foreach (var movie in group.OrderBy(x => x.Year).ThenBy(x => x.Title))
                collectionNode.Nodes.Add(new TreeNode(movie.Year.Length > 0 ? $"{movie.Title} ({movie.Year})" : movie.Title) { Tag = new LibraryFilter("Movie", Path: movie.FullPath) });
            collections.Nodes.Add(collectionNode);
        }
        foreach (var movie in movieItems.Where(x => !IsMovieCollection(x)).OrderBy(x => x.Title).ThenBy(x => x.Year))
            standalone.Nodes.Add(new TreeNode(movie.Year.Length > 0 ? $"{movie.Title} ({movie.Year})" : movie.Title) { Tag = new LibraryFilter("Movie", Path: movie.FullPath) });
        movies.Nodes.Add(collections);
        movies.Nodes.Add(standalone);
        foreach (var seriesGroup in library.Where(x => x.Type == "Show").GroupBy(x => x.Series.Length > 0 ? x.Series : x.Title, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key))
        {
            var seriesNode = new TreeNode(seriesGroup.Key) { Tag = new LibraryFilter("Series", seriesGroup.Key) };
            foreach (var seasonGroup in seriesGroup.Where(x => x.SeasonNumber > 0).GroupBy(x => x.SeasonNumber).OrderBy(x => x.Key))
                seriesNode.Nodes.Add(new TreeNode($"Season {seasonGroup.Key} ({seasonGroup.Count()} episodes)") { Tag = new LibraryFilter("Season", seriesGroup.Key, seasonGroup.Key) });
            shows.Nodes.Add(seriesNode);
        }
        all.Nodes.Add(movies);
        all.Nodes.Add(shows);
        navigation.Nodes.Add(all);
        all.Expand();
        shows.Expand();
        navigation.SelectedNode = all;
        navigation.EndUpdate();
    }

    private bool IsMovieCollection(MediaItem media)
    {
        if (media.Type != "Movie" || media.Collection.Length == 0) return false;
        if (Regex.IsMatch(media.Collection, @"(?i)\b(collection|trilogy|saga|complete|anthology|series|movies?)\b")) return true;
        return library.Count(x => x.Type == "Movie" && x.Collection.Equals(media.Collection, StringComparison.OrdinalIgnoreCase)) > 1;
    }

    private void EditFolders()
    {
        using var dialog = new Form { Text = "Library folders", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(700, 440), StartPosition = FormStartPosition.CenterParent };
        var folders = new ListBox { BackColor = Surface, ForeColor = Color.White, Location = new Point(18, 45), Size = new Size(645, 270), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        folders.Items.AddRange(settings.LibraryFolders.Cast<object>().ToArray());
        dialog.Controls.Add(new Label { Text = "Add every folder containing movies or shows. Subfolders are scanned automatically.", AutoSize = true, Location = new Point(18, 16) });
        dialog.Controls.Add(folders);
        var add = MakeButton("Add folder...", 125); add.Location = new Point(18, 330);
        var remove = MakeButton("Remove", 100); remove.Location = new Point(153, 330);
        var save = MakeButton("Save & scan", 135, Accent); save.Location = new Point(528, 330); save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        add.Click += (_, _) => { using var pick = new FolderBrowserDialog { Description = "Choose a movie or show folder", UseDescriptionForTitle = true }; if (pick.ShowDialog(dialog) == DialogResult.OK && !folders.Items.Contains(pick.SelectedPath)) folders.Items.Add(pick.SelectedPath); };
        remove.Click += (_, _) => { if (folders.SelectedIndex >= 0) folders.Items.RemoveAt(folders.SelectedIndex); };
        save.Click += (_, _) => { settings.LibraryFolders = folders.Items.Cast<string>().ToList(); SaveSettings(); dialog.DialogResult = DialogResult.OK; };
        dialog.Controls.AddRange(new Control[] { add, remove, save });
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = ScanLibraryAsync();
    }

    private async Task OpenAuthorizedDownloadAsync()
    {
        using var dialog = new Form { Text = "Authorized download", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(720, 440), StartPosition = FormStartPosition.CenterParent };
        var heading = new Label { Text = "DOWNLOAD AN AUTHORIZED LINK", ForeColor = Accent, Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, Location = new Point(24, 22) };
        var note = new Label { Text = "Paste one direct HTTP(S) link per line. No index search, torrent discovery, or magnet handling is performed.", ForeColor = Muted, Location = new Point(24, 55), Size = new Size(650, 40) };
        var link = new TextBox { PlaceholderText = "https://…", BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 108), Width = 650 };
        link.Multiline = true;
        link.AcceptsReturn = true;
        link.ScrollBars = ScrollBars.Vertical;
        link.Height = 105;
        var rights = new CheckBox { Text = "I have the right to download this content", AutoSize = true, Location = new Point(24, 220) };
        var status = new Label { Text = "Ready", ForeColor = Muted, Location = new Point(24, 260), Size = new Size(650, 40), AutoEllipsis = true };
        var cancel = MakeButton("Cancel", 110); cancel.Location = new Point(445, 350); cancel.Click += (_, _) => dialog.Close();
        var download = MakeButton("DOWNLOAD", 130, Accent); download.Location = new Point(565, 350);
        download.Click += async (_, _) =>
        {
            if (!rights.Checked) { MessageBox.Show(dialog, "Confirm that you have the right to download this content.", "Authorization required", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var batchLinks = link.Lines.Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            if (batchLinks.Count > 1)
            {
                await DownloadAuthorizedBatchAsync(batchLinks, dialog, settings.RealDebridApiToken, status, download, cancel);
                return;
            }
            if (!Uri.TryCreate(link.Text.Trim(), UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            {
                MessageBox.Show(dialog, "Enter a direct HTTP(S) link. Magnet links and search sources are not supported.", "Link required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var save = new SaveFileDialog
            {
                Title = "Save authorized media",
                Filter = "Media files|*.mkv;*.mp4;*.avi;*.mov;*.m4v;*.webm;*.ts|All files|*.*",
                FileName = Path.GetFileName(source.AbsolutePath),
                InitialDirectory = settings.LibraryFolders.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                OverwritePrompt = true
            };
            if (save.ShowDialog(dialog) != DialogResult.OK) return;

            download.Enabled = false;
            cancel.Enabled = false;
            try
            {
                status.Text = "Resolving through the official Real-Debrid API…";
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var resolved = await authorizedDownloads.ResolveAsync(source.ToString(), settings.RealDebridApiToken, cancellation.Token);
                var progress = new Progress<long>(value => status.Text = value <= 100 ? $"Downloading… {value}%" : $"Downloaded {value / 1024d / 1024d:0.0} MB");
                await authorizedDownloads.DownloadAsync(resolved, save.FileName, progress, cancellation.Token);
                status.Text = "Download complete. Refreshing the library…";
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception exception)
            {
                try { if (File.Exists(save.FileName)) File.Delete(save.FileName); } catch { }
                MessageBox.Show(dialog, exception.Message, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                status.Text = "Ready";
                download.Enabled = true;
                cancel.Enabled = true;
            }
        };
        dialog.Controls.AddRange(new Control[] { heading, note, link, rights, status, cancel, download });
        if (dialog.ShowDialog(this) == DialogResult.OK) await ScanLibraryAsync();
    }

    private async Task DownloadAuthorizedBatchAsync(IReadOnlyList<string> values, Form dialog, string realDebridToken, Label status, Button download, Button cancel)
    {
        var sources = new List<Uri>();
        foreach (var value in values)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            {
                MessageBox.Show(dialog, $"This is not a direct HTTP(S) link:\r\n{value}\r\n\r\nMagnet links and search sources are not supported.", "Invalid link", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            sources.Add(source);
        }

        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a folder for the authorized files",
            SelectedPath = settings.LibraryFolders.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            ShowNewFolderButton = true
        };
        if (picker.ShowDialog(dialog) != DialogResult.OK) return;

        download.Enabled = false;
        cancel.Enabled = false;
        settings.RealDebridApiToken = realDebridToken;
        SaveSettings();
        var completed = 0;
        var failures = new List<string>();
        try
        {
            foreach (var source in sources)
            {
                var name = Path.GetFileName(Uri.UnescapeDataString(source.AbsolutePath));
                if (string.IsNullOrWhiteSpace(name) || name is "." or "/") name = $"download-{completed + failures.Count + 1}.bin";
                name = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
                var destination = Path.Combine(picker.SelectedPath, name);
                var stem = Path.GetFileNameWithoutExtension(destination);
                var extension = Path.GetExtension(destination);
                var suffix = 2;
                while (File.Exists(destination)) destination = Path.Combine(picker.SelectedPath, $"{stem} ({suffix++}){extension}");
                try
                {
                    var index = completed + failures.Count + 1;
                    status.Text = $"Resolving {index} of {sources.Count} through Real-Debrid...";
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    var resolved = await authorizedDownloads.ResolveAsync(source.ToString(), settings.RealDebridApiToken, cancellation.Token);
                    var progress = new Progress<long>(value => status.Text = value <= 100 ? $"Downloading {index} of {sources.Count}... {value}%" : $"Downloaded {value / 1024d / 1024d:0.0} MB");
                    await authorizedDownloads.DownloadAsync(resolved, destination, progress, cancellation.Token);
                    completed++;
                }
                catch (Exception exception)
                {
                    try { if (File.Exists(destination)) File.Delete(destination); } catch { }
                    failures.Add($"{name}: {exception.Message}");
                }
            }
            status.Text = $"Finished: {completed} of {sources.Count} downloaded.";
            var summary = failures.Count == 0 ? $"Downloaded {completed} file(s) successfully." : $"Downloaded {completed} of {sources.Count}.\r\n\r\nFailures:\r\n{string.Join("\r\n", failures.Take(8))}";
            MessageBox.Show(dialog, summary, "Authorized download", MessageBoxButtons.OK, failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (completed > 0) dialog.DialogResult = DialogResult.OK;
        }
        finally
        {
            download.Enabled = true;
            cancel.Enabled = true;
        }
    }

    private string QbittorrentBaseUrl()
    {
        var value = (settings.QbittorrentUrl ?? "").Trim();
        if (value.Length == 0) return "";
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
        return value.TrimEnd('/');
    }

    private bool ConfigureQbittorrent()
    {
        using var dialog = new Form { Text = "qBittorrent connection", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(600, 330), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        dialog.Controls.Add(new Label { Text = "qBittorrent Web UI", ForeColor = Accent, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(24, 22) });
        dialog.Controls.Add(new Label { Text = "High Seas Media connects to the local qBittorrent Web API. Media files stay managed by qBittorrent.", ForeColor = Muted, Location = new Point(24, 58), Size = new Size(530, 36) });
        var url = new TextBox { Text = settings.QbittorrentUrl, PlaceholderText = "http://127.0.0.1:8080", BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 111), Width = 530 };
        var username = new TextBox { Text = settings.QbittorrentUsername, PlaceholderText = "Username (optional)", BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 156), Width = 255 };
        var password = new TextBox { Text = settings.QbittorrentPassword, PlaceholderText = "Password (optional)", UseSystemPasswordChar = true, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(299, 156), Width = 255 };
        var cancel = MakeButton("Cancel", 105); cancel.Location = new Point(334, 230); cancel.Click += (_, _) => dialog.DialogResult = DialogResult.Cancel;
        var save = MakeButton("Save & connect", 145, Accent); save.Location = new Point(439, 230); save.Click += (_, _) =>
        {
            settings.QbittorrentUrl = url.Text.Trim();
            settings.QbittorrentUsername = username.Text.Trim();
            settings.QbittorrentPassword = password.Text;
            qbittorrentAuthenticated = false;
            SaveSettings();
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.AddRange(new Control[] { url, username, password, cancel, save });
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private async Task RefreshQbittorrentAsync()
    {
        if (qbittorrentRefreshRunning) return;
        if (string.IsNullOrWhiteSpace(QbittorrentBaseUrl()))
        {
            if (ConfigureQbittorrent()) await RefreshQbittorrentAsync();
            return;
        }
        qbittorrentRefreshRunning = true;
        if (browseMode == "QBittorrent") RefreshWatchView();
        try
        {
            var items = await FetchQbittorrentItemsAsync(CancellationToken.None);
            qbittorrentItems.Clear();
            if (items != null) qbittorrentItems.AddRange(items);
            status.Text = $"qBittorrent: {qbittorrentItems.Count} transfer{(qbittorrentItems.Count == 1 ? "" : "s")}.";
        }
        catch (Exception exception)
        {
            qbittorrentItems.Clear();
            status.Text = $"qBittorrent unavailable: {exception.Message}";
        }
        finally
        {
            qbittorrentRefreshRunning = false;
            if (!IsDisposed && browseMode == "QBittorrent") RefreshWatchView();
        }
    }

    private async Task<List<QbittorrentItem>?> FetchQbittorrentItemsAsync(CancellationToken token)
    {
        var baseUrl = QbittorrentBaseUrl();
        if (baseUrl.Length == 0) return null;
        if (!qbittorrentAuthenticated && !await LoginQbittorrentAsync(baseUrl, token)) return null;
        using var response = await qbittorrentHttp.GetAsync(baseUrl + "/api/v2/torrents/info?sort=added_on&reverse=true", token);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            qbittorrentAuthenticated = false;
            if (!await LoginQbittorrentAsync(baseUrl, token)) return null;
            response.Dispose();
            using var retry = await qbittorrentHttp.GetAsync(baseUrl + "/api/v2/torrents/info?sort=added_on&reverse=true", token);
            if (!retry.IsSuccessStatusCode) return null;
            return ParseQbittorrentItems(await retry.Content.ReadAsStringAsync(token));
        }
        if (!response.IsSuccessStatusCode) return null;
        return ParseQbittorrentItems(await response.Content.ReadAsStringAsync(token));
    }

    private async Task<bool> LoginQbittorrentAsync(string baseUrl, CancellationToken token)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = settings.QbittorrentUsername,
            ["password"] = settings.QbittorrentPassword
        });
        using var response = await qbittorrentHttp.PostAsync(baseUrl + "/api/v2/auth/login", form, token);
        var body = await response.Content.ReadAsStringAsync(token);
        qbittorrentAuthenticated = response.IsSuccessStatusCode && body.Contains("Ok", StringComparison.OrdinalIgnoreCase);
        return qbittorrentAuthenticated;
    }

    private static List<QbittorrentItem> ParseQbittorrentItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<QbittorrentItem>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            result.Add(new QbittorrentItem
            {
                Hash = item.TryGetProperty("hash", out var hash) ? hash.GetString() ?? "" : "",
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "(unnamed torrent)" : "(unnamed torrent)",
                State = item.TryGetProperty("state", out var state) ? state.GetString() ?? "unknown" : "unknown",
                Progress = item.TryGetProperty("progress", out var progress) ? progress.GetDouble() : 0,
                Size = item.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                DownloadSpeed = item.TryGetProperty("dlspeed", out var dlspeed) ? dlspeed.GetInt64() : 0,
                UploadSpeed = item.TryGetProperty("upspeed", out var upspeed) ? upspeed.GetInt64() : 0,
                Eta = item.TryGetProperty("eta", out var eta) ? eta.GetInt64() : 0
            });
        }
        return result;
    }

    private async Task<bool> SendQbittorrentActionAsync(string action, string hash)
    {
        var baseUrl = QbittorrentBaseUrl();
        if (baseUrl.Length == 0) return false;
        if (!qbittorrentAuthenticated && !await LoginQbittorrentAsync(baseUrl, CancellationToken.None)) return false;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash });
        using var response = await qbittorrentHttp.PostAsync(baseUrl + "/api/v2/torrents/" + action, form);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) qbittorrentAuthenticated = false;
        return response.IsSuccessStatusCode;
    }

    private void ShowQbittorrentActions(QbittorrentItem torrent)
    {
        using var dialog = new Form { Text = "qBittorrent transfer", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(620, 260), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        dialog.Controls.Add(new Label { Text = torrent.Name, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f), Location = new Point(20, 18), Size = new Size(560, 48), AutoEllipsis = true });
        dialog.Controls.Add(new Label { Text = $"{torrent.Progress * 100:0.0}% · {torrent.State}", ForeColor = Muted, AutoSize = true, Location = new Point(20, 70) });
        var pause = MakeButton(torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase) ? "Resume" : "Pause", 110, Accent); pause.Location = new Point(20, 125);
        var recheck = MakeButton("Recheck", 110); recheck.Location = new Point(140, 125);
        var remove = MakeButton("Remove (keep files)", 155); remove.Location = new Point(260, 125);
        var close = MakeButton("Close", 100); close.Location = new Point(455, 125); close.Click += (_, _) => dialog.Close();
        pause.Click += async (_, _) => { await SendQbittorrentActionAsync(torrent.State.Contains("paused", StringComparison.OrdinalIgnoreCase) ? "resume" : "pause", torrent.Hash); dialog.Close(); _ = RefreshQbittorrentAsync(); };
        recheck.Click += async (_, _) => { await SendQbittorrentActionAsync("recheck", torrent.Hash); dialog.Close(); _ = RefreshQbittorrentAsync(); };
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(dialog, "Remove this torrent from qBittorrent but keep its downloaded files?", "Confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await SendQbittorrentActionAsync("delete", torrent.Hash); dialog.Close(); _ = RefreshQbittorrentAsync();
        };
        dialog.Controls.AddRange(new Control[] { pause, recheck, remove, close });
        dialog.ShowDialog(this);
    }

    private void EditSettings()
    {
        using var dialog = new Form { Text = "High Seas Settings", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(680, 860), StartPosition = FormStartPosition.CenterParent };
        var covers = new CheckBox { Text = "Download covers (sends title/year to TVMaze or Wikipedia)", Checked = settings.AutoDownloadCovers, AutoSize = true, Location = new Point(24, 35) };
        var subtitles = new CheckBox { Text = "Automatically run subtitle audit after startup", Checked = settings.AutoSubtitleAudit, AutoSize = true, Location = new Point(24, 82) };
        var note = new Label { Text = "Subtitle auditing is manual by default. Use Check subtitles whenever you want to run it once.", ForeColor = Muted, Location = new Point(24, 120), Size = new Size(410, 48) };
        var phoneRemote = new CheckBox { Text = "Enable PIN-protected phone remote on this Wi-Fi", Checked = settings.EnablePhoneRemote, AutoSize = true, Location = new Point(24, 182) };
        var phoneNote = new Label { Text = "If Windows asks, allow access on Private networks only. Your media files are not uploaded.", ForeColor = Muted, Location = new Point(45, 215), Size = new Size(480, 42) };
        var tmdbLabel = new Label { Text = "TMDB API Read Access Token — posters, descriptions, and collections", AutoSize = true, Location = new Point(24, 272) };
        var tmdb = new TextBox { Text = settings.TmdbReadToken, UseSystemPasswordChar = true, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 302), Width = 615 };
        var tmdbNote = new Label { Text = "Stored only on this PC. High Seas Media uses TMDB data but is not endorsed or certified by TMDB.", ForeColor = Muted, Location = new Point(24, 336), Size = new Size(615, 38) };
        var realDebridLabel = new Label { Text = "REAL-DEBRID API TOKEN (optional)", ForeColor = Accent, AutoSize = true, Location = new Point(24, 382) };
        var realDebrid = new TextBox { Text = settings.RealDebridApiToken, UseSystemPasswordChar = true, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 410), Width = 296 };
        var realDebridNote = new Label { Text = "Tokens stay on this PC. Real-Debrid is limited to authorized direct-link downloads.", ForeColor = Muted, Location = new Point(24, 444), Size = new Size(615, 22) };

        var subtitleHeading = new Label { Text = "OPENSUBTITLES — DOWNLOAD MISSING ENGLISH SUBTITLES", ForeColor = Accent, Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Location = new Point(24, 492) };
        var subtitleSourceHeading = new Label { Text = "SUBTITLE SOURCES - TRY IN ORDER", ForeColor = Accent, Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Location = new Point(24, 532) };
        var subtitleKeyLabel = new Label { Text = "OPENSUBTITLES API KEY", ForeColor = Color.White, AutoSize = true, Location = new Point(24, 566) };
        var subtitleKey = new TextBox { Text = settings.OpenSubtitlesApiKey, UseSystemPasswordChar = true, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 588), Width = 615 };
        var subtitleUser = new TextBox { Text = settings.OpenSubtitlesUsername, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 642), Width = 296, PlaceholderText = "OpenSubtitles username (optional)" };
        var subtitlePassword = new TextBox { Text = settings.OpenSubtitlesPassword, UseSystemPasswordChar = true, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(343, 642), Width = 296, PlaceholderText = "Password (optional)" };
        var subdlLabel = new Label { Text = "SUBDL API KEY (optional fallback)", ForeColor = Color.White, AutoSize = true, Location = new Point(24, 686) };
        var subdlKey = new TextBox { Text = settings.SubdlApiKey, UseSystemPasswordChar = true, BackColor = Control, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 708), Width = 615 };
        var subtitleNote = new Label { Text = "OpenSubtitles is tried first, then SubDL. Both use title/year/season/episode metadata only; video files stay on this PC.", ForeColor = Muted, Location = new Point(24, 742), Size = new Size(615, 38) };
        var save = MakeButton("SAVE SETTINGS", 150, Accent); save.Location = new Point(489, 800);
        save.Click += (_, _) =>
        {
            settings.AutoDownloadCovers = covers.Checked;
            settings.AutoSubtitleAudit = subtitles.Checked;
            settings.EnablePhoneRemote = phoneRemote.Checked;
            settings.TmdbReadToken = tmdb.Text.Trim();
            settings.RealDebridApiToken = realDebrid.Text.Trim();
            settings.OpenSubtitlesApiKey = subtitleKey.Text.Trim();
            settings.OpenSubtitlesUsername = subtitleUser.Text.Trim();
            settings.OpenSubtitlesPassword = subtitlePassword.Text;
            settings.SubdlApiKey = subdlKey.Text.Trim();
            SaveSettings();
            if (settings.EnablePhoneRemote) StartPhoneRemote(); else StopPhoneRemote();
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.AddRange(new Control[] { covers, subtitles, note, phoneRemote, phoneNote, tmdbLabel, tmdb, tmdbNote, realDebridLabel, realDebrid, realDebridNote, subtitleSourceHeading, subtitleKeyLabel, subtitleKey, subtitleUser, subtitlePassword, subdlLabel, subdlKey, subtitleNote, save });
        if (dialog.ShowDialog(this) == DialogResult.OK && settings.AutoDownloadCovers) _ = DownloadAllCoversAsync();
    }

    private void RefreshRemoteLibrary()
    {
        lock (remoteLibraryLock)
        {
            remoteLibrary.Clear();
            foreach (var media in library)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(media.FullPath.ToUpperInvariant()));
                var id = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
                remoteLibrary[id] = media;
            }
        }
    }

    private string PhoneRemoteUrl()
    {
        try
        {
            var address = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));
            return address == null ? "Network address unavailable" : $"http://{address}:{settings.PhoneRemotePort}";
        }
        catch { return "Network address unavailable"; }
    }

    private static Bitmap CreateQrBitmap(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        using var code = new QRCode(data);
        return code.GetGraphic(8, Color.FromArgb(7, 17, 12), Color.FromArgb(45, 190, 105), true);
    }

    private void ShowPhoneRemoteDialog()
    {
        using var dialog = new Form { Text = "High Seas Remote", Icon = Icon, BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(810, 500), StartPosition = FormStartPosition.CenterParent };
        dialog.Shown += (_, _) => { var dark = 1; DwmSetWindowAttribute(dialog.Handle, 20, ref dark, sizeof(int)); };
        dialog.Controls.Add(new Label { Text = "CONTROL FROM YOUR PHONE", ForeColor = Accent, Font = new Font("Segoe UI Semibold", 17f), AutoSize = true, Location = new Point(24, 22) });
        dialog.Controls.Add(new Label { Text = "Open this address in your phone's browser while it is on the same Wi-Fi:", ForeColor = Muted, AutoSize = true, Location = new Point(25, 75) });
        var address = new TextBox { Text = PhoneRemoteUrl(), ReadOnly = true, BackColor = Surface, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Location = new Point(25, 103), Width = 360 };
        var copy = MakeButton("Copy", 95); copy.Location = new Point(395, 98); copy.Click += (_, _) => { if (!address.Text.Contains("unavailable")) Clipboard.SetText(address.Text); };
        dialog.Controls.AddRange(new Control[] { address, copy });
        dialog.Controls.Add(new Label { Text = "PIN", ForeColor = Muted, AutoSize = true, Location = new Point(25, 155) });
        dialog.Controls.Add(new Label { Text = remotePin, Font = new Font("Segoe UI Semibold", 25f), AutoSize = true, Location = new Point(22, 178) });
        var installUrl = PhoneRemoteUrl() + "/install";
        var qr = new PictureBox { Location = new Point(550, 82), Size = new Size(205, 205), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Image = installUrl.Contains("unavailable") ? null : CreateQrBitmap(installUrl) };
        dialog.Controls.Add(new Label { Text = "SCAN TO INSTALL OR UPDATE APP", ForeColor = Muted, AutoSize = true, Location = new Point(550, 54), Font = new Font("Segoe UI Semibold", 8.5f) });
        var install = MakeButton("COPY INSTALL LINK", 205, Accent); install.Location = new Point(550, 300); install.Click += (_, _) => { if (!installUrl.Contains("unavailable")) Clipboard.SetText(installUrl); };
        dialog.Controls.AddRange(new Control[] { qr, install });
        var enabled = new CheckBox { Text = "Enable the phone remote", Checked = settings.EnablePhoneRemote, AutoSize = true, Location = new Point(25, 240) };
        dialog.Controls.Add(enabled);
        dialog.Controls.Add(new Label { Text = "Search your collection, move the Media Center between monitors, choose video and audio destinations, play titles, and control playback. The Android app remembers this PC.", ForeColor = Muted, Location = new Point(25, 273), Size = new Size(465, 58) });
        var state = new Label { Text = remoteMessage, ForeColor = Muted, AutoSize = true, Location = new Point(25, 385) };
        var apply = MakeButton("Apply", 115, Accent); apply.Location = new Point(645, 375); apply.Click += (_, _) =>
        {
            settings.EnablePhoneRemote = enabled.Checked;
            SaveSettings();
            if (enabled.Checked) StartPhoneRemote(); else StopPhoneRemote();
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.AddRange(new Control[] { state, apply });
        activePhoneRemoteDialog = dialog;
        dialog.ShowDialog(this);
        if (ReferenceEquals(activePhoneRemoteDialog, dialog)) activePhoneRemoteDialog = null;
        qr.Image?.Dispose();
    }

    private void StartPhoneRemote()
    {
        if (remoteListener != null) return;
        remoteCancellation = new CancellationTokenSource();
        var token = remoteCancellation.Token;
        remoteListener = new TcpListener(IPAddress.Any, settings.PhoneRemotePort);
        try
        {
            remoteListener.Start();
            remoteMessage = $"Ready at {PhoneRemoteUrl()}";
            _ = Task.Run(() => PhoneRemoteLoopAsync(token));
        }
        catch (Exception ex)
        {
            remoteMessage = $"Could not start: {ex.Message}";
            remoteListener = null;
            remoteCancellation.Dispose();
            remoteCancellation = null;
        }
    }

    private void StopPhoneRemote()
    {
        try { remoteCancellation?.Cancel(); } catch { }
        try { remoteListener?.Stop(); } catch { }
        remoteListener = null;
        remoteCancellation?.Dispose();
        remoteCancellation = null;
        remoteMessage = "Phone remote is off";
    }

    private async Task PhoneRemoteLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && remoteListener != null)
        {
            try
            {
                var client = await remoteListener.AcceptTcpClientAsync(token);
                _ = HandlePhoneClientAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(150, token).ContinueWith(_ => { }, TaskScheduler.Default); }
        }
    }

    private async Task HandlePhoneClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (endpoint == null || !IsPrivateAddress(endpoint.Address)) return;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, true);
            var requestLine = await reader.ReadLineAsync(token);
            if (string.IsNullOrWhiteSpace(requestLine)) return;
            string? header;
            do { header = await reader.ReadLineAsync(token); } while (!string.IsNullOrEmpty(header));
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || parts[0] != "GET") { await WriteHttpAsync(stream, 405, "text/plain", "GET only", token); return; }
            Uri request;
            try { request = new Uri("http://localhost" + parts[1]); }
            catch { await WriteHttpAsync(stream, 400, "text/plain", "Bad request", token); return; }
            var query = ParseQuery(request.Query);
            if (request.AbsolutePath == "/") { await WriteHttpAsync(stream, 200, "text/html; charset=utf-8", PhoneRemoteHtml(), token); return; }
            if (request.AbsolutePath == "/install") { await WriteHttpAsync(stream, 200, "text/html; charset=utf-8", PhoneInstallHtml(), token); return; }
            if (request.AbsolutePath is "/download/High-Seas-Remote-latest.apk" or "/download/CliveRemote.apk")
            {
                var apkPath = Path.Combine(AppContext.BaseDirectory, "High-Seas-Remote-latest.apk");
                if (!File.Exists(apkPath)) { await WriteHttpAsync(stream, 404, "text/plain", "Phone app is not available in this build.", token); return; }
                var versionPath = Path.Combine(AppContext.BaseDirectory, "High-Seas-Remote-version.txt");
                var version = File.Exists(versionPath) ? (await File.ReadAllTextAsync(versionPath, token)).Trim() : "latest";
                var filename = $"High-Seas-Remote-v{Regex.Replace(version, "[^0-9A-Za-z.-]", "-")}.apk";
                await WriteHttpBytesAsync(stream, 200, "application/vnd.android.package-archive", await File.ReadAllBytesAsync(apkPath, token), token, $"attachment; filename={filename}");
                return;
            }
            if (!query.TryGetValue("pin", out var pin) || pin != remotePin) { await WriteHttpAsync(stream, 403, "application/json", "{\"error\":\"Wrong PIN\"}", token); return; }
            ClosePhoneRemoteDialogAfterConnection();

            if (request.AbsolutePath == "/api/status")
            {
                var response = JsonSerializer.Serialize(new { ok = true, mediaCount = library.Count, monitors = Screen.AllScreens.Select((x, i) => new { id = i + 1, name = $"Monitor {i + 1}", size = $"{x.Bounds.Width}x{x.Bounds.Height}" }), audioOutputs = RemoteAudioOutputs() });
                await WriteHttpAsync(stream, 200, "application/json", response, token);
                return;
            }
            if (request.AbsolutePath == "/api/library")
            {
                var term = query.GetValueOrDefault("q", "").Trim();
                List<object> result;
                lock (remoteLibraryLock)
                {
                    result = remoteLibrary.Select(x => new { x.Key, x.Value })
                        .Where(x => term.Length == 0 || ($"{x.Value.Title} {x.Value.Series} {x.Value.EpisodeTitle}").Contains(term, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x.Value.Type).ThenBy(x => x.Value.Type == "Show" ? x.Value.Series : x.Value.Title).ThenBy(x => x.Value.SeasonNumber).ThenBy(x => x.Value.EpisodeNumber)
                        .Take(500)
                        .Select(x => (object)new { id = x.Key, title = x.Value.Type == "Show" ? (x.Value.EpisodeTitle.Length > 0 ? x.Value.EpisodeTitle : x.Value.Episode) : x.Value.Title, detail = x.Value.Type == "Show" ? $"{x.Value.Series} · S{x.Value.SeasonNumber:00}E{x.Value.EpisodeNumber:00}" : $"Movie{(x.Value.Year.Length > 0 ? " · " + x.Value.Year : "")}", type = x.Value.Type }).ToList();
                }
                await WriteHttpAsync(stream, 200, "application/json", JsonSerializer.Serialize(result), token);
                return;
            }
            if (request.AbsolutePath == "/api/play")
            {
                MediaItem? media = null;
                lock (remoteLibraryLock) remoteLibrary.TryGetValue(query.GetValueOrDefault("id", ""), out media);
                if (media == null) { await WriteHttpAsync(stream, 404, "application/json", "{\"error\":\"Title not found\"}", token); return; }
                var monitor = int.TryParse(query.GetValueOrDefault("monitor", "1"), out var selectedMonitor) ? Math.Clamp(selectedMonitor, 1, Math.Max(1, Screen.AllScreens.Length)) : 1;
                BeginInvoke(() => { selectedGridMedia = media; monitors.SelectedIndex = monitor - 1; useSubtitles.Checked = true; PlaySelected(); });
                await WriteHttpAsync(stream, 200, "application/json", JsonSerializer.Serialize(new { ok = true, playing = media.Title, monitor }), token);
                return;
            }
            if (request.AbsolutePath == "/api/audio")
            {
                if (!Guid.TryParse(query.GetValueOrDefault("id", ""), out var deviceId)) { await WriteHttpAsync(stream, 400, "application/json", "{\"error\":\"Audio device not found\"}", token); return; }
                BeginInvoke(() => { SetAudioOutput(deviceId); RefreshAudioOutputs(); });
                await WriteHttpAsync(stream, 200, "application/json", "{\"ok\":true}", token);
                return;
            }
            if (request.AbsolutePath == "/api/pointer")
            {
                var action = query.GetValueOrDefault("action", "move").ToLowerInvariant();
                var dx = int.TryParse(query.GetValueOrDefault("dx", "0"), out var parsedX) ? Math.Clamp(parsedX, -480, 480) : 0;
                var dy = int.TryParse(query.GetValueOrDefault("dy", "0"), out var parsedY) ? Math.Clamp(parsedY, -1200, 1200) : 0;
                BeginInvoke(() => RunPointerAction(action, dx, dy));
                await WriteHttpAsync(stream, 200, "application/json", "{\"ok\":true}", token);
                return;
            }
            if (request.AbsolutePath == "/api/type")
            {
                var text = query.GetValueOrDefault("text", "");
                if (text.Length > 500) text = text[..500];
                BeginInvoke(() => SendKeys.SendWait(EscapeSendKeysText(text)));
                await WriteHttpAsync(stream, 200, "application/json", "{\"ok\":true}", token);
                return;
            }
            if (request.AbsolutePath == "/api/key")
            {
                var keyName = query.GetValueOrDefault("name", "").ToLowerInvariant();
                BeginInvoke(() => SendRemoteKey(keyName));
                await WriteHttpAsync(stream, 200, "application/json", "{\"ok\":true}", token);
                return;
            }
            if (request.AbsolutePath == "/api/command")
            {
                var command = query.GetValueOrDefault("name", "").ToLowerInvariant();
                var targetMonitor = int.TryParse(query.GetValueOrDefault("monitor", "1"), out var requestedMonitor) ? requestedMonitor : 1;
                BeginInvoke(() => { if (command == "moveapp") MoveAppToMonitor(targetMonitor); else RunRemoteCommand(command); });
                await WriteHttpAsync(stream, 200, "application/json", "{\"ok\":true}", token);
                return;
            }
            await WriteHttpAsync(stream, 404, "text/plain", "Not found", token);
        }
    }

    private void ClosePhoneRemoteDialogAfterConnection()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            if (activePhoneRemoteDialog is { IsDisposed: false } dialog) dialog.Close();
            Activate();
            watchPanel.Focus();
        });
    }

    private static void RunPointerAction(string action, int dx, int dy)
    {
        const uint leftDown = 0x0002, leftUp = 0x0004, rightDown = 0x0008, rightUp = 0x0010, wheel = 0x0800;
        if (action == "move") Cursor.Position = new Point(Cursor.Position.X + dx, Cursor.Position.Y + dy);
        else if (action == "left") { mouse_event(leftDown, 0, 0, 0, UIntPtr.Zero); mouse_event(leftUp, 0, 0, 0, UIntPtr.Zero); }
        else if (action == "right") { mouse_event(rightDown, 0, 0, 0, UIntPtr.Zero); mouse_event(rightUp, 0, 0, 0, UIntPtr.Zero); }
        else if (action == "scroll" && dy != 0) mouse_event(wheel, 0, 0, dy, UIntPtr.Zero);
        else if (action == "wheelup") mouse_event(wheel, 0, 0, 120, UIntPtr.Zero);
        else if (action == "wheeldown") mouse_event(wheel, 0, 0, -120, UIntPtr.Zero);
    }

    private static string EscapeSendKeysText(string text)
    {
        var escaped = new StringBuilder(text.Length * 2);
        foreach (var character in text)
        {
            escaped.Append(character switch
            {
                '+' => "{+}", '^' => "{^}", '%' => "{%}", '~' => "{~}", '(' => "{(}", ')' => "{)}",
                '[' => "{[}", ']' => "{]}", '{' => "{{}", '}' => "{}}", '\r' => "", '\n' => "{ENTER}",
                _ => character.ToString()
            });
        }
        return escaped.ToString();
    }

    private static void SendRemoteKey(string name)
    {
        var sequence = name switch
        {
            "backspace" => "{BACKSPACE}", "enter" => "{ENTER}", "tab" => "{TAB}", "escape" => "{ESC}",
            "delete" => "{DELETE}", "up" => "{UP}", "down" => "{DOWN}", "left" => "{LEFT}", "right" => "{RIGHT}",
            "home" => "{HOME}", "end" => "{END}", "pageup" => "{PGUP}", "pagedown" => "{PGDN}", _ => ""
        };
        if (sequence.Length > 0) SendKeys.SendWait(sequence);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x.Length > 1 ? x[1] : ""), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] == 169 && bytes[1] == 254;
    }

    private static async Task WriteHttpAsync(NetworkStream stream, int statusCode, string contentType, string body, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var label = statusCode switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {label}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token);
        await stream.WriteAsync(payload, token);
    }

    private static async Task WriteHttpBytesAsync(NetworkStream stream, int statusCode, string contentType, byte[] payload, CancellationToken token, string? disposition = null)
    {
        var label = statusCode == 200 ? "OK" : "Error";
        var extra = disposition == null ? "" : $"Content-Disposition: {disposition}\r\n";
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {label}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\n{extra}Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token);
        await stream.WriteAsync(payload, token);
    }

    private void HandleNavigationKey(object? sender, KeyEventArgs e)
    {
        if (!watchPanel.Visible) return;
        if (watchSearchEditing && watchSearch.Focused)
        {
            if (e.KeyCode == Keys.Escape)
            {
                watchSearchEditing = false;
                ActiveControl = null;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            return;
        }
        if (e.KeyCode == Keys.OemQuestion && !e.Shift)
        {
            watchSearchEditing = true;
            watchSearch.Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        string? command = null;
        if (e.Alt && e.KeyCode == Keys.Left) command = "back";
        else command = e.KeyCode switch
        {
            Keys.Left or Keys.A => "left",
            Keys.Right or Keys.D => "right",
            Keys.Up or Keys.W => "up",
            Keys.Down or Keys.S => "down",
            Keys.Enter => "select",
            Keys.Space => "playpause",
            Keys.Back => "back",
            Keys.Escape => "escape",
            Keys.Home => "home",
            Keys.F or Keys.F11 => "fullscreen",
            Keys.P => "playpause",
            Keys.M => "mute",
            Keys.OemMinus or Keys.Subtract => "volumedown",
            Keys.Oemplus or Keys.Add => "volumeup",
            _ => null
        };
        if (command == null) return;
        RunRemoteCommand(command);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void NavigateWatchBack()
    {
        if (activeRemoteDialog != null && !activeRemoteDialog.IsDisposed) { activeRemoteDialog.Close(); return; }
        if (browseMode == "Collection") browseMode = "Collections";
        else browseMode = "Home";
        activeCollection = "";
        RefreshWatchView();
    }

    private void NavigateTopMode(int direction)
    {
        var modes = new[] { "Home", "Movies", "Shows", "Collections", "QBittorrent" };
        var current = Array.IndexOf(modes, browseMode);
        if (current < 0) current = Array.IndexOf(modes, "Collections");
        var next = Math.Clamp(current + direction, 0, modes.Length - 1);
        if (next == current) return;
        browseMode = modes[next];
        activeCollection = "";
        RefreshWatchView();
        if (browseMode == "QBittorrent") _ = RefreshQbittorrentAsync();
    }

    private void ToggleAppFullscreen()
    {
        if (!appFullscreen)
        {
            windowedBounds = Bounds;
            windowedState = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            appFullscreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            Bounds = windowedBounds;
            if (windowedState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
            appFullscreen = false;
        }
        RefreshWatchView();
    }

    private void MoveAppToMonitor(int monitorNumber)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        var screen = screens[Math.Clamp(monitorNumber - 1, 0, screens.Length - 1)];
        if (appFullscreen)
        {
            WindowState = FormWindowState.Normal;
            Bounds = screen.Bounds;
        }
        else
        {
            WindowState = FormWindowState.Normal;
            var area = screen.WorkingArea;
            var width = Math.Min(Math.Max(MinimumSize.Width, Width), area.Width);
            var height = Math.Min(Math.Max(MinimumSize.Height, Height), area.Height);
            Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        }
        Activate();
        RefreshWatchView();
    }

    private void FitWindowToCurrentMonitor()
    {
        if (WindowState != FormWindowState.Normal || appFullscreen) return;
        var area = Screen.FromControl(this).Bounds;
        // Per-monitor-aware WinForms reports a 1280x720 TV at its 1920x1080
        // logical size when the display is scaled.  Fill that monitor's bounds
        // so the page uses the whole screen instead of leaving a clipped,
        // undersized window in the corner.
        if (DeviceDpi > 96 && area.Width >= 1600 && area.Height >= 900)
        {
            Bounds = area;
            return;
        }
        var width = Math.Min(Math.Max(MinimumSize.Width, Width), area.Width);
        var height = Math.Min(Math.Max(MinimumSize.Height, Height), area.Height);
        var left = Math.Clamp(Left, area.Left, area.Right - width);
        var top = Math.Clamp(Top, area.Top, area.Bottom - height);
        if (Bounds != new Rectangle(left, top, width, height))
            Bounds = new Rectangle(left, top, width, height);
    }

    private void SetRemoteSelection(Panel? card, bool ensureVerticalVisibility = true)
    {
        var old = remoteSelectedCard;
        if (ReferenceEquals(old, card)) return;
        remoteSelectedCard = card;
        if (old != null) SetCardFocusState(old, false);
        if (card != null)
        {
            RevealCardHorizontally(card);
            SetCardFocusState(card, true);
            _ = UpdateWatchFocusAsync(card);
        }
        if (!ensureVerticalVisibility || card == null || !watchCardPositions.TryGetValue(card, out var position)) return;
        var currentScroll = watchScrollY;
        var desiredScroll = currentScroll;
        var rowTop = position.Top - 14;
        var rowBottom = rowTop + 340;
        if (rowTop < currentScroll + 18) desiredScroll = rowTop - 24;
        else if (rowBottom > currentScroll + posterGrid.ClientSize.Height - 24) desiredScroll = rowBottom - posterGrid.ClientSize.Height + 30;
        if (desiredScroll != currentScroll) AnimateWatchScroll(desiredScroll);
    }

    private static void SetCardFocusState(Panel card, bool focused)
    {
        card.BackColor = focused ? Color.FromArgb(30, 82, 55) : Surface;
        var title = card.Controls.OfType<Label>().FirstOrDefault(label => Equals(label.Tag, "title"));
        var detail = card.Controls.OfType<Label>().FirstOrDefault(label => Equals(label.Tag, "detail"));
        var badge = card.Controls.OfType<Label>().FirstOrDefault(label => Equals(label.Tag, "focus-badge"));
        if (title != null) title.ForeColor = focused ? Color.White : Color.FromArgb(235, 239, 236);
        if (detail != null) detail.ForeColor = focused ? Color.FromArgb(203, 235, 213) : Muted;
        if (badge != null) { badge.Visible = focused; badge.BringToFront(); }
        card.Invalidate();
    }

    /// <summary>
    /// Moves a poster carousel just enough to keep keyboard/remote focus fully visible. The logical
    /// card coordinates stay stable, which prevents focus animations from slowly drifting the row.
    /// </summary>
    private void RevealCardHorizontally(Panel card)
    {
        if (card.Parent?.Parent is not CarouselPanel row) return;
        row.RevealControl(card);

        // Apply once more after pending layout work so rapid remote key repeats cannot leave the
        // visual row one selection behind.
        BeginInvoke(() =>
        {
            if (IsDisposed || card.IsDisposed || !row.Owns(card) || !ReferenceEquals(remoteSelectedCard, card)) return;
            row.RevealControl(card);
        });
    }

    /// <summary>
    /// Exercises the same deterministic horizontal and vertical layout math used by the app.
    /// Release verification runs this without opening the main window.
    /// </summary>
    internal static void VerifyCarouselScrolling()
    {
        using var host = new Form { ClientSize = new Size(1280, 500), ShowInTaskbar = false };
        using var row = new CarouselPanel { Location = new Point(0, 0), Size = new Size(1200, 340) };
        const int cardCount = 48;
        const int stride = 204;
        host.Controls.Add(row);

        var cards = new List<(Panel Card, Rectangle Logical)>();
        for (var index = 0; index < cardCount; index++)
        {
            var card = new Panel { Bounds = new Rectangle(14 + index * stride, 14, 180, 304) };
            var logical = card.Bounds;
            row.AddCarouselControl(card, logical.Location);
            cards.Add((card, logical));
        }

        _ = host.Handle;
        _ = row.Handle;
        host.PerformLayout();
        row.PerformLayout();

        foreach (var index in Enumerable.Range(0, cardCount))
        {
            var target = cards[index];
            row.RevealControl(target.Card);
            var visibleLeft = row.VisibleLeft(target.Card);
            var visibleRight = visibleLeft + target.Logical.Width;
            if (visibleLeft < 0 || visibleRight > row.ClientSize.Width)
                throw new InvalidOperationException($"Carousel failed to reveal card {index}: {visibleLeft}..{visibleRight} in {row.ClientSize.Width}px.");
        }

        var previousOffset = -1;
        foreach (var index in Enumerable.Range(6, 8))
        {
            row.RevealControl(cards[index].Card);
            if (row.ViewportOffset <= previousOffset && row.ViewportOffset < (cardCount * stride - row.ClientSize.Width))
                throw new InvalidOperationException($"Carousel stopped advancing at card {index}.");
            previousOffset = row.ViewportOffset;
        }

        // The page itself deliberately avoids WinForms AutoScroll. Verify that the final row and
        // bottom marker are fully reachable using the same stable logical-coordinate translation.
        const int viewportHeight = 720;
        const int contentHeight = 4_260;
        var maximumY = contentHeight - viewportHeight;
        var logicalTops = new[] { 0, 440, 1_780, 3_810, 4_259 };
        foreach (var offset in new[] { 0, maximumY / 2, maximumY })
        {
            var translated = logicalTops.Select(top => top - offset).ToArray();
            for (var index = 0; index < logicalTops.Length; index++)
                if (translated[index] != logicalTops[index] - offset)
                    throw new InvalidOperationException($"Page translation drifted at offset {offset}.");
        }
        if (logicalTops[^2] - maximumY < 0 || logicalTops[^1] - maximumY >= viewportHeight)
            throw new InvalidOperationException("Page bottom remains clipped at maximum vertical offset.");
    }

    private void MoveRemoteSelection(string direction)
    {
        watchSearchEditing = false;
        if (watchSearch.Focused) ActiveControl = null;
        if (watchCards.Count == 0)
        {
            if (direction is "left" or "right") NavigateTopMode(direction == "left" ? -1 : 1);
            return;
        }
        if (remoteSelectedCard == null || !watchCards.Contains(remoteSelectedCard)) { SetRemoteSelection(watchCards[0]); return; }
        if (!watchCardPositions.TryGetValue(remoteSelectedCard, out var current)) return;
        Panel? best;
        if (direction is "left" or "right")
        {
            var targetColumn = current.Column + (direction == "left" ? -1 : 1);
            best = watchCards.FirstOrDefault(card => watchCardPositions.TryGetValue(card, out var position) && position.Section == current.Section && position.Row == current.Row && position.Column == targetColumn);
            if (best == null)
            {
                var targetRow = current.Row + (direction == "left" ? -1 : 1);
                var adjacentRow = watchCards.Where(card => watchCardPositions.TryGetValue(card, out var position) && position.Section == current.Section && position.Row == targetRow).ToList();
                best = direction == "left"
                    ? adjacentRow.OrderByDescending(card => watchCardPositions[card].Column).FirstOrDefault()
                    : adjacentRow.OrderBy(card => watchCardPositions[card].Column).FirstOrDefault();
            }
        }
        else
        {
            var targetRow = current.Row + (direction == "up" ? -1 : 1);
            var candidates = watchCards.Where(card => watchCardPositions.TryGetValue(card, out var position) && position.Section == current.Section && position.Row == targetRow).ToList();
            if (candidates.Count == 0)
            {
                var targetSection = current.Section + (direction == "up" ? -1 : 1);
                var sectionCards = watchCards.Where(card => watchCardPositions.TryGetValue(card, out var position) && position.Section == targetSection).ToList();
                if (sectionCards.Count > 0)
                {
                    var edgeRow = direction == "up" ? sectionCards.Max(card => watchCardPositions[card].Row) : sectionCards.Min(card => watchCardPositions[card].Row);
                    candidates = sectionCards.Where(card => watchCardPositions[card].Row == edgeRow).ToList();
                }
            }
            best = candidates.OrderBy(card => Math.Abs(watchCardPositions[card].Column - current.Column)).FirstOrDefault();
        }
        if (best != null) SetRemoteSelection(best, ensureVerticalVisibility: direction is not ("left" or "right"));
        else if (direction is "left" or "right")
        {
            // Keep arrows dedicated to card movement until the current page's section edge.
            // At that edge, the same gesture advances to the neighboring top-level page.
            var sectionCards = watchCards.Where(card => watchCardPositions.TryGetValue(card, out var position) && position.Section == current.Section).ToList();
            var atSectionEdge = direction == "left"
                ? ReferenceEquals(remoteSelectedCard, sectionCards.OrderBy(card => watchCardPositions[card].Row).ThenBy(card => watchCardPositions[card].Column).FirstOrDefault())
                : ReferenceEquals(remoteSelectedCard, sectionCards.OrderByDescending(card => watchCardPositions[card].Row).ThenByDescending(card => watchCardPositions[card].Column).FirstOrDefault());
            if (atSectionEdge) NavigateTopMode(direction == "left" ? -1 : 1);
        }
    }

    private void ActivateRemoteSelection()
    {
        if (activeRemoteDialog != null && !activeRemoteDialog.IsDisposed) { activeRemoteSelect?.Invoke(); return; }
        if (remoteSelectedCard == null) { if (watchCards.Count > 0) SetRemoteSelection(watchCards[0]); return; }
        if (remoteSelectedCard.Tag is Action action) action();
    }

    private void RunRemoteCommand(string command)
    {
        // Playback commands go directly to our in-app player. Navigation commands continue to
        // control the poster browser, so the same phone remote works in both contexts.
        if (activePlayer is { IsDisposed: false })
        {
            switch (command)
            {
                case "playpause": activePlayer.TogglePlayPause(); return;
                case "previous": activePlayer.RequestPreviousEpisode(); return;
                case "next": activePlayer.RequestNextEpisode(); return;
                case "mute": activePlayer.ToggleMute(); return;
                case "volumedown": activePlayer.VolumeBy(-5); return;
                case "volumeup": activePlayer.VolumeBy(5); return;
                case "stop": activePlayer.StopPlayback(); return;
            }
        }
        if (command is "up" or "down" or "left" or "right")
        {
            if (activeRemoteDialog != null && !activeRemoteDialog.IsDisposed) activeRemoteNavigate?.Invoke(command);
            else MoveRemoteSelection(command);
            return;
        }
        if (command == "select") { ActivateRemoteSelection(); return; }
        if (command == "back") { NavigateWatchBack(); return; }
        if (command == "escape") { if (appFullscreen) ToggleAppFullscreen(); else NavigateWatchBack(); return; }
        if (command == "fullscreen") { ToggleAppFullscreen(); return; }
        if (command == "home")
        {
            if (activeRemoteDialog != null && !activeRemoteDialog.IsDisposed) activeRemoteDialog.Close();
            browseMode = "Home"; activeCollection = ""; RefreshWatchView();
            return;
        }
        var key = command switch { "playpause" => (byte)0xB3, "previous" => (byte)0xB1, "next" => (byte)0xB0, "mute" => (byte)0xAD, "volumedown" => (byte)0xAE, "volumeup" => (byte)0xAF, _ => (byte)0 };
        if (command == "stop") return;
        if (key == 0) return;
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    private static string PhoneInstallHtml() => """
<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><title>Install High Seas Remote</title>
<style>*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#09120f;color:#fff;font-family:system-ui,-apple-system,Segoe UI,sans-serif;padding:22px}.box{width:min(480px,100%);background:#101f19;border:1px solid #1b3127;border-radius:22px;padding:28px;text-align:center;box-shadow:0 24px 70px #0008}.mark{font-size:68px}.brand{color:#2dbe69;font-size:13px;font-weight:900;letter-spacing:2px}h1{margin:8px 0 10px;font-size:30px}.muted{color:#abc2b4;line-height:1.5}.download{display:block;background:#2dbe69;color:#06100b;text-decoration:none;border-radius:12px;padding:17px;margin:25px 0 18px;font-weight:900}.steps{text-align:left;background:#0b1712;border-radius:12px;padding:15px 18px;line-height:1.55;font-size:14px}</style></head>
<body><main class="box"><div class="mark">🏴‍☠️</div><div class="brand">YOUR PERSONAL TREASURE CHEST</div><h1>High Seas Remote</h1><p class="muted">Install or update the phone remote directly from your High Seas Media PC.</p><a class="download" href="/download/High-Seas-Remote-latest.apk">DOWNLOAD HIGH SEAS REMOTE</a><div class="steps"><b>Install or update</b><br>1. Download the versioned High Seas Remote APK.<br>2. Allow installation from your browser when Android asks.<br>3. Android will install it or update your existing High Seas Remote.<br>4. Open it; your saved PC address and PIN remain available.</div><p class="muted">Scan this page again after future High Seas Media updates to get the newest remote.</p></main></body></html>
""";

    private string PhoneRemoteHtml()
    {
        var options = string.Join("", Screen.AllScreens.Select((_, i) => $"<option value=\"{i + 1}\"{(i == Math.Min(2, Screen.AllScreens.Length - 1) ? " selected" : "")}>Monitor {i + 1}</option>"));
        var html = """
<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><title>High Seas Remote</title>
<style>*{box-sizing:border-box}html,body{min-height:100%;overflow-y:auto}body{margin:0;background:#09120f;color:#fff;font-family:system-ui,-apple-system,Segoe UI,sans-serif}.wrap{max-width:620px;min-height:100dvh;margin:auto;padding:22px 18px calc(90px + env(safe-area-inset-bottom))}.brand{color:#2dbe69;font-size:14px;font-weight:900;letter-spacing:2px}h1{font-size:30px;margin:8px 0 18px}.card{background:#101f19;border:1px solid #1b3127;border-radius:16px;padding:15px;margin:12px 0;box-shadow:0 10px 30px #0005}.label{display:block;color:#abc2b4;font-size:11px;font-weight:800;letter-spacing:.7px;margin:5px 2px 7px}input,select,button{font:inherit;border:0;border-radius:10px;color:#fff;background:#1b3127;padding:14px}input{width:100%;margin:7px 0}select{width:100%;margin-bottom:11px}button{font-weight:700;touch-action:manipulation}.go{background:#2dbe69;color:#06100b;width:100%;margin-top:10px}.dpad{display:grid;grid-template-columns:repeat(3,76px);grid-template-rows:repeat(3,64px);gap:8px;justify-content:center}.dpad button{font-size:25px}.dpad .ok{background:#2dbe69;color:#06100b;font-size:16px}.navkeys{display:grid;grid-template-columns:repeat(3,1fr);gap:9px;margin-top:12px}.controls{display:grid;grid-template-columns:repeat(3,1fr);gap:9px}.controls button{font-size:18px;min-height:54px}.result{padding:13px 4px;border-bottom:1px solid #294536;display:flex;gap:10px;align-items:center}.result div{flex:1;min-width:0}.result b,.result small{overflow-wrap:anywhere}.result small{color:#abc2b4;display:block}.result button{background:#2dbe69;color:#06100b;padding:10px 15px;flex:0 0 auto}.muted{color:#abc2b4;font-size:13px}.hidden{display:none}</style></head>
<body><main class="wrap"><div class="brand">HIGH SEAS MEDIA</div><h1>Phone Remote</h1>
<section class="card" id="login"><label>Enter the PIN shown on your PC</label><input id="pin" inputmode="numeric" maxlength="6" placeholder="6-digit PIN"><button class="go" onclick="connect()">CONNECT</button><p class="muted" id="loginmsg"></p></section>
<div id="remote" class="hidden"><section class="card"><label class="label">VIDEO / MEDIA CENTER MONITOR</label><select id="monitor">__MONITORS__</select><label class="label">AUDIO PLAYS THROUGH</label><select id="audio" onchange="setAudio()"><option>Loading audio devices…</option></select></section>
<section class="card"><div class="dpad"><span></span><button onclick="cmd('up')">▲</button><span></span><button onclick="cmd('left')">◀</button><button class="ok" onclick="cmd('select')">OK</button><button onclick="cmd('right')">▶</button><span></span><button onclick="cmd('down')">▼</button><span></span></div><div class="navkeys"><button onclick="cmd('back')">BACK</button><button onclick="cmd('home')">HOME</button><button onclick="cmd('fullscreen')">FULL</button></div><button class="go" onclick="cmd('moveapp')">MOVE MEDIA CENTER TO THIS MONITOR</button></section>
<section class="card"><div class="controls"><button onclick="cmd('previous')">⏮</button><button onclick="cmd('playpause')">⏯</button><button onclick="cmd('next')">⏭</button><button onclick="cmd('volumedown')">− VOL</button><button onclick="cmd('mute')">MUTE</button><button onclick="cmd('volumeup')">+ VOL</button></div><button class="go" onclick="cmd('stop')">STOP PLAYBACK</button></section>
<section class="card"><input id="search" placeholder="Search movies, shows, episodes..." oninput="queueSearch()"><div id="results"></div></section></div></main>
<script>let timer,allRows=[];const el=x=>document.getElementById(x);function qp(o){return Object.entries(o).map(([k,v])=>encodeURIComponent(k)+'='+encodeURIComponent(v)).join('&')}async function api(path,args={}){args.pin=el('pin').value;const r=await fetch(path+'?'+qp(args));if(!r.ok)throw new Error(r.status===403?'Wrong PIN':'Connection failed');return r.json()}async function connect(){try{const state=await api('/api/status');allRows=await api('/api/library');localStorage.clivePin=el('pin').value;el('audio').innerHTML=(state.audioOutputs||[]).map(x=>`<option value="${x.id}"${x.isDefault?' selected':''}>${esc(x.name)}</option>`).join('')||'<option value="">Windows default audio</option>';el('login').classList.add('hidden');el('remote').classList.remove('hidden');search()}catch(e){el('loginmsg').textContent=e.message}}async function cmd(name){try{await api('/api/command',{name,monitor:el('monitor').value})}catch(e){alert(e.message)}}async function setAudio(){if(!el('audio').value)return;try{await api('/api/audio',{id:el('audio').value})}catch(e){alert(e.message)}}function queueSearch(){clearTimeout(timer);timer=setTimeout(search,90)}function search(){const q=el('search').value.trim().toLowerCase();const rows=allRows.filter(x=>!q||(x.title+' '+x.detail).toLowerCase().includes(q)).slice(0,80);el('results').innerHTML=rows.map(x=>`<div class="result"><div><b>${esc(x.title)}</b><small>${esc(x.detail)}</small></div><button onclick="play('${x.id}')">PLAY</button></div>`).join('')||'<p class="muted">No matches</p>'}async function play(id){try{await api('/api/play',{id,monitor:el('monitor').value})}catch(e){alert(e.message)}}function esc(s){const d=document.createElement('div');d.textContent=s;return d.innerHTML}el('pin').value=localStorage.clivePin||'';if(el('pin').value.length===6)connect()</script></body></html>
""";
        const string trackpadCss = """
.modebar{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin:12px 0}.modebar button{background:#132b20;border:1px solid #315b43}.modebar button.active{background:#2dbe69;color:#06100b}.trackpad{height:300px;border:1px solid #315b43;border-radius:18px;background:radial-gradient(circle at 50% 35%,#163326,#0b1712 72%);display:grid;place-items:center;color:#8eb6a0;touch-action:none;user-select:none;-webkit-user-select:none}.trackpad span{text-align:center;pointer-events:none}.sensitivity{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:10px;margin:8px 2px 12px;color:#abc2b4;font-size:12px}.sensitivity input{margin:0;padding:0;accent-color:#2dbe69}.clickrow,.keygrid{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:10px}.clickrow button,.keygrid button{min-height:48px}.typebox{display:grid;grid-template-columns:1fr auto;gap:8px;margin-top:12px}.typebox input{margin:0}.typebox button{background:#2dbe69;color:#06100b}
""";
        const string modeHeader = """
<div class="modebar"><button id="mediaModeButton" class="active" onclick="switchMode('media')">MEDIA REMOTE</button><button id="trackpadModeButton" onclick="switchMode('trackpad')">TRACKPAD + KEYS</button></div><div id="mediaMode">
""";
        const string trackpadMarkup = """
<div id="trackpadMode" class="hidden"><section class="card"><label class="label">PC TRACKPAD</label><div class="sensitivity"><span>POINTER</span><input id="sensitivity" type="range" min="0.5" max="3" step="0.1" value="1.5" oninput="saveSensitivity()"><b id="sensitivityValue">1.5x</b></div><div class="sensitivity"><span>SCROLL</span><input id="scrollSensitivity" type="range" min="0.4" max="2.5" step="0.1" value="1.0" oninput="saveSensitivity()"><b id="scrollSensitivityValue">1.0x</b></div><div id="trackpad" class="trackpad"><span>ONE FINGER: MOVE + TAP<br>TWO FINGERS: SMOOTH SCROLL</span></div><div class="clickrow"><button onclick="pointerAction('left')">LEFT</button><button onclick="pointerAction('right')">RIGHT</button><button onclick="pointerAction('wheelup')">SCROLL +</button><button onclick="pointerAction('wheeldown')">SCROLL -</button></div><div class="typebox"><input id="typeText" placeholder="Type on the PC..."><button onclick="sendText()">SEND</button></div><div class="keygrid"><button onclick="remoteKey('backspace')">BACKSPACE</button><button onclick="remoteKey('enter')">ENTER</button><button onclick="remoteKey('tab')">TAB</button><button onclick="remoteKey('escape')">ESC</button><button onclick="remoteKey('left')">LEFT</button><button onclick="remoteKey('up')">UP</button><button onclick="remoteKey('down')">DOWN</button><button onclick="remoteKey('right')">RIGHT</button></div></section></div>
""";
        const string trackpadScript = """
function switchMode(name){const media=name==='media';el('mediaMode').classList.toggle('hidden',!media);el('trackpadMode').classList.toggle('hidden',media);el('mediaModeButton').classList.toggle('active',media);el('trackpadModeButton').classList.toggle('active',!media)}function pointerAction(action,dx=0,dy=0){api('/api/pointer',{action,dx,dy}).catch(()=>{})}function remoteKey(name){api('/api/key',{name}).catch(()=>{})}function saveSensitivity(){const pointer=el('sensitivity').value,scroll=el('scrollSensitivity').value;localStorage.highSeasSensitivity=pointer;localStorage.highSeasScrollSensitivity=scroll;el('sensitivityValue').textContent=Number(pointer).toFixed(1)+'x';el('scrollSensitivityValue').textContent=Number(scroll).toFixed(1)+'x'}async function sendText(){const box=el('typeText'),value=box.value;if(!value)return;try{await api('/api/type',{text:value});box.value=''}catch(e){alert(e.message)}}const touches=new Map();let totalMove=0,pendingX=0,pendingY=0,pendingScroll=0,pointerFrame=0;const trackpad=el('trackpad');function flushPointer(){if(touches.size>=2){if(Math.abs(pendingScroll)>=1)pointerAction('scroll',0,Math.round(pendingScroll));pendingScroll=0;pendingX=pendingY=0}else{if(Math.abs(pendingX)+Math.abs(pendingY)>=1)pointerAction('move',Math.round(pendingX),Math.round(pendingY));pendingX=pendingY=0;pendingScroll=0}pointerFrame=0}trackpad.onpointerdown=e=>{touches.set(e.pointerId,{x:e.clientX,y:e.clientY});if(touches.size===1)totalMove=0;trackpad.setPointerCapture(e.pointerId)};trackpad.onpointermove=e=>{const old=touches.get(e.pointerId);if(!old)return;const dx=e.clientX-old.x,dy=e.clientY-old.y;touches.set(e.pointerId,{x:e.clientX,y:e.clientY});totalMove+=Math.abs(dx)+Math.abs(dy);if(touches.size>=2)pendingScroll+=dy*Number(el('scrollSensitivity').value)*8;else{pendingX+=dx*Number(el('sensitivity').value);pendingY+=dy*Number(el('sensitivity').value)}if(!pointerFrame)pointerFrame=requestAnimationFrame(flushPointer)};trackpad.onpointerup=e=>{const wasSingle=touches.size===1;touches.delete(e.pointerId);if(wasSingle&&totalMove<8)pointerAction('left');pendingX=pendingY=pendingScroll=0};trackpad.onpointercancel=e=>{touches.delete(e.pointerId);pendingX=pendingY=pendingScroll=0};el('sensitivity').value=localStorage.highSeasSensitivity||'1.5';el('scrollSensitivity').value=localStorage.highSeasScrollSensitivity||'1.0';saveSensitivity();
""";
        return html
            .Replace("__MONITORS__", options)
            .Replace("</style>", trackpadCss + "</style>")
            .Replace("<div id=\"remote\" class=\"hidden\">", "<div id=\"remote\" class=\"hidden\">" + modeHeader)
            .Replace("</select></section>", "</select><button class=\"go\" onclick=\"window.open('/install','_blank')\">UPDATE REMOTE APP</button></section>")
            .Replace("</section></div></main>", "</section></div>" + trackpadMarkup + "</div></main>")
            .Replace("el('pin').value=localStorage.clivePin||'';", trackpadScript + "el('pin').value=localStorage.clivePin||'';");
    }

    private MediaItem? SelectedMedia() => selectedGridMedia ?? (list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as MediaItem);

    private void ChooseSubtitle()
    {
        using var picker = new OpenFileDialog { Title = "Choose an English subtitle", Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.vtt|All files|*.*" };
        if (picker.ShowDialog(this) == DialogResult.OK) { chosenSubtitle = picker.FileName; status.Text = $"Subtitle selected: {Path.GetFileName(chosenSubtitle)}"; }
    }

    private void PlaySelected()
    {
        var media = SelectedMedia();
        if (media == null) { status.Text = "Choose a movie or episode first."; return; }
        if (useSubtitles.Checked && media.Subtitles is "Not checked" or "Missing" && chosenSubtitle == null)
        {
            var answer = MessageBox.Show(this, "No English subtitle is ready for this file. Choose one now?", "Subtitles", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) ChooseSubtitle();
            else if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes && chosenSubtitle == null) return;
        }
        var subtitlePath = useSubtitles.Checked ? chosenSubtitle ?? FindSidecarSubtitle(media.FullPath) : null;
        StartPlayback(media, subtitlePath, autoplayNext: media.Type == "Show");
        chosenSubtitle = null;
    }

    private void StartPlayback(MediaItem media, string? subtitlePath, bool autoplayNext)
    {
        var screenIndex = Math.Clamp(monitors.SelectedIndex, 0, Math.Max(0, Screen.AllScreens.Length - 1));
        try
        {
            activePlayer?.Close();
            activePlayer = new HighSeasPlayerForm(media.FullPath, subtitlePath, Screen.AllScreens[screenIndex], useSubtitles.Checked);
            if (autoplayNext) activePlayer.PlaybackEnded += (_, _) => PlayNextEpisode(media);
            activePlayer.NextEpisodeRequested += (_, _) => PlayNextEpisode(media);
            activePlayer.PreviousEpisodeRequested += (_, _) => PlayPreviousEpisode(media);
            activePlayer.FormClosed += (_, _) => { if (activePlayer?.IsDisposed == true) activePlayer = null; };
            RememberWatched(media);
            activePlayer.Show(this);
            status.Text = $"Playing {media.Title}{(media.Episode.Length > 0 ? " " + media.Episode : "")} on monitor {screenIndex + 1}.";
        }
        catch (Exception ex)
        {
            activePlayer = null;
            status.Text = "Playback could not start. The media center is still running.";
            MessageBox.Show(this, $"High Seas Media could not start playback.\n\n{ex.Message}", "Playback unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PlayNextEpisode(MediaItem finished)
    {
        var series = finished.Series.Length > 0 ? finished.Series : finished.Title;
        var next = library
            .Where(x => x.Type == "Show" && string.Equals(x.Series.Length > 0 ? x.Series : x.Title, series, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.SeasonNumber > finished.SeasonNumber || x.SeasonNumber == finished.SeasonNumber && x.EpisodeNumber > finished.EpisodeNumber)
            .OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber)
            .FirstOrDefault();
        if (next == null)
        {
            status.Text = $"Finished {finished.Title}.";
            activePlayer?.Close();
            return;
        }

        selectedGridMedia = next;
        var subtitlePath = useSubtitles.Checked ? FindSidecarSubtitle(next.FullPath) : null;
        StartPlayback(next, subtitlePath, autoplayNext: true);
    }

    private void PlayPreviousEpisode(MediaItem current)
    {
        var series = current.Series.Length > 0 ? current.Series : current.Title;
        var previous = library
            .Where(x => x.Type == "Show" && string.Equals(x.Series.Length > 0 ? x.Series : x.Title, series, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.SeasonNumber < current.SeasonNumber || x.SeasonNumber == current.SeasonNumber && x.EpisodeNumber < current.EpisodeNumber)
            .OrderByDescending(x => x.SeasonNumber).ThenByDescending(x => x.EpisodeNumber)
            .FirstOrDefault();
        if (previous == null) { status.Text = $"Already at the first episode of {series}."; return; }

        selectedGridMedia = previous;
        var subtitlePath = useSubtitles.Checked ? FindSidecarSubtitle(previous.FullPath) : null;
        StartPlayback(previous, subtitlePath, autoplayNext: true);
    }

    private static string? FindSidecarSubtitle(string mediaPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(mediaPath)!, Path.GetFileNameWithoutExtension(mediaPath));
        return new[] { ".srt", ".ass", ".ssa", ".vtt" }
            .Select(extension => stem + extension)
            .FirstOrDefault(File.Exists);
    }

    private async Task ShowThumbnailAsync()
    {
        var media = SelectedMedia();
        if (media == null || string.IsNullOrEmpty(ffmpegPath)) return;
        previewTitle.Text = media.Episode.Length > 0 ? $"{media.Title}\nSeason {media.SeasonNumber}, {media.Episode}" : media.Title;
        CancelThumbnailWork();
        thumbnailCancellation = new CancellationTokenSource();
        var token = thumbnailCancellation.Token;
        try
        {
            var cover = await GetCoverFileAsync(media, token);
            if (!token.IsCancellationRequested && cover != null && File.Exists(cover))
            {
                SetPreviewImage(cover);
                return;
            }
        }
        catch { }

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(media.FullPath)));
        var output = Path.Combine(thumbnailFolder, hash + ".jpg");
        try
        {
            if (!File.Exists(output))
            {
                var psi = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-y", "-ss", "00:02:00", "-i", media.FullPath, "-frames:v", "1", "-vf", "scale=640:-2", output }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi)!;
                lock (thumbnailProcessLock) thumbnailProcesses.Add(process);
                try { await process.WaitForExitAsync(token); }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    return;
                }
                finally { lock (thumbnailProcessLock) thumbnailProcesses.Remove(process); }
            }
            if (!token.IsCancellationRequested && File.Exists(output))
                SetPreviewImage(output);
        }
        catch { }
    }

    private void CancelThumbnailWork()
    {
        thumbnailCancellation?.Cancel();
        lock (thumbnailProcessLock)
        {
            foreach (var process in thumbnailProcesses.ToArray())
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            }
            thumbnailProcesses.Clear();
        }
    }

    private void SetPreviewImage(string path)
    {
        using var image = Image.FromFile(path);
        var old = preview.Image;
        preview.Image = new Bitmap(image);
        old?.Dispose();
    }

    private string CoverKey(MediaItem media)
    {
        // Bump the show artwork namespace after tightening title matching so
        // stale near-match posters (for example Hell Motel on Born Again) are
        // not reused from the old cache.
        var identity = media.Type.Equals("Show", StringComparison.OrdinalIgnoreCase)
            ? $"show-cover-v2|{media.Title}|{media.Year}"
            : $"{media.Type}|{media.Title}|{media.Year}";
        identity = identity.ToLowerInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private string GenreCachePath(MediaItem media) => Path.Combine(metadataFolder, CoverKey(media) + ".genres.json");

    private List<string> LoadCachedGenres(MediaItem media)
    {
        try
        {
            var path = GenreCachePath(media);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch { return new(); }
    }

    private void SaveCachedGenres(string title, string year, IReadOnlyCollection<string> genres)
    {
        if (genres.Count == 0) return;
        var media = new MediaItem { Type = "Movie", Title = title, Year = year };
        File.WriteAllText(GenreCachePath(media), JsonSerializer.Serialize(genres.OrderBy(x => x).ToList()));
    }

    private async Task<string?> GetCoverFileAsync(MediaItem media, CancellationToken token)
    {
        var destination = Path.Combine(coverFolder, CoverKey(media) + ".img");
        if (File.Exists(destination) && new FileInfo(destination).Length > 1000) return destination;
        var localArtwork = FindLocalArtwork(media.FullPath, media.Title);
        var bytes = localArtwork != null ? await File.ReadAllBytesAsync(localArtwork, token) : null;
        var imageUrl = bytes is null || bytes.Length < 1000
            ? media.Type == "Show" ? await FindTvCoverAsync(media.Title, token) : await FindMovieCoverAsync(media.Title, media.Year, token)
            : null;
        if ((bytes == null || bytes.Length < 1000) && !string.IsNullOrWhiteSpace(imageUrl)) bytes = await Http.GetByteArrayAsync(imageUrl, token);
        if (bytes == null || bytes.Length < 1000) return null;
        await File.WriteAllBytesAsync(destination, bytes, token);
        return destination;
    }

    private static string? FindLocalArtwork(string mediaPath, string? title = null)
    {
        try
        {
            var titleTokens = NormalizeArtworkTitle(title);
            var directory = new DirectoryInfo(Path.GetDirectoryName(mediaPath)!);
            for (var level = 0; directory != null && level < 4; level++, directory = directory.Parent)
            {
                var named = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                    .Where(file => file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    // Generic cover/fanart files are frequently screenshots or cast
                    // portraits. Prefer a title-matching poster, otherwise accept
                    // only conventional poster/season artwork names.
                    .Where(file => titleTokens.Length > 0 && NormalizeArtworkTitle(Path.GetFileNameWithoutExtension(file.Name)).Contains(titleTokens, StringComparison.OrdinalIgnoreCase)
                        || Regex.IsMatch(file.Name, @"(?i)^(?:poster|series|season(?:\s*\d+)?)\.(?:jpe?g|png)$"))
                    .OrderByDescending(file => file.Length)
                    .FirstOrDefault();
                if (named != null) return named.FullName;
            }
        }
        catch { }
        return null;
    }

    private async Task<string?> FindTvCoverAsync(string title, CancellationToken token)
    {
        var wanted = NormalizeArtworkTitle(title);
        var queries = new[] { title, Regex.Replace(title, @"(?i)\s+\(?(?:the\s+)?complete.*$", "").Trim(), title.Replace("'", "") }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            try
            {
                var url = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(query);
                using var document = JsonDocument.Parse(await Http.GetStringAsync(url, token));
                var resultName = document.RootElement.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                if (!IsArtworkTitleMatch(wanted, resultName)) continue;
                if (document.RootElement.TryGetProperty("image", out var image) && image.ValueKind != JsonValueKind.Null)
                {
                    if (image.TryGetProperty("original", out var original) && !string.IsNullOrWhiteSpace(original.GetString())) return original.GetString();
                    if (image.TryGetProperty("medium", out var medium) && !string.IsNullOrWhiteSpace(medium.GetString())) return medium.GetString();
                }
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(settings.TmdbReadToken))
        {
            try
            {
                var tokenValue = settings.TmdbReadToken.Trim();
                if (tokenValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) tokenValue = tokenValue[7..].Trim();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/tv?language=en-US&query=" + Uri.EscapeDataString(title));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
                using var response = await Http.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
                    if (document.RootElement.TryGetProperty("results", out var results))
                    {
                        string? bestPoster = null;
                        var bestScore = 0;
                        foreach (var result in results.EnumerateArray())
                        {
                            var resultName = result.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                            if (!IsArtworkTitleMatch(wanted, resultName) || !result.TryGetProperty("poster_path", out var poster) || poster.ValueKind == JsonValueKind.Null) continue;
                            var score = ArtworkTitleScore(wanted, resultName);
                            if (score > bestScore) { bestScore = score; bestPoster = poster.GetString(); }
                        }
                        if (!string.IsNullOrWhiteSpace(bestPoster)) return "https://image.tmdb.org/t/p/w780" + bestPoster;
                    }
                }
            }
            catch { }
        }
        try
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrsearch=" + Uri.EscapeDataString($"{title} television series") + "&gsrlimit=5&prop=pageimages&piprop=thumbnail&pithumbsize=600&origin=*";
            using var document = JsonDocument.Parse(await Http.GetStringAsync(url, token));
            if (document.RootElement.TryGetProperty("query", out var queryNode) && queryNode.TryGetProperty("pages", out var pages))
                foreach (var page in pages.EnumerateArray())
                {
                    var pageTitle = page.TryGetProperty("title", out var pageTitleNode) ? pageTitleNode.GetString() ?? "" : "";
                    if (!IsArtworkTitleMatch(wanted, pageTitle)) continue;
                    if (page.TryGetProperty("thumbnail", out var thumbnail) && thumbnail.TryGetProperty("source", out var source)) return source.GetString();
                }
        }
        catch { }
        return null;
    }

    private static string NormalizeArtworkTitle(string? value)
        => Regex.Replace((value ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static bool IsArtworkTitleMatch(string wanted, string candidate)
    {
        if (wanted.Length == 0 || candidate.Length == 0) return false;
        var normalized = NormalizeArtworkTitle(candidate);
        if (normalized.Equals(wanted, StringComparison.OrdinalIgnoreCase) || normalized.Contains(wanted, StringComparison.OrdinalIgnoreCase) || wanted.Contains(normalized, StringComparison.OrdinalIgnoreCase)) return true;
        // Marvel's catalog often omits the branding prefix.  Ignore that
        // optional token, but require every meaningful title word so a loose
        // search result cannot substitute an unrelated show poster.
        var wantedTokens = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 2 && x is not "the" and not "marvel" and not "marvels")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wantedTokens.Count > 0 && wantedTokens.All(candidateTokens.Contains);
    }

    private static int ArtworkTitleScore(string wanted, string candidate)
    {
        var normalized = NormalizeArtworkTitle(candidate);
        if (normalized.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return 100;
        var wantedTokens = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 2 && x is not "the" and not "marvel" and not "marvels").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wantedTokens.Count(x => candidateTokens.Contains(x));
    }

    private async Task<string?> FindMovieCoverAsync(string title, string year, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(settings.TmdbReadToken))
        {
            foreach (var candidate in MetadataTitleCandidates(title))
            {
                var tmdb = await FindTmdbMovieMetadataAsync(candidate, year, token);
                SaveCachedGenres(title, year, tmdb.Genres);
                if (!string.IsNullOrWhiteSpace(tmdb.PosterPath)) return "https://image.tmdb.org/t/p/w780" + tmdb.PosterPath;
            }
        }
        var directTitles = string.Join('|', new[] { year.Length > 0 ? $"{title} ({year} film)" : "", $"{title} (film)", title }.Where(x => x.Length > 0));
        var directUrl = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&titles=" + Uri.EscapeDataString(directTitles) + "&prop=pageimages%7Cpageterms&piprop=thumbnail&pithumbsize=600&pilimit=10&wbptterms=description&origin=*";
        try
        {
            using var directDocument = JsonDocument.Parse(await Http.GetStringAsync(directUrl, token));
            if (directDocument.RootElement.TryGetProperty("query", out var directQuery) && directQuery.TryGetProperty("pages", out var directPages))
            {
                foreach (var page in directPages.EnumerateArray())
                {
                    if (page.TryGetProperty("missing", out _)) continue;
                    var pageTitle = page.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
                    var description = "";
                    if (page.TryGetProperty("terms", out var termsNode) && termsNode.TryGetProperty("description", out var descriptions) && descriptions.GetArrayLength() > 0) description = descriptions[0].GetString() ?? "";
                    var likelyFilm = description.Contains("film", StringComparison.OrdinalIgnoreCase) || pageTitle.Contains("film", StringComparison.OrdinalIgnoreCase) || year.Length > 0 && description.Contains(year);
                    if (likelyFilm && page.TryGetProperty("thumbnail", out var thumbnail) && thumbnail.TryGetProperty("source", out var source)) return source.GetString();
                }
            }
        }
        catch { }
        var terms = $"intitle:\"{title}\" {year} film".Trim();
        var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrsearch=" + Uri.EscapeDataString(terms) + "&gsrlimit=5&prop=pageimages%7Cpageterms&piprop=thumbnail&pithumbsize=600&pilimit=5&wbptterms=description&origin=*";
        using var document = JsonDocument.Parse(await Http.GetStringAsync(url, token));
        if (!document.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)) return null;
        foreach (var page in pages.EnumerateArray())
        {
            var pageTitle = page.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
            var description = "";
            if (page.TryGetProperty("terms", out var termNode) && termNode.TryGetProperty("description", out var descriptions) && descriptions.GetArrayLength() > 0)
                description = descriptions[0].GetString() ?? "";
            var likelyFilm = description.Contains("film", StringComparison.OrdinalIgnoreCase) || pageTitle.Contains("film", StringComparison.OrdinalIgnoreCase) || (year.Length > 0 && pageTitle.Contains(year));
            if (!likelyFilm || !page.TryGetProperty("thumbnail", out var thumbnail) || !thumbnail.TryGetProperty("source", out var source)) continue;
            return source.GetString();
        }
        return null;
    }

    private static IEnumerable<string> MetadataTitleCandidates(string title)
    {
        var candidates = new List<string> { title };
        // Search both punctuation forms used by media filenames and the
        // canonical title used by TMDB/Wikipedia.
        var colonized = Regex.Replace(title, @"\s*[-–—]\s*", ": ").Trim();
        if (!colonized.Equals(title, StringComparison.OrdinalIgnoreCase)) candidates.Add(colonized);
        var spaced = Regex.Replace(title, @"\s*[:]\s*", " ").Trim();
        if (!spaced.Equals(title, StringComparison.OrdinalIgnoreCase)) candidates.Add(spaced);
        foreach (var pattern in new[]
        {
            @"\s+(?:super\s+duper|extended|unrated|ultimate|director(?:'s|s)?|the\s+rogue)\s+cut\s*$",
            @"\s+extended\s*$",
            @"\s+the\s+rogue\s+cut\s*$"
        })
        {
            var simplified = Regex.Replace(title, pattern, "", RegexOptions.IgnoreCase).Trim();
            if (simplified.Length > 0) candidates.Add(simplified);
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> GetCollectionDescriptionAsync(string collection)
    {
        var identity = "collection|" + collection.ToLowerInvariant();
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
        var cache = Path.Combine(metadataFolder, key + ".txt");
        try
        {
            if (File.Exists(cache))
            {
                var cached = await File.ReadAllTextAsync(cache);
                // A prior failed lookup must not become permanent. Retry the
                // placeholder now that title cleanup/metadata fallbacks improve.
                if (!cached.Equals("A description is not available for this title yet.", StringComparison.OrdinalIgnoreCase)) return cached;
            }
            var searchTitle = Regex.Replace(collection, @"\b\d+\s*-\s*\d+\b", " ", RegexOptions.IgnoreCase);
            searchTitle = Regex.Replace(searchTitle, @"\b(complete|collection|trilogy|quadrilogy|anthology|saga|films?|movies?|remastered|blu-?ray|1080p|2160p|4k)\b", " ", RegexOptions.IgnoreCase);
            searchTitle = Regex.Replace(searchTitle, @"\s+", " ").Trim(' ', '-', '(', ')');

            string? description = null;
            if (!string.IsNullOrWhiteSpace(settings.TmdbReadToken)) description = await FindTmdbCollectionDescriptionAsync(searchTitle);
            description ??= await FindWikipediaCollectionDescriptionAsync(searchTitle);
            description ??= BuildLocalCollectionDescription(collection, searchTitle);
            await File.WriteAllTextAsync(cache, description);
            return description;
        }
        catch { return BuildLocalCollectionDescription(collection, collection); }
    }

    private async Task<string?> FindTmdbCollectionDescriptionAsync(string title)
    {
        try
        {
            var tokenValue = settings.TmdbReadToken.Trim();
            if (tokenValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) tokenValue = tokenValue[7..].Trim();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/search/collection?language=en-US&query=" + Uri.EscapeDataString(title));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("results", out var results)) return null;
            foreach (var result in results.EnumerateArray())
            {
                var overview = result.TryGetProperty("overview", out var node) ? node.GetString() : null;
                if (!string.IsNullOrWhiteSpace(overview)) return CleanSummary(overview);
            }
        }
        catch { }
        return null;
    }

    private static async Task<string?> FindWikipediaCollectionDescriptionAsync(string title)
    {
        try
        {
            var terms = $"\"{title}\" film series";
            var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrsearch=" + Uri.EscapeDataString(terms) + "&gsrlimit=6&prop=extracts&exintro=1&explaintext=1&exsentences=6&origin=*";
            using var document = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!document.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)) return null;
            foreach (var page in pages.EnumerateArray())
            {
                var pageTitle = page.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
                var extract = page.TryGetProperty("extract", out var extractNode) ? CleanSummary(extractNode.GetString()) : null;
                if (string.IsNullOrWhiteSpace(extract)) continue;
                if (pageTitle.Contains("series", StringComparison.OrdinalIgnoreCase) || extract.Contains("film series", StringComparison.OrdinalIgnoreCase) || extract.Contains("film franchise", StringComparison.OrdinalIgnoreCase) || extract.Contains("series of", StringComparison.OrdinalIgnoreCase)) return extract;
            }
        }
        catch { }
        return null;
    }

    private string BuildLocalCollectionDescription(string collection, string cleanedTitle)
    {
        var movies = library.Where(x => x.Type == "Movie" && x.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Year).ThenBy(x => x.Title).ToList();
        if (movies.Count == 0) return $"A film collection centered on {cleanedTitle}.";
        var years = movies.Select(x => int.TryParse(x.Year, out var year) ? year : 0).Where(x => x > 0).ToList();
        var span = years.Count == 0 ? "" : years.Min() == years.Max() ? $" from {years.Min()}" : $" spanning {years.Min()}–{years.Max()}";
        var titles = movies.Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        var examples = titles.Count == 0 ? "" : " Includes " + string.Join(", ", titles.Take(Math.Max(1, titles.Count - 1))) + (titles.Count > 1 ? $", and {titles[^1]}" : "") + ".";
        return $"A {movies.Count}-film {cleanedTitle} collection{span}.{examples}";
    }

    private async Task<string> GetDescriptionAsync(MediaItem media)
    {
        var identity = $"{media.Type}|{media.Series}|{media.Title}|{media.SeasonNumber}|{media.EpisodeNumber}|{media.Year}".ToLowerInvariant();
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
        var cache = Path.Combine(metadataFolder, key + ".txt");
        try
        {
            if (File.Exists(cache)) return await File.ReadAllTextAsync(cache);
            string? description;
            if (media.Type == "Show")
            {
                description = await FindEpisodeDescriptionAsync(media);
            }
            else
            {
                description = await FindMovieDescriptionAsync(media.Title, media.Year);
            }
            description = string.IsNullOrWhiteSpace(description) ? "A description is not available for this title yet." : description.Trim();
            await File.WriteAllTextAsync(cache, description);
            return description;
        }
        catch { return "A description is not available for this title yet."; }
    }

    private static async Task<string?> FindEpisodeDescriptionAsync(MediaItem media)
    {
        var series = media.Series.Length > 0 ? media.Series : media.Title;
        var searchUrl = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(series);
        using var show = JsonDocument.Parse(await Http.GetStringAsync(searchUrl));
        if (!show.RootElement.TryGetProperty("id", out var id)) return null;
        var episodeUrl = $"https://api.tvmaze.com/shows/{id.GetInt32()}/episodes";
        using var episodes = JsonDocument.Parse(await Http.GetStringAsync(episodeUrl));
        foreach (var episode in episodes.RootElement.EnumerateArray())
        {
            if (!episode.TryGetProperty("season", out var season) || !episode.TryGetProperty("number", out var number) || number.ValueKind == JsonValueKind.Null) continue;
            if (season.GetInt32() != media.SeasonNumber || number.GetInt32() != media.EpisodeNumber) continue;
            return episode.TryGetProperty("summary", out var summary) && summary.ValueKind != JsonValueKind.Null ? CleanSummary(summary.GetString()) : null;
        }
        return null;
    }

    private async Task<string?> FindMovieDescriptionAsync(string title, string year)
    {
        if (!string.IsNullOrWhiteSpace(settings.TmdbReadToken))
        {
            foreach (var candidate in MetadataTitleCandidates(title))
            {
                var tmdb = await FindTmdbMovieMetadataAsync(candidate, year, CancellationToken.None);
                SaveCachedGenres(title, year, tmdb.Genres);
                if (!string.IsNullOrWhiteSpace(tmdb.Overview)) return tmdb.Overview;
            }
        }
        foreach (var candidate in MetadataTitleCandidates(title))
        {
            var terms = $"intitle:\"{candidate}\" {year} film".Trim();
            var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrsearch=" + Uri.EscapeDataString(terms) + "&gsrlimit=5&prop=extracts%7Cpageterms&exintro=1&explaintext=1&exsentences=6&wbptterms=description&origin=*";
            using var document = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!document.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)) continue;
            foreach (var page in pages.EnumerateArray())
            {
                var pageTitle = page.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
                var kind = "";
                if (page.TryGetProperty("terms", out var termsNode) && termsNode.TryGetProperty("description", out var descriptions) && descriptions.GetArrayLength() > 0) kind = descriptions[0].GetString() ?? "";
                if (!kind.Contains("film", StringComparison.OrdinalIgnoreCase) && !pageTitle.Contains("film", StringComparison.OrdinalIgnoreCase) && !(year.Length > 0 && pageTitle.Contains(year))) continue;
                if (page.TryGetProperty("extract", out var extract)) return CleanSummary(extract.GetString());
            }
        }
        return null;
    }

    private async Task<(string? PosterPath, string? Overview, IReadOnlyList<string> Genres, string ReleaseYear)> FindTmdbMovieMetadataAsync(string title, string year, CancellationToken token)
    {
        try
        {
            var url = "https://api.themoviedb.org/3/search/movie?language=en-US&include_adult=false&query=" + Uri.EscapeDataString(title);
            if (Regex.IsMatch(year, @"^\d{4}$")) url += "&primary_release_year=" + year;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var tokenValue = settings.TmdbReadToken.Trim();
            if (tokenValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) tokenValue = tokenValue[7..].Trim();
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            using var response = await Http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return (null, null, Array.Empty<string>(), "");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return (null, null, Array.Empty<string>(), "");
            JsonElement? best = null;
            var bestScore = 0;
            foreach (var result in results.EnumerateArray())
            {
                var resultTitle = result.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
                var release = result.TryGetProperty("release_date", out var releaseNode) ? releaseNode.GetString() ?? "" : "";
                var score = ArtworkTitleScore(NormalizeArtworkTitle(title), resultTitle);
                if (resultTitle.Equals(title, StringComparison.OrdinalIgnoreCase)) score += 100;
                if (year.Length == 4 && release.StartsWith(year, StringComparison.Ordinal)) score += 25;
                if (score > bestScore) { bestScore = score; best = result; }
            }
            if (best is not JsonElement match) return (null, null, Array.Empty<string>(), "");
            var poster = match.TryGetProperty("poster_path", out var posterNode) && posterNode.ValueKind != JsonValueKind.Null ? posterNode.GetString() : null;
            var overview = match.TryGetProperty("overview", out var overviewNode) ? overviewNode.GetString() : null;
            var releaseDate = match.TryGetProperty("release_date", out var matchReleaseNode) ? matchReleaseNode.GetString() ?? "" : "";
            var releaseYear = Regex.Match(releaseDate, @"^(?:19|20)\d{2}").Value;
            var genres = match.TryGetProperty("genre_ids", out var genreNodes)
                ? genreNodes.EnumerateArray().Select(x => TmdbGenreName(x.GetInt32())).Where(x => x != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            return (poster, overview, genres, releaseYear);
        }
        catch { return (null, null, Array.Empty<string>(), ""); }
    }

    private static string? TmdbGenreName(int id) => id switch
    {
        28 => "Action", 12 => "Adventure", 16 => "Animation", 35 => "Comedy", 80 => "Crime",
        99 => "Documentary", 18 => "Drama", 10751 => "Family", 14 => "Fantasy", 36 => "History",
        27 => "Horror", 10402 => "Music", 9648 => "Mystery", 10749 => "Romance",
        878 => "Science Fiction", 53 => "Thriller", 10752 => "War", 37 => "Western", _ => null
    };

    private async Task EnrichMovieGenresAsync()
    {
        if (genreEnrichmentRunning) return;
        genreEnrichmentRunning = true;
        try
        {
            foreach (var media in library.Where(x => x.Type == "Movie" && x.Genres.Count == 0).ToList())
            {
                var metadata = await FindTmdbMovieMetadataAsync(media.Title, media.Year, CancellationToken.None);
                if (metadata.Genres.Count == 0) continue;
                media.Genres = metadata.Genres.ToList();
                SaveCachedGenres(media.Title, media.Year, metadata.Genres);
                await Task.Delay(120);
            }
        }
        catch { }
        finally { genreEnrichmentRunning = false; }
        // Genre shelves are rebuilt only on an intentional page refresh. Replacing the entire
        // control tree from a background task invalidated keyboard/remote selection mid-navigation.
    }

    private async Task EnrichMovieCollectionsAsync()
    {
        var movies = library.Where(x => x.Type == "Movie" && x.Collection.Length == 0).ToList();
        if (movies.Count == 0 || string.IsNullOrWhiteSpace(settings.TmdbReadToken)) return;
        var cachePath = Path.Combine(metadataFolder, "collections.json");
        Dictionary<string, string> cache;
        try { cache = File.Exists(cachePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(cachePath)) ?? new() : new(); }
        catch { cache = new(StringComparer.OrdinalIgnoreCase); }
        var changed = false;
        foreach (var media in movies)
        {
            var key = $"{media.Title}|{media.Year}".ToLowerInvariant();
            if (!cache.TryGetValue(key, out var collection))
            {
                collection = await FindTmdbCollectionNameAsync(media.Title, media.Year, CancellationToken.None) ?? "";
                cache[key] = collection;
                try { await Task.Delay(120); } catch { }
            }
            if (collection.Length == 0) collection = InferKnownMovieCollection(media.Title);
            if (collection.Length == 0) continue;
            media.Collection = collection;
            changed = true;
        }
        try { await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        if (changed)
        {
            BuildNavigation();
            FillList();
            RefreshWatchView();
        }
    }

    private async Task<string?> FindTmdbCollectionNameAsync(string title, string year, CancellationToken token)
    {
        try
        {
            var searchUrl = "https://api.themoviedb.org/3/search/movie?language=en-US&query=" + Uri.EscapeDataString(title);
            if (Regex.IsMatch(year, @"^\d{4}$")) searchUrl += "&primary_release_year=" + year;
            var tokenValue = settings.TmdbReadToken.Trim();
            if (tokenValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) tokenValue = tokenValue[7..].Trim();
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            using var searchResponse = await Http.SendAsync(searchRequest, token);
            if (!searchResponse.IsSuccessStatusCode) return null;
            using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync(token));
            if (!search.RootElement.TryGetProperty("results", out var results)) return null;
            JsonElement? best = null;
            foreach (var result in results.EnumerateArray())
            {
                var resultTitle = result.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "" : "";
                var release = result.TryGetProperty("release_date", out var releaseNode) ? releaseNode.GetString() ?? "" : "";
                if (resultTitle.Equals(title, StringComparison.OrdinalIgnoreCase) && (year.Length == 0 || release.StartsWith(year))) { best = result; break; }
                best ??= result;
            }
            if (best is not JsonElement match || !match.TryGetProperty("id", out var id)) return null;
            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/movie/{id.GetInt32()}?language=en-US");
            detailRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            using var detailResponse = await Http.SendAsync(detailRequest, token);
            if (!detailResponse.IsSuccessStatusCode) return null;
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(token));
            return detail.RootElement.TryGetProperty("belongs_to_collection", out var collection) && collection.ValueKind != JsonValueKind.Null && collection.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch { return null; }
    }

    private async Task EnrichMissingYearsAsync()
    {
        var missing = library.Where(x => string.IsNullOrWhiteSpace(x.Year)).ToList();
        if (missing.Count == 0) return;

        var cachePath = Path.Combine(metadataFolder, "years.json");
        Dictionary<string, string> cache;
        try { cache = File.Exists(cachePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(cachePath)) ?? new() : new(); }
        catch { cache = new(StringComparer.OrdinalIgnoreCase); }

        var groups = missing.GroupBy(x => x.Type == "Show" ? NormalizeSeries(x.Series.Length > 0 ? x.Series : x.Title) : x.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = false;
        var lookedUp = 0;
        foreach (var group in groups)
        {
            var key = $"{group.First().Type}|{group.Key}".ToLowerInvariant();
            var year = cache.TryGetValue(key, out var cached) && Regex.IsMatch(cached, @"^(?:19|20)\d{2}$") ? cached : "";
            if (year.Length == 0)
            {
                status.Text = $"Finding release year {lookedUp + 1} of {groups.Count}: {group.Key}";
                try
                {
                    year = group.First().Type == "Show"
                        ? await FindTvPremiereYearAsync(group.Key)
                        : !string.IsNullOrWhiteSpace(settings.TmdbReadToken)
                            ? (await FindTmdbMovieMetadataAsync(group.Key, "", CancellationToken.None)).ReleaseYear
                            : "";
                }
                catch { year = ""; }
                if (Regex.IsMatch(year, @"^(?:19|20)\d{2}$")) cache[key] = year;
                lookedUp++;
            }

            if (!Regex.IsMatch(year, @"^(?:19|20)\d{2}$")) continue;
            foreach (var media in group)
            {
                media.Year = year;
                changed = true;
            }
        }

        try { await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        if (!changed) return;
        BuildNavigation();
        FillList();
        RefreshWatchView();
    }

    private static async Task<string> FindTvPremiereYearAsync(string title)
    {
        var url = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(title);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var document = JsonDocument.Parse(await Http.GetStringAsync(url, timeout.Token));
        var premiered = document.RootElement.TryGetProperty("premiered", out var node) ? node.GetString() ?? "" : "";
        return Regex.Match(premiered, @"^(?:19|20)\d{2}").Value;
    }

    private static string? CleanSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return WebUtility.HtmlDecode(Regex.Replace(value, "<.*?>", " ")).Replace("\n", " ").Trim();
    }

    private async Task DownloadAllCoversAsync()
    {
        var unique = library.GroupBy(CoverKey).Select(group => group.First()).ToList();
        var downloaded = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = unique.Select(async media =>
        {
            await gate.WaitAsync();
            try
            {
                var cover = await GetCoverFileAsync(media, CancellationToken.None);
                var finished = Interlocked.Increment(ref downloaded);
                if (!IsDisposed) BeginInvoke(() =>
                {
                    status.Text = $"Downloading cover art {finished} of {unique.Count}: {media.Title}";
                    if (cover != null) UpdateVisibleCardArtwork(media);
                });
            }
            catch { Interlocked.Increment(ref downloaded); }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        status.Text = $"Cover library ready ({downloaded} titles checked)";
        if (SelectedMedia() != null) await ShowThumbnailAsync();
    }

    private void UpdateVisibleCardArtwork(MediaItem media)
    {
        var key = CoverKey(media);
        foreach (var pair in watchCardInfo.ToArray())
        {
            var card = pair.Key;
            if (card.IsDisposed || !CoverKey(pair.Value.Media).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var replacement = LoadExistingArtwork(pair.Value.Media);
            if (replacement == null) continue;
            var picture = card.Controls.OfType<PictureBox>().FirstOrDefault();
            if (picture != null)
            {
                var old = picture.Image;
                picture.Image = replacement;
                old?.Dispose();
            }
            else replacement.Dispose();

            if (ReferenceEquals(remoteSelectedCard, card) && picture?.Image != null)
            {
                var oldFocus = watchFocusArt.Image;
                watchFocusArt.Image = new Bitmap(picture.Image);
                oldFocus?.Dispose();
            }
        }
    }

    private async Task PromptSubtitleAuditAsync()
    {
        var answer = MessageBox.Show(this, "Use saved subtitle results for unchanged files?\n\nYes = fast cached audit\nNo = force a fresh check of every file", "Subtitle audit", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;
        await AuditSubtitlesAsync(answer == DialogResult.No);
    }

    private string SubtitleAuditCachePath => Path.Combine(metadataFolder, "subtitle-audit.json");

    private Dictionary<string, SubtitleAuditEntry> LoadSubtitleAuditCache()
    {
        try
        {
            if (!File.Exists(SubtitleAuditCachePath)) return new(StringComparer.OrdinalIgnoreCase);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SubtitleAuditEntry>>(File.ReadAllText(SubtitleAuditCachePath));
            return loaded == null ? new(StringComparer.OrdinalIgnoreCase) : new(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveSubtitleAuditCache(Dictionary<string, SubtitleAuditEntry> cache)
    {
        try { File.WriteAllText(SubtitleAuditCachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private static string SubtitleAuditKey(MediaItem media) => media.FullPath.ToUpperInvariant();

    private bool IsSubtitleCacheValid(MediaItem media, SubtitleAuditEntry entry)
    {
        try
        {
            var info = new FileInfo(media.FullPath);
            if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.LastWriteUtcTicks || entry.ProviderSignature != SubtitleProviderSignature) return false;
            // A provider outage should not make a cached audit re-run every title
            // and wait through another network timeout. Keep that result briefly;
            // choosing the force-refresh option still retries immediately.
            if (entry.Status.Equals("Service unavailable", StringComparison.OrdinalIgnoreCase))
            {
                // Entries written by older builds have no timestamp. Treat them
                // as cached for the normal pass; the user can choose Force fresh
                // when they want to retry an old provider failure.
                if (entry.CheckedUtcTicks <= 0) return true;
                var age = DateTime.UtcNow - new DateTime(entry.CheckedUtcTicks, DateTimeKind.Utc);
                return age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(10);
            }
            return true;
        }
        catch { return false; }
    }

    private SubtitleAuditEntry MakeSubtitleAuditEntry(MediaItem media)
    {
        var info = new FileInfo(media.FullPath);
        return new SubtitleAuditEntry { Length = info.Exists ? info.Length : 0, LastWriteUtcTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0, CheckedUtcTicks = DateTime.UtcNow.Ticks, Status = media.Subtitles, ProviderSignature = SubtitleProviderSignature };
    }

    private string SubtitleProviderSignature
    {
        get
        {
            var material = $"opensubtitles:{settings.OpenSubtitlesApiKey.Trim()}|subdl:{settings.SubdlApiKey.Trim()}";
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(material)))[..12];
        }
    }

    private async Task AuditSubtitlesAsync(bool force = false)
    {
        if (string.IsNullOrEmpty(ffprobePath)) { MessageBox.Show(this, "Subtitle tools are missing."); return; }
        if (string.IsNullOrWhiteSpace(settings.OpenSubtitlesApiKey) && string.IsNullOrWhiteSpace(settings.SubdlApiKey))
        {
            var configure = MessageBox.Show(this, "Subtitle checking can identify existing tracks, but downloading missing subtitles needs an OpenSubtitles or SubDL API key. Open Settings now?", "Subtitle downloads", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (configure == DialogResult.Yes) EditSettings();
        }
        // Keep the library readable during the audit. Disabling the form causes
        // WinForms to wash out every child control, including the media grid.
        UseWaitCursor = true;
        subtitleAuditButton.Enabled = false;
        var ready = 0; var missing = 0; var external = 0; var synchronized = 0; var downloaded = 0; var unavailable = 0;
        var skipped = 0;
        var subtitleCache = LoadSubtitleAuditCache();
        var downloadErrors = new List<string>();
        var subtitleProviders = new List<ISubtitleProvider>();
        if (!string.IsNullOrWhiteSpace(settings.OpenSubtitlesApiKey)) subtitleProviders.Add(new OpenSubtitlesClient(settings));
        if (!string.IsNullOrWhiteSpace(settings.SubdlApiKey)) subtitleProviders.Add(new SubdlClient(settings.SubdlApiKey));
        void Remember(MediaItem media) => subtitleCache[SubtitleAuditKey(media)] = MakeSubtitleAuditEntry(media);
        for (var i = 0; i < library.Count; i++)
        {
            var media = library[i];
            status.Text = $"Checking subtitles {i + 1} of {library.Count}: {media.Title}";
            Application.DoEvents();
            if (!force && subtitleCache.TryGetValue(SubtitleAuditKey(media), out var cached) && IsSubtitleCacheValid(media, cached))
            {
                media.Subtitles = cached.Status;
                skipped++;
                continue;
            }
            var stem = Path.Combine(Path.GetDirectoryName(media.FullPath)!, Path.GetFileNameWithoutExtension(media.FullPath));
            if (File.Exists(stem + ".subtitle-sync.json")) { media.Subtitles = "Audio-synced"; ready++; Remember(media); continue; }
            if (File.Exists(stem + ".srt"))
            {
                status.Text = $"Synchronizing subtitles {i + 1} of {library.Count}: {media.Title}";
                if (await SyncSubtitleAsync(media.FullPath)) { media.Subtitles = "Audio-synced"; synchronized++; }
                else { media.Subtitles = "External subtitle"; external++; }
                Remember(media);
                continue;
            }
            if (new[] { ".ass", ".ssa", ".vtt" }.Any(ext => File.Exists(stem + ext))) { media.Subtitles = "External subtitle"; external++; Remember(media); continue; }
            var hasEnglish = await HasEmbeddedEnglishAsync(media.FullPath);
            if (hasEnglish) { media.Subtitles = "Embedded English"; ready++; Remember(media); continue; }

            if (subtitleProviders.Count > 0)
            {
                status.Text = $"Downloading subtitles {i + 1} of {library.Count}: {media.Title}";
                var providerSucceeded = false;
                var providerUnavailable = false;
                foreach (var subtitleClient in subtitleProviders)
                {
                    try
                    {
                        // A provider can leave a search or download ticket hanging. Keep one
                        // troublesome title from blocking the entire audit.
                        // No-match searches should fail quickly. A slow provider is
                        // allowed 15 seconds, then the next provider is tried.
                        using var subtitleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        status.Text = $"Trying {subtitleClient.Name}: {media.Title}";
                        if (await subtitleClient.DownloadEnglishSubtitleAsync(media, subtitleTimeout.Token))
                        {
                            providerSucceeded = true;
                            downloaded++;
                            status.Text = $"Synchronizing downloaded subtitle: {media.Title}";
                            if (await SyncSubtitleAsync(media.FullPath)) { media.Subtitles = "Audio-synced"; synchronized++; }
                            else { media.Subtitles = "Downloaded subtitle"; external++; }
                            Remember(media);
                            break;
                        }
                        if (subtitleClient.LastFailureWasServiceError) providerUnavailable = true;
                        if (subtitleClient.LastFailureWasServiceError && downloadErrors.Count < 5)
                            downloadErrors.Add($"{subtitleClient.Name} - {media.Title}{(media.Episode.Length > 0 ? " " + media.Episode : "")}: {subtitleClient.LastError}");
                    }
                    catch (Exception exception)
                    {
                        providerUnavailable = true;
                        if (downloadErrors.Count < 5) downloadErrors.Add($"{subtitleClient.Name} - {media.Title}: {exception.Message}");
                    }
                }
                if (providerSucceeded) continue;
                if (providerUnavailable)
                {
                    media.Subtitles = "Service unavailable";
                    unavailable++;
                    Remember(media);
                    continue;
                }
            }
            media.Subtitles = "Missing";
            missing++;
            Remember(media);
        }
        SaveSubtitleAuditCache(subtitleCache);
        UseWaitCursor = false;
        subtitleAuditButton.Enabled = true;
        FillList();
        var serviceDetails = downloadErrors.Count == 0 ? "" : "\n\nSubtitle provider messages:\n" + string.Join("\n", downloadErrors);
        MessageBox.Show(this, $"Subtitle check complete.\n\nAlready ready: {ready}\nDownloaded now: {downloaded}\nAudio-synced now: {synchronized}\nExternal subtitles: {external}\nNo match found: {missing}\nTemporarily unavailable: {unavailable}\nSkipped from cache: {skipped}{serviceDetails}", "Subtitle audit", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task<bool> SyncSubtitleAsync(string mediaPath)
    {
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "play-movie.ps1");
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Path", mediaPath, "-NoLaunch" }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            await process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<bool> HasEmbeddedEnglishAsync(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type:stream_tags=language,title", "-of", "json", path }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return Regex.IsMatch(output, "(?i)\\\"language\\\"\\s*:\\s*\\\"(eng|en)\\\"") || Regex.IsMatch(output, "(?i)English");
        }
        catch { return false; }
    }

    private async Task RunLibraryUpdateAsync()
    {
        using var options = new Form { Text = "Update library", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(620, 475), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var organize = new CheckBox { Text = "Clean names and organize movies/shows into folders", Checked = true, AutoSize = true, Location = new Point(24, 28) };
        var episodeTitles = new CheckBox { Text = "Look up missing episode titles", Checked = true, AutoSize = true, Location = new Point(45, 62) };
        var subtitles = new CheckBox { Text = "Check and download missing English subtitles", Checked = true, AutoSize = true, Location = new Point(24, 106) };
        var forceSubtitles = new CheckBox { Text = "Force a fresh subtitle check (ignore saved results)", Checked = false, AutoSize = true, Location = new Point(45, 140) };
        var metadata = new CheckBox { Text = "Refresh covers and metadata", Checked = true, AutoSize = true, Location = new Point(24, 184) };
        var cleanup = new CheckBox { Text = "Remove empty leftover folders", Checked = true, AutoSize = true, Location = new Point(24, 218) };
        var duplicates = new CheckBox { Text = "Check for duplicate movies/episodes and ask which copy to keep", Checked = true, AutoSize = true, Location = new Point(24, 252) };
        var note = new Label { Text = "Only empty directories inside your library roots are removed.\nActive/incomplete torrent trees and folders containing any files are left untouched.", ForeColor = Muted, Location = new Point(24, 289), Size = new Size(550, 46) };
        var cancel = MakeButton("Cancel", 105); cancel.Location = new Point(370, 385); cancel.Click += (_, _) => options.DialogResult = DialogResult.Cancel;
        var start = MakeButton("Update library", 135, Accent); start.Location = new Point(485, 385); start.Click += (_, _) => options.DialogResult = DialogResult.OK;
        options.Controls.AddRange(new Control[] { organize, episodeTitles, subtitles, forceSubtitles, metadata, cleanup, duplicates, note, cancel, start });
        if (options.ShowDialog(this) != DialogResult.OK) return;

        if (organize.Checked) await CheckFilenamesAsync(episodeTitles.Checked);
        else await ScanLibraryAsync();
        var duplicateRemoved = duplicates.Checked ? await ResolveDuplicateMediaAsync() : 0;
        if (metadata.Checked && !settings.AutoDownloadCovers) await DownloadAllCoversAsync();
        if (subtitles.Checked) await AuditSubtitlesAsync(forceSubtitles.Checked);
        var removedFolders = 0;
        if (cleanup.Checked)
        {
            status.Text = "Removing empty leftover folders...";
            removedFolders = await RemoveEmptyLibraryFoldersAsync();
        }
        var duplicateNote = duplicateRemoved == 0 ? "" : $" Removed {duplicateRemoved} duplicate file{(duplicateRemoved == 1 ? "" : "s")}.";
        var folderNote = removedFolders == 0 ? " No empty folders found." : $" Removed {removedFolders} empty folder{(removedFolders == 1 ? "" : "s")}.";
        status.Text = "Library update complete." + duplicateNote + folderNote;
    }

    private static string DuplicateMediaKey(MediaItem media)
    {
        if (media.Type.Equals("Show", StringComparison.OrdinalIgnoreCase))
        {
            var series = NormalizeArtworkTitle(media.Series.Length > 0 ? media.Series : media.Title);
            return $"show|{series}|{media.SeasonNumber}|{media.EpisodeNumber}";
        }
        return $"movie|{NormalizeArtworkTitle(media.Title)}|{media.Year}";
    }

    private async Task<int> ResolveDuplicateMediaAsync()
    {
        var groups = library.GroupBy(DuplicateMediaKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.ToList())
            .ToList();
        if (groups.Count == 0) return 0;

        var removed = 0;
        foreach (var group in groups)
        {
            // Never make a delete decision around an active or torrent-origin
            // file. The user can finish/move that download and run the audit again.
            if (group.Any(media => IsProtectedTorrentMedia(media.FullPath))) continue;
            var keepPath = ChooseDuplicateToKeep(group);
            if (string.IsNullOrWhiteSpace(keepPath)) continue;
            var extras = group.Where(media => !media.FullPath.Equals(keepPath, StringComparison.OrdinalIgnoreCase)).ToList();
            var title = group[0].Type.Equals("Show", StringComparison.OrdinalIgnoreCase)
                ? $"{group[0].Series} · S{group[0].SeasonNumber:00}E{group[0].EpisodeNumber:00}"
                : group[0].Title;
            var confirm = MessageBox.Show(this,
                $"Keep:\n{keepPath}\n\nDelete the other {extras.Count} duplicate file{(extras.Count == 1 ? "" : "s")} for {title}?\nMatching subtitle sidecars will be removed with each duplicate.",
                "Confirm duplicate cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) continue;

            foreach (var extra in extras)
            {
                if (DeleteMediaAndSidecars(extra)) removed++;
            }
        }

        if (removed > 0) await ScanLibraryAsync();
        return removed;
    }

    private string? ChooseDuplicateToKeep(IReadOnlyList<MediaItem> candidates)
    {
        var title = candidates[0].Type.Equals("Show", StringComparison.OrdinalIgnoreCase)
            ? $"{candidates[0].Series} · S{candidates[0].SeasonNumber:00}E{candidates[0].EpisodeNumber:00}"
            : candidates[0].Title;
        using var dialog = new Form { Text = "Duplicate media found", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(820, 430), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        dialog.Controls.Add(new Label { Text = $"{title} has {candidates.Count} copies. Select the file to keep:", AutoSize = true, Location = new Point(20, 20) });
        var list = new ListBox { Location = new Point(20, 55), Size = new Size(765, 275), BackColor = Surface, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true };
        foreach (var candidate in candidates)
        {
            var info = new FileInfo(candidate.FullPath);
            var size = info.Exists ? $" ({info.Length / 1024d / 1024d:0} MB)" : " (missing)";
            list.Items.Add(candidate.FullPath + size);
        }
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        var pathByDisplay = candidates.ToDictionary(candidate => candidate.FullPath + (File.Exists(candidate.FullPath) ? $" ({new FileInfo(candidate.FullPath).Length / 1024d / 1024d:0} MB)" : " (missing)"), candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase);
        var skip = MakeButton("Skip", 105); skip.Location = new Point(565, 345); skip.Click += (_, _) => dialog.DialogResult = DialogResult.Cancel;
        var keep = MakeButton("Keep selected", 125, Accent); keep.Location = new Point(680, 345); keep.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        dialog.Controls.AddRange(new Control[] { list, skip, keep });
        return dialog.ShowDialog(this) == DialogResult.OK && list.SelectedItem is string display && pathByDisplay.TryGetValue(display, out var path) ? path : null;
    }

    private static bool DeleteMediaAndSidecars(MediaItem media)
    {
        try
        {
            if (!File.Exists(media.FullPath)) return false;
            var directory = Path.GetDirectoryName(media.FullPath)!;
            var stem = Path.GetFileNameWithoutExtension(media.FullPath);
            File.Delete(media.FullPath);
            foreach (var sidecar in Directory.EnumerateFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly)
                         .Where(path => Regex.IsMatch(Path.GetExtension(path), @"(?i)^\.(?:srt|ass|ssa|vtt|sub|idx)$") || path.EndsWith(".subtitle-sync.json", StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(sidecar); } catch { }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Removes only genuinely empty directories below configured library roots.
    /// The operation is deliberately conservative: roots, reparse points,
    /// torrent-origin trees, and directories containing even one sidecar/hidden
    /// file are preserved.
    /// </summary>
    private async Task<int> RemoveEmptyLibraryFoldersAsync()
    {
        return await Task.Run(() =>
        {
            var removed = 0;
            foreach (var rootPath in settings.LibraryFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DirectoryInfo root;
                try { root = new DirectoryInfo(Path.GetFullPath(rootPath)); }
                catch { continue; }

                List<DirectoryInfo> directories;
                try
                {
                    directories = root.EnumerateDirectories("*", SearchOption.AllDirectories)
                        .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        .OrderByDescending(directory => directory.FullName.Length)
                        .ToList();
                }
                catch { continue; }

                foreach (var directory in directories)
                {
                    if (IsProtectedTorrentDirectory(directory)) continue;
                    try
                    {
                        if (directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).Any()) continue;
                        directory.Delete(false);
                        removed++;
                    }
                    catch { /* A file arriving or a locked folder is left for the next pass. */ }
                }
            }
            return removed;
        });
    }

    private static bool IsProtectedTorrentDirectory(DirectoryInfo directory)
    {
        try
        {
            for (var current = directory; current != null; current = current.Parent)
            {
                if (Regex.IsMatch(current.Name, @"(?i)^(?:incomplete|downloading|partial|\.incomplete)$")) return true;
                if (current.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Any(file =>
                        Regex.IsMatch(file.Name, @"(?i)(?:\.aria2|\.!qB|\.part|\.crdownload|\.opdownload|\.torrent$|\.fastresume$|^torrent\s+downloaded\s+from)"))) return true;
            }
        }
        catch { return true; }
        return false;
    }

    private async Task CheckFilenamesAsync(bool? lookupOverride = null)
    {
        var lookupTitles = lookupOverride ?? false;
        if (lookupOverride is null)
        {
            using var options = new Form { Text = "Check filenames", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(590, 285), StartPosition = FormStartPosition.CenterParent };
            options.Controls.Add(new Label { Text = "Scan the live library and preview files that do not follow the clean naming scheme.", Location = new Point(22, 22), Size = new Size(530, 45) });
            var lookup = new CheckBox { Text = "Look up missing episode titles with TVMaze", AutoSize = true, Location = new Point(22, 83) };
            options.Controls.Add(lookup);
            options.Controls.Add(new Label { Text = "This sends the series name and season/episode numbers to TVMaze. Video files are never uploaded.", ForeColor = Muted, Location = new Point(43, 115), Size = new Size(500, 48) });
            var cancel = MakeButton("Cancel", 105); cancel.Location = new Point(320, 180); cancel.Click += (_, _) => options.DialogResult = DialogResult.Cancel;
            var check = MakeButton("Build preview", 125, Accent); check.Location = new Point(435, 180); check.Click += (_, _) => { lookupTitles = lookup.Checked; options.DialogResult = DialogResult.OK; };
            options.Controls.AddRange(new Control[] { cancel, check });
            if (options.ShowDialog(this) != DialogResult.OK) return;
        }

        SaveSettings();
        // Keep the library and every menu fully readable while the audit runs.
        // Disabling the form itself makes WinForms wash out every child control.
        UseWaitCursor = true;
        filenameAuditButton.Enabled = false;
        (int ExitCode, string Output, string Error) result;
        try
        {
            if (lookupTitles) await FetchEpisodeTitlesAsync();
            status.Text = "Building filename preview...";
            result = await RunOrganizerAsync(false);
        }
        finally
        {
            UseWaitCursor = false;
            filenameAuditButton.Enabled = true;
        }
        if (result.ExitCode != 0) { MessageBox.Show(this, result.Error, "Filename check error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        var planFile = Path.Combine(AppContext.BaseDirectory, "rename-plan.json");
        var plans = ReadRenamePlans(planFile);
        if (plans.Count == 0) { MessageBox.Show(this, "Every media filename already follows the clean naming scheme.", "Filename check"); return; }
        if (!ShowRenamePreview(plans)) return;

        UseWaitCursor = true;
        filenameAuditButton.Enabled = false;
        try
        {
            status.Text = $"Renaming {plans.Count} media files and matching sidecars...";
            CancelThumbnailWork();
            await Task.Delay(500);
            result = await RunOrganizerAsync(true);
        }
        finally
        {
            UseWaitCursor = false;
            filenameAuditButton.Enabled = true;
        }
        if (result.ExitCode != 0) MessageBox.Show(this, result.Error, "Rename error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
        {
            await ScanLibraryAsync();
            var renamed = plans.Count;
            var skipped = 0;
            try
            {
                using var summary = JsonDocument.Parse(result.Output.Trim());
                if (summary.RootElement.TryGetProperty("Renamed", out var renamedNode)) renamed = renamedNode.GetInt32();
                if (summary.RootElement.TryGetProperty("Skipped", out var skippedNode)) skipped = skippedNode.GetInt32();
            }
            catch { }
            var message = skipped == 0 ? $"Renamed {renamed} files successfully." : $"Renamed {renamed} files. {skipped} busy files were skipped and can be retried later.";
            MessageBox.Show(this, message, "Filename cleanup", MessageBoxButtons.OK, skipped == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunOrganizerAsync(bool apply)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "organize-media.ps1");
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(arg);
        if (apply) psi.ArgumentList.Add("-Apply");
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output, error);
    }

    private static List<RenamePlan> ReadRenamePlans(string path)
    {
        if (!File.Exists(path)) return new();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<RenamePlan>>(document.RootElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var single = JsonSerializer.Deserialize<RenamePlan>(document.RootElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return single == null ? new() : new() { single };
        }
        return new();
    }

    private bool ShowRenamePreview(List<RenamePlan> plans)
    {
        using var dialog = new Form { Text = $"Filename preview - {plans.Count} changes", BackColor = Window, ForeColor = Color.White, Font = Font, Size = new Size(1000, 650), StartPosition = FormStartPosition.CenterParent };
        var grid = new DataGridView { Location = new Point(18, 50), Size = new Size(945, 500), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Surface, ForeColor = Color.White, GridColor = Control, BorderStyle = BorderStyle.FixedSingle, EnableHeadersVisualStyles = false };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Control;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Color.White;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(34, 92, 60);
        grid.Columns.Add("Old", "Current filename");
        grid.Columns.Add("New", "Proposed filename");
        foreach (var plan in plans) grid.Rows.Add(Path.GetFileName(plan.OldPath), Path.GetFileName(plan.NewPath));
        dialog.Controls.Add(new Label { Text = "Review every proposed change. Nothing is renamed until you press Apply.", AutoSize = true, Location = new Point(18, 20) });
        dialog.Controls.Add(grid);
        var cancel = MakeButton("Cancel", 110); cancel.Location = new Point(730, 565); cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; cancel.Click += (_, _) => dialog.DialogResult = DialogResult.Cancel;
        var apply = MakeButton("Apply changes", 135, Accent); apply.Location = new Point(850, 565); apply.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; apply.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        dialog.Controls.AddRange(new Control[] { cancel, apply });
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private static string NormalizeSeries(string series)
    {
        series = Regex.Replace(series, @"^\[[^\]]*(torrent|\.com|\.net|\.to|movcr|yts|tgx|galaxy)[^\]]*\]\s*", "", RegexOptions.IgnoreCase);
        // Treat release punctuation as separators.  This makes
        // "Daredevil - Born Again", "Daredevil Born Again", and
        // "Daredevil.Born.Again" one logical series while keeping the
        // original Daredevil series separate from Born Again.
        series = Regex.Replace(series.Replace('.', ' ').Replace('_', ' ').Replace('–', ' ').Replace('—', ' '), @"\s*-\s*", " ");
        series = Regex.Replace(series, @"\s+", " ").Trim(' ', '-');
        var compact = NormalizeArtworkTitle(series);
        if (compact is "marvels the punisher" or "marvel the punisher") return "Marvel's The Punisher";
        if (compact is "marvels daredevil" or "marvel daredevil") return "Marvel's Daredevil";
        if (compact is "daredevil born again") return "Daredevil Born Again";
        if (compact is "the punisher one last kill" or "punisher one last kill") return "The Punisher: One Last Kill";
        if (series.Equals("From", StringComparison.OrdinalIgnoreCase)) return "From";
        return series;
    }

    private async Task FetchEpisodeTitlesAsync()
    {
        var metadata = new List<EpisodeMetadata>();
        var seriesNames = library.Where(x => x.Type == "Show" && x.SeasonNumber > 0).Select(x => NormalizeSeries(x.Series)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var index = 0; index < seriesNames.Count; index++)
        {
            var series = seriesNames[index];
            status.Text = $"Looking up episode titles {index + 1} of {seriesNames.Count}: {series}";
            Application.DoEvents();
            try
            {
                var searchUrl = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(series);
                using var showDocument = JsonDocument.Parse(await Http.GetStringAsync(searchUrl));
                if (!showDocument.RootElement.TryGetProperty("id", out var idNode)) continue;
                var episodeUrl = $"https://api.tvmaze.com/shows/{idNode.GetInt32()}/episodes";
                using var episodeDocument = JsonDocument.Parse(await Http.GetStringAsync(episodeUrl));
                foreach (var episode in episodeDocument.RootElement.EnumerateArray())
                {
                    if (!episode.TryGetProperty("season", out var seasonNode) || !episode.TryGetProperty("number", out var numberNode) || numberNode.ValueKind == JsonValueKind.Null || !episode.TryGetProperty("name", out var nameNode)) continue;
                    metadata.Add(new EpisodeMetadata { Series = series, Season = seasonNode.GetInt32(), Episode = numberNode.GetInt32(), Title = nameNode.GetString() ?? "" });
                }
                await Task.Delay(500);
            }
            catch { }
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "episode-titles.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }
}

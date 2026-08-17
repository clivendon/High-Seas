using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace HighSeasRemote;

[Activity(Label = "High Seas Remote", MainLauncher = true, Exported = true, Theme = "@style/AppTheme")]
public sealed class MainActivity : Activity
{
    private const string DefaultAddress = "http://10.0.0.128:8765";
    private WebView? browser;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Rgb(9, 18, 15));
        ShowRemote();
    }

    private void ShowRemote()
    {
        var frame = new FrameLayout(this);
        frame.SetBackgroundColor(Color.Rgb(9, 18, 15));
        browser = new WebView(this);
        browser.Settings.JavaScriptEnabled = true;
        browser.Settings.DomStorageEnabled = true;
        browser.Settings.CacheMode = CacheModes.Default;
        browser.Settings.BuiltInZoomControls = false;
        browser.SetWebViewClient(new RemoteWebViewClient(() => ShowAddressDialog()));
        frame.AddView(browser, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var settingsButton = new TextView(this)
        {
            Text = "\u2699",
            TextSize = 22,
            Gravity = GravityFlags.Center,
            ContentDescription = "Change Media Center address"
        };
        settingsButton.SetTextColor(Color.White);
        settingsButton.Click += (_, _) => ShowAddressDialog();
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        var buttonBackground = new GradientDrawable();
        buttonBackground.SetColor(Color.Argb(238, 18, 54, 38));
        buttonBackground.SetStroke((int)(2 * density), Color.Rgb(45, 190, 105));
        buttonBackground.SetCornerRadius(14 * density);
        settingsButton.Background = buttonBackground;
        var size = (int)(46 * density);
        var margin = (int)(10 * density);
        var buttonLayout = new FrameLayout.LayoutParams(size, size, GravityFlags.Top | GravityFlags.Right);
        buttonLayout.SetMargins(margin, margin, margin, margin);
        frame.AddView(settingsButton, buttonLayout);
        SetContentView(frame);
        LoadSavedAddress();
    }

    private void LoadSavedAddress()
    {
        var preferences = GetSharedPreferences("clive_remote", FileCreationMode.Private);
        var address = preferences?.GetString("address", DefaultAddress) ?? DefaultAddress;
        browser?.LoadUrl(NormalizeAddress(address));
    }

    private void ShowAddressDialog()
    {
        var preferences = GetSharedPreferences("clive_remote", FileCreationMode.Private);
        var input = new EditText(this)
        {
            Text = preferences?.GetString("address", DefaultAddress) ?? DefaultAddress,
            Hint = "http://10.0.0.128:8765",
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri
        };
        input.SetSingleLine(true);
        var padding = (int)(18 * (Resources?.DisplayMetrics?.Density ?? 1f));
        var holder = new FrameLayout(this);
        holder.SetPadding(padding, 0, padding, 0);
        holder.AddView(input);
        var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("High Seas Media address");
        dialog.SetMessage("Use the address shown by High Seas Remote on your PC.");
        dialog.SetView(holder);
        dialog.SetNegativeButton("Cancel", (_, _) => { });
        dialog.SetPositiveButton("Connect", (_, _) =>
        {
            var address = NormalizeAddress(input.Text ?? DefaultAddress);
            preferences?.Edit()?.PutString("address", address)?.Apply();
            browser?.LoadUrl(address);
        });
        dialog.Show();
    }

    private static string NormalizeAddress(string value)
    {
        value = value.Trim().TrimEnd('/');
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
        return value;
    }

    public override void OnBackPressed()
    {
        if (browser?.CanGoBack() == true) browser.GoBack();
        else base.OnBackPressed();
    }

    private sealed class RemoteWebViewClient(Action showSetup) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var target = request?.Url?.ToString() ?? "";
            if (!target.EndsWith("/install", StringComparison.OrdinalIgnoreCase)) return false;

            // Open the installer in the phone's browser so Android can download and update the APK,
            // while this WebView keeps the connected remote ready in the background.
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(target));
            intent.AddFlags(ActivityFlags.NewTask);
            view?.Context?.StartActivity(intent);
            return true;
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            base.OnReceivedError(view, request, error);
            if (request?.IsForMainFrame == true) showSetup();
        }
    }
}

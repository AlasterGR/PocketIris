using Android.Webkit;

namespace PocketIris;

/// <summary>
/// The PocketIris page: PocketIris's own screen (safety, appearance, sessions and trash, developer tools), shown in a web view
/// that loads the page from the app's own files. It is the only web view that gets the native bridge (window.PocketIris), and it can
/// never leave its own page, so nothing from the internet or from a chat can reach the bridge.
/// </summary>
public sealed class PocketIrisPage
{
    /// <summary>The address the page pretends to have; nothing is ever fetched from it.</summary>
    const string BaseUrl = "http://pocketiris.local/";

    readonly MainActivity activity;

    /// <summary>The web view with the page itself.</summary>
    public WebView View { get; }

    /// <summary>The whole tab to place in the screen layout: the shared header row (the same as Browser's and Terminal's), then the page.</summary>
    public Android.Widget.LinearLayout Pane { get; }

    /// <summary>The header's text, kept so the text size setting can reach it.</summary>
    readonly Android.Widget.TextView title;

    /// <summary>Builds the tab: its header, then the web view with the page.</summary>
    /// <param name="owner">The activity that owns the screens.</param>
    public PocketIrisPage(MainActivity owner)
    {
        activity = owner;
        Pane = new Android.Widget.LinearLayout(activity) { Orientation = Android.Widget.Orientation.Vertical, Visibility = Android.Views.ViewStates.Invisible };
        Pane.SetBackgroundColor(Tokens.Sys.Background);
        var header = PaneHeader.Create(activity);
        title = new Android.Widget.TextView(activity) { Text = "App settings and info.", TextSize = Tokens.Text.Lg * Appearance.TextScale, Gravity = Android.Views.GravityFlags.CenterVertical };
        title.SetTextColor(Tokens.Sys.Text);
        title.SetPadding((int)(Tokens.Space.Sm * activity.Resources!.DisplayMetrics!.Density), 0, 0, 0);
        header.AddView(title, PaneHeader.ChildParams(0, 1f));
        Pane.AddView(header, PaneHeader.Params(activity));
        View = new WebView(activity);
        View.SetBackgroundColor(Tokens.Sys.Background);
        View.Settings.JavaScriptEnabled = true;
        View.Settings.DomStorageEnabled = false;
        View.Settings.AllowFileAccess = false;
        View.Settings.AllowContentAccess = false;
        View.SetWebViewClient(new LockedClient());
        View.AddJavascriptInterface(new PageBridge(activity, View), "PocketIris");
        Pane.AddView(View, new Android.Widget.LinearLayout.LayoutParams(-1, 0, 1f));
        Load();
    }

    /// <summary>Reads the page files from the app, puts the design tokens and the files together, and loads the result.</summary>
    public void Load()
    {
        //  Reads one of the page's files from the app package.
        string Asset(string name)
        {
            using var stream = activity.Assets!.Open("pocketiris/" + name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        var html = Asset("index.html")
            .Replace("<!--TOKENS-->", "<style>" + Tokens.CssVariables() + "</style>")
            .Replace("<!--CSS-->", "<style>" + Asset("app.css") + "</style>")
            .Replace("<!--JS-->", "<script>" + Asset("app.js") + "</script>");
        View.LoadDataWithBaseURL(BaseUrl, html, "text/html", "utf-8", null);
    }

    /// <summary>Tells the page that appearance values changed, so it redraws.</summary>
    public void NotifyAppearance() => activity.RunOnUiThread(() =>
    {
        title.TextSize = Tokens.Text.Lg * Appearance.TextScale;
        View.EvaluateJavascript("window.__pcAppearance&&window.__pcAppearance()", null);
    });

    /// <summary>Tells the page it is on screen again, so it reloads its lists.</summary>
    public void NotifyShown() => activity.RunOnUiThread(() => View.EvaluateJavascript("window.__pcShown&&window.__pcShown()", null));

    /// <summary>Refuses every navigation: the page may never be replaced by another address.</summary>
    sealed class LockedClient : WebViewClient
    {
        /// <summary>Always says "handled", so no link or script can load anything else here.</summary>
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => true;
    }
}

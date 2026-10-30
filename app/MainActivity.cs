using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Runtime;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace PocketIris;

/// <summary>
/// PocketIris: starts the Claude server inside Termux (without the user opening Termux)
/// and shows its web UI full screen, plus the Browser, Terminal and Scripts views next to it.
/// </summary>
[Activity(Name = "io.github.alastergr.pocketiris.MainActivity", Label = "@string/app_name", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden |
                           ConfigChanges.Keyboard | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.UiMode | ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    const string ServerUrl = "http://127.0.0.1:3400/";
    const int RequestFile = 12, RequestMic = 13;
    public const int RequestCamera = 14, RequestLocation = 15;
    public static MainActivity? Current { get; private set; }
    public static TaskCompletionSource<Bitmap?>? CameraResult;
    public static TaskCompletionSource<bool>? LocationResult;

    /// <summary>The app-level switch for phone control (reading and tapping other apps); off until the user turns it on.</summary>
    public static bool PhoneControlEnabled;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    static Color Bg => Tokens.Sys.Background;
    static Color Fg => Tokens.Sys.Text;
    static Color Dim => Tokens.Sys.TextDim;
    static Color Accent => Tokens.Sys.Accent;

    WebView web = null!;
    LinearLayout overlay = null!;
    TextView status = null!, detail = null!;
    ProgressBar spinner = null!;
    Button retry = null!;
    IValueCallback? fileCallback;
    PermissionRequest? pendingWebPermission;
    CancellationTokenSource? startCts;

    BrowserHost browser = null!;
    McpServer mcp = null!;
    LinearLayout browserPane = null!;
    WebView? browserWeb;
    int termFontPercent = Appearance.TextZoomPercent;
    ToolPanes tools = null!;
    LinearLayout outer = null!;
    NavBar navBar = null!;
    PocketIrisPage pcPage = null!;
    bool keyboardOpen;
    CancellationTokenSource? dotLoop;
    TermuxRunner termux = null!;
    View browserBack = null!;
    EditText addressBar = null!;
    string? pendingPath;

    /// <summary>True while the app is on screen.</summary>
    public bool IsForeground { get; private set; }

    /// <summary>Builds the whole screen: chat page, start-up overlay, Browser, Terminal and Scripts views; then starts the server.</summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        termux = new TermuxRunner(this);
        Approval.FingerprintAllows = Prefs.GetBoolean("fingerprintAllows", true);
        PhoneControlEnabled = Prefs.GetBoolean("phoneControl", false);
        BluetoothDebug.RestoreAfterRestart(this);
        Window!.SetStatusBarColor(Bg);
        Window.SetNavigationBarColor(Bg);

        var root = new FrameLayout(this);
        root.SetBackgroundColor(Bg);

        //  The chat page: a web view that stays hidden until the server answers.
        WebView.SetWebContentsDebuggingEnabled(GetSharedPreferences("ca", FileCreationMode.Private)!.GetBoolean("debug", false));
        web = new WebView(this) { Visibility = ViewStates.Invisible };
        web.SetBackgroundColor(Bg);
        var s = web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        s.MediaPlaybackRequiresUserGesture = false;
        s.AllowFileAccess = false;
        s.SetSupportMultipleWindows(false);
        web.SetWebViewClient(new Client(this));
        web.SetWebChromeClient(new Chrome(this));
        //  The chat page can show text from the internet, so it gets only the small, reversible "move to trash" bridge.
        web.AddJavascriptInterface(new TrashShortcut(this, web), "PocketTrash");
        root.AddView(web, new FrameLayout.LayoutParams(-1, -1));

        //  The start-up overlay with the status text, spinner and retry button.
        overlay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        overlay.SetGravity(GravityFlags.Center);
        overlay.SetPadding(Dp(Tokens.Space.X4l), Dp(Tokens.Space.X4l), Dp(Tokens.Space.X4l), Dp(Tokens.Space.X4l));
        overlay.SetBackgroundColor(Bg);
        overlay.AddView(Label("PocketIris", Tokens.Text.X2l, Fg, bold: true));
        status = Label("Starting…", Tokens.Text.Lg, Fg);
        status.SetPadding(0, Dp(Tokens.Space.Xl), 0, 0);
        overlay.AddView(status);
        detail = Label("", Tokens.Text.Sm, Dim);
        detail.SetPadding(0, Dp(Tokens.Space.Md), 0, Dp(Tokens.Space.Xl));
        overlay.AddView(detail);
        spinner = new ProgressBar(this) { Indeterminate = true };
        spinner.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
        overlay.AddView(spinner);
        retry = new Button(this) { Text = "Try again", Visibility = ViewStates.Gone };
        retry.Click += (_, _) => Start();
        overlay.AddView(retry);
        root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));

        //  The custom views sit on top of the chat page and stay hidden until asked for.
        BuildBrowserPane(root);
        tools = new ToolPanes(this, root, termux.TermToken(), ShowView);
        tools.BeforeTerminal = termux.EnsureHelpers;

        //  PocketIris's own page (settings, sessions and trash) sits on top of the other views, hidden until asked for.
        pcPage = new PocketIrisPage(this);
        root.AddView(pcPage.Pane, new FrameLayout.LayoutParams(-1, -1));

        //  The one navigation bar of the app goes around the content, at the top or the bottom as the Appearance setting says.
        navBar = new NavBar(this, ShowView);
        outer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        outer.SetBackgroundColor(Bg);
        outer.AddView(root, new LinearLayout.LayoutParams(-1, 0, 1f));
        ApplyNavPosition();
        Appearance.Changed += OnAppearanceChanged;
        ApplyTextZoom();

        //  Tell the appearance how wide the content is, so Auto can choose phone, tablet or desktop.
        root.LayoutChange += (_, _) => Appearance.SetViewWidth((int)(root.Width / Resources!.DisplayMetrics!.Density));

        FitToSystemBars(outer);
        SetContentView(outer);
        AddShortcuts();
        mcp.Start();

        //  The keep-alive service is optional: without it the tools only work while the app is open.
        try { StartForegroundService(new Intent(this, typeof(KeepAliveService))); } catch (Exception swallowed) { Diag.Swallowed("MainActivity:125", swallowed); }
        var startPath = Intent?.GetStringExtra("path");
        //  Any app can open PocketIris with a page to show, so only a path that stays on the chat server is kept.
        pendingPath = ChatAddress(startPath) != null ? startPath : null;
        var cmd = Intent?.GetStringExtra("cmd") ?? ActionToCommand(Intent?.Action);

        //  Run the command the app was opened with, or start the server as usual.
        if (cmd != null) HandleCommand(cmd, FromControlEntry(Intent)); else Start();
        //  A command that only opens a view (a launcher shortcut such as Terminal, or one from the PC) must still load the chat behind it on a fresh start.
        if (cmd is "reload" or "browser" or "terminal" or "scripts" or "pocketiris" or "chat" or "debug-on" or "debug-off" or "run") Start();
        termux.EnsureHelpers();

        //  Compare the installed versions with the known-good set once the helpers have had time to record them and the server is up.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(45));
            //  A failed check only means no banner this time; it is tried again when the PocketIris tab opens.
            try { await VersionCheck.Run(); } catch (Exception swallowed) { Diag.Swallowed("MainActivity.VersionCheck", swallowed); }
            //  The Termux apps are compared with their GitHub releases once a month (a notice only).
            try { await TermuxApps.CheckIfDue(this); } catch (Exception swallowed) { Diag.Swallowed("MainActivity.TermuxApps", swallowed); }
        });
    }

    /// <summary>
    /// True when the intent came in through the control entry point (<c>ControlActivity</c>), which Android only lets the adb shell use
    /// (it is protected by the DUMP permission that ordinary apps can never hold). Commands that run code or change safety
    /// settings are accepted only from there, so no other app on the phone can trigger them.
    /// </summary>
    static bool FromControlEntry(Intent? intent) =>
        intent?.Component?.ClassName?.EndsWith(".ControlActivity", StringComparison.Ordinal) == true;

    string? shortcutScript;

    /// <summary>The script that adds "Move to trash" to the chat page: its style and its code, read from the app's files once.</summary>
    internal string ShortcutScript()
    {
        //  Build it the first time and reuse it afterwards.
        if (shortcutScript != null) return shortcutScript;
        string Asset(string name)
        {
            using var stream = Assets!.Open("yepshortcut/" + name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        return shortcutScript =
            "(function(){var s=document.createElement('style');s.textContent=" + PageBridge.Quote(Asset("shortcut.css")) + ";document.head.appendChild(s);})();" + Asset("shortcut.js");
    }

    /// <summary>True when the address is exactly the chat server's (same scheme, host and port, and no user name hidden in front of the host).</summary>
    /// <param name="url">The address to check.</param>
    static bool IsServerPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && u.Host == "127.0.0.1" && u.Port == 3400 && string.IsNullOrEmpty(u.UserInfo);

    /// <summary>
    /// Turns a path from a notification or another app (for example "/projects/a/sessions/b") into the chat server's full address,
    /// or returns null when the result would not be exactly the chat server (a path such as "@evil.example/" would change the host).
    /// </summary>
    /// <param name="path">The path to open.</param>
    static string? ChatAddress(string? path)
    {
        //  Only an absolute path can be added safely after the server's address.
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return null;
        var url = ServerUrl.TrimEnd('/') + path;
        return IsServerPage(url) ? url : null;
    }

    // ---------- the views ----------

    /// <summary>Shows one view and hides the others. "Chat" means none of the custom views, so the web UI shows through.</summary>
    /// <param name="name">One of <see cref="NavBar.Views"/>, or Scripts.</param>
    public void ShowView(string name)
    {
        //  Hidden tabs stay in the layout (Invisible, not Gone), so they keep their real size; a web view resized while Gone came back believing it had no height.
        browserPane.Visibility = ViewStates.Invisible;
        tools.HideAll();
        pcPage.Pane.Visibility = ViewStates.Invisible;
        navBar.Select(name);

        //  Open the view that was asked for; any other name (Chat) leaves everything hidden.
        switch (name)
        {
            case "Browser":
                browserPane.Visibility = ViewStates.Visible;
                browser.Web.OnResume();
                break;
            case "Terminal":
                tools.ShowTerminal(null);
                break;
            case "Scripts":
                tools.ShowScripts();
                break;
            case "PocketIris":
                pcPage.Pane.Visibility = ViewStates.Visible;
                //  Look at the versions again in the background (cheap), so a change shows the next time the page draws.
                _ = Task.Run(async () => { try { await VersionCheck.Run(); } catch (Exception swallowed) { Diag.Swallowed("ShowView.VersionCheck", swallowed); } });
                pcPage.NotifyShown();
                break;
        }
    }

    /// <summary>Opens the Browser view.</summary>
    public void ShowBrowserPane() => ShowView("Browser");

    /// <summary>Builds the Browser view: tab row, address bar with back and reload, then the page.</summary>
    void BuildBrowserPane(FrameLayout root)
    {
        var bw = new WebView(this);
        browserWeb = bw;
        browser = new BrowserHost(this, bw);
        mcp = McpServer.Instance;
        mcp.Attach(browser);

        browserPane = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Invisible };
        browserPane.SetBackgroundColor(Bg);
        var bar = PaneHeader.Create(this);
        browserBack = PaneHeader.AccentButton(this, "‹", "Back", narrow: true);
        browserBack.Click += (_, _) =>
        {
            //  Go back one page, or say there is nowhere to go back to.
            if (bw.CanGoBack()) bw.GoBack();
            else Toast.MakeText(this, "No earlier page", ToastLength.Short)!.Show();
        };
        browserBack.Alpha = 0.4f;
        bar.AddView(browserBack);

        //  The address bar shows the current address; typing a web address or search words and pressing Go opens it.
        addressBar = new EditText(this);
        addressBar.SetSingleLine(true);
        addressBar.SetTextColor(Fg);
        addressBar.SetHintTextColor(Dim);
        addressBar.Hint = "Search or type an address";
        addressBar.SetTextSize(Android.Util.ComplexUnitType.Sp, 14 * Appearance.TextScale);
        addressBar.SetSelectAllOnFocus(true);
        addressBar.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
        addressBar.ImeOptions = Android.Views.InputMethods.ImeAction.Go;
        addressBar.SetPadding(Dp(Tokens.Space.Lg), 0, Dp(Tokens.Space.Lg), 0);
        //  Android gives text fields a minimum height; here the header decides it, so the field can be as slim as one line of text plus its small gaps.
        addressBar.SetMinHeight(0); addressBar.SetMinimumHeight(0);
        var field = new Android.Graphics.Drawables.GradientDrawable();
        field.SetColor(Tokens.Sys.Surface);
        field.SetCornerRadius(Dp(Tokens.Radius.Sm));
        addressBar.Background = field;
        addressBar.EditorAction += (_, e) =>
        {
            //  Pressing Go or Enter opens what was typed and hides the keyboard.
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Go || e.Event?.KeyCode == Keycode.Enter)
            {
                GoTo(addressBar.Text ?? "");
                addressBar.ClearFocus();
                ((Android.Views.InputMethods.InputMethodManager?)GetSystemService(InputMethodService))?.HideSoftInputFromWindow(addressBar.WindowToken, 0);
                e.Handled = true;
            }
        };
        var lp = PaneHeader.ChildParams(0, 1f);
        lp.SetMargins(Dp(Tokens.Space.Sm), 0, Dp(Tokens.Space.Sm), 0);
        bar.AddView(addressBar, lp);

        //  Reload: a tap reloads the page; a long press offers to clear this site's cookies and stored data and the cache, then reload fresh.
        var reload = PaneHeader.AccentButton(this, "↻", "Reload (long press: clear this site and reload)");
        reload.Click += (_, _) => bw.Reload();
        reload.LongClick += (_, _) => _ = ClearSiteAndReload(bw);
        bar.AddView(reload);
        browserPane.AddView(bar, PaneHeader.Params(this));
        browserPane.AddView(bw, new LinearLayout.LayoutParams(-1, 0, 1f));
        root.AddView(browserPane, new FrameLayout.LayoutParams(-1, -1));

        //  Keep the address bar and the back button in step with the page.
        browser.Changed += () => RunOnUiThread(() =>
        {
            //  Do not overwrite the address while the user is typing in it.
            if (!addressBar.HasFocus) addressBar.Text = DisplayAddress(browser.Web.Url ?? browser.CurrentUrl);
            browserBack.Alpha = browser.Web.CanGoBack() ? 1f : 0.4f;
        });
    }

    /// <summary>
    /// The reload button's long press: after a question (it signs the user out of that site), deletes the current site's cookies and stored
    /// data (localStorage and the like) and the web cache, then reloads the page fresh. Other sites, and the chat's own login, are left alone.
    /// </summary>
    async Task ClearSiteAndReload(WebView bw)
    {
        var url = bw.Url ?? "";
        //  Only an ordinary web page has site data to clear.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { Toast.MakeText(this, "No web page to clear", ToastLength.Short)!.Show(); return; }
        var answer = await Approval.Ask(this, $"Clear {uri.Host} and reload?",
            $"Deletes this site's cookies and stored data and the browser's cache, then loads the page fresh. You will be signed out of {uri.Host}; other sites stay as they are.",
            allowLabel: "Clear and reload", allowDelegate: false, fingerprintAllows: false);
        //  Anything but a clear yes leaves everything as it was.
        if (answer != Decision.AllowOnce) return;
        RunOnUiThread(() =>
        {
            var cookies = CookieManager.Instance!;
            var names = (cookies.GetCookie(url) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0].Trim()).Where(n => n.Length > 0).Distinct().ToList();
            var parts = uri.Host.Split('.');
            //  A cookie can belong to the host or to any parent domain (www.example.com, .example.com), so expire each name for all of them.
            foreach (var name in names)
            {
                cookies.SetCookie(url, $"{name}=; Max-Age=0; Path=/");
                for (var i = 0; i < parts.Length - 1; i++) cookies.SetCookie(url, $"{name}=; Max-Age=0; Path=/; Domain=.{string.Join('.', parts[i..])}");
            }
            cookies.Flush();
            WebStorage.Instance!.DeleteOrigin($"{uri.Scheme}://{uri.Authority}");
            bw.ClearCache(true);
            bw.Reload();
            Toast.MakeText(this, $"Cleared {uri.Host} ({names.Count} cookie{(names.Count == 1 ? "" : "s")}, stored data, cache); reloaded", ToastLength.Long)!.Show();
        });
    }

    /// <summary>https://www.example.com/a?b becomes www.example.com/a?b (the scheme is only noise in a narrow bar).</summary>
    static string DisplayAddress(string url) =>
        url.StartsWith("https://") ? url[8..] : url.StartsWith("http://") ? "http://" + url[7..] : url;

    /// <summary>The user typed in the address bar: open a site, or search for the words.</summary>
    void GoTo(string text)
    {
        text = text.Trim();

        //  Ignore an empty address bar.
        if (text.Length == 0) return;
        string target;
        bool looksLikeAddress = !text.Contains(' ') && (text.Contains("://") || text.Contains('.') || text.Contains(':'));

        //  Treat it as a web address when it looks like one, otherwise search for the words.
        if (looksLikeAddress) target = text.Contains("://") ? text : "https://" + text;
        else target = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);

        //  Refuse anything that is not an ordinary web address.
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Toast.MakeText(this, "Only http and https addresses can be opened", ToastLength.Short)!.Show();
            return;
        }

        //  Refuse addresses on this phone or the local network.
        if (BrowserHost.IsBlockedHost(uri.Host))
        {
            Toast.MakeText(this, "Blocked: local and private addresses are not opened", ToastLength.Long)!.Show();
            return;
        }
        browser.UserOpened(uri.Host);
        browser.Web.LoadUrl(uri.ToString());
    }

    /// <summary>Opens one fixed public page in the Browser tab on the user's behalf (for example the Termux releases page from the Updates card).</summary>
    /// <param name="url">An https address chosen by the app, never by a page.</param>
    internal void OpenInBrowser(string url)
    {
        var uri = new Uri(url);
        browser.UserOpened(uri.Host);
        browser.Web.LoadUrl(url);
        ShowBrowserPane();
    }

    /// <summary>Runs commands in Termux for the PocketIris page's Updates card.</summary>
    internal TermuxRunner Termux => termux;

    /// <summary>
    /// Android 15 draws apps edge to edge. Pads this view by the status bar, the navigation bar (or gesture area),
    /// the camera cutout and the keyboard, so its content never sits under them.
    /// </summary>
    void FitToSystemBars(View view)
    {
        view.SetOnApplyWindowInsetsListener(new InsetsListener(this));
        view.RequestApplyInsets();
    }

    /// <summary>The keyboard opened or closed: a bottom navigation bar steps out of the way while it is open.</summary>
    /// <param name="open">True while the keyboard is showing.</param>
    void SetKeyboardOpen(bool open)
    {
        //  Only a real change needs the bar to move.
        if (open == keyboardOpen) return;
        keyboardOpen = open;
        navBar.View.Visibility = Appearance.Nav == "bottom" && keyboardOpen ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>Puts the navigation bar at the top or the bottom of the screen, as chosen in the Appearance setting.</summary>
    void ApplyNavPosition()
    {
        var bar = navBar.View;
        (bar.Parent as ViewGroup)?.RemoveView(bar);
        var lp = new LinearLayout.LayoutParams(-1, -2);

        //  Bottom: after the content; top: before it.
        if (Appearance.Nav == "bottom") outer.AddView(bar, lp); else outer.AddView(bar, 0, lp);
        bar.Visibility = Appearance.Nav == "bottom" && keyboardOpen ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>An appearance value changed: move the bar if needed and tell the PocketIris page.</summary>
    void OnAppearanceChanged() => RunOnUiThread(() => { ApplyNavPosition(); ApplyTextZoom(); pcPage.NotifyAppearance(); });

    /// <summary>Applies the chosen text size everywhere: the chat (the web view's text zoom), the Browser (whole-page zoom), the terminal, the tab row and the tab headers.</summary>
    void ApplyTextZoom()
    {
        web.Settings.TextZoom = Appearance.TextZoomPercent;
        //  The Browser view exists only after it was built; it zooms whole pages instead of only their letters, so its text zoom stays at 100%.
        if (browserWeb != null) { browserWeb.Settings.TextZoom = 100; browser.ApplyPageZoom(); }
        navBar.ApplyTextScale();
        PaneHeader.ApplyTextScale(this);

        //  The terminal server draws its own letters, so a new size means restarting it with the new font and loading its page again.
        if (Appearance.TextZoomPercent != termFontPercent)
        {
            termFontPercent = Appearance.TextZoomPercent;
            _ = Task.Run(async () =>
            {
                termux.Run($"{termux.Env()} {TermuxRunner.Home}/bin/ca-term");
                await Task.Delay(3000);
                RunOnUiThread(() => tools.ReloadTerminal());
            });
        }
        addressBar?.SetTextSize(Android.Util.ComplexUnitType.Sp, 14 * Appearance.TextScale);
        tools.SetTextZoom(Appearance.TextZoomPercent);
    }

    /// <summary>Keeps the attention dot on the PocketIris tab up to date while the app is on screen.</summary>
    void StartDotLoop()
    {
        dotLoop?.Cancel();
        dotLoop = new CancellationTokenSource();
        var ct = dotLoop.Token;
        _ = Task.Run(async () =>
        {
            //  Refresh the dot every two seconds until the app leaves the screen.
            while (!ct.IsCancellationRequested)
            {
                RunOnUiThread(navBar.RefreshDot);
                //  Leaving the screen cancels the wait, which ends the loop.
                try { await Task.Delay(2000, ct); } catch (TaskCanceledException) { break; }
            }
        });
    }

    /// <summary>Turns the system bar sizes into padding and then marks them as used, so the pages inside do not pad for them again.</summary>
    sealed class InsetsListener(MainActivity owner) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        /// <summary>Applies the padding when Android reports the bar sizes.</summary>
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            //  Newer phones report the bars and the keyboard separately; older ones give one set of numbers.
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                var ime = insets.GetInsets(WindowInsets.Type.Ime());
                v.SetPadding(bars.Left, bars.Top, bars.Right, Math.Max(bars.Bottom, ime.Bottom));
                owner.SetKeyboardOpen(ime.Bottom > 0);
            }
            else
            {
#pragma warning disable CA1422
                v.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
            }
            return OperatingSystem.IsAndroidVersionAtLeast(30) ? WindowInsets.Consumed : insets.ConsumeSystemWindowInsets();
        }
    }

    internal ISharedPreferences Prefs => GetSharedPreferences("ca", FileCreationMode.Private)!;

    /// <summary>True if the user chose "Always allow" for this site earlier.</summary>
    public bool IsHostAlwaysAllowed(string host) => Prefs.GetStringSet("allowedHosts", new HashSet<string>())!.Contains(host.ToLowerInvariant());

    /// <summary>Remembers that the user always allows this site.</summary>
    public void AllowHostAlways(string host)
    {
        var set = new HashSet<string>(Prefs.GetStringSet("allowedHosts", new HashSet<string>())!) { host.ToLowerInvariant() };
        Prefs.Edit()!.PutStringSet("allowedHosts", set)!.Apply();
    }

    /// <summary>Claude wants the browser but the app is in the background: tell the user.</summary>
    public void NotifyBrowserNeeded() => AppNotifier.BrowserNeeded();

    /// <summary>The server keeps running (foreground service) and tells the user to reopen the app.</summary>
    protected override void OnDestroy()
    {
        mcp.Detach(browser);
        base.OnDestroy();
    }

    /// <summary>The app was opened again while running: a notification, a shortcut or the PC asked for something.</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;

        //  A request to show the browser wins over everything else.
        if (intent?.GetBooleanExtra("show-browser", false) == true) { ShowBrowserPane(); return; }

        //  The "End" button of the trusted-session notification: ending a session is always safe, so no check is needed.
        if (intent?.GetBooleanExtra("end-trust", false) == true) { TrustedSession.End(); return; }
        var path = intent?.GetStringExtra("path");

        //  Open the page a notification pointed at, otherwise run the requested command.
        if (path != null) OpenPath(path);
        else HandleCommand(intent?.GetStringExtra("cmd") ?? ActionToCommand(intent?.Action), FromControlEntry(intent));
    }

    /// <summary>A notification asked to open a page (for example a session) inside the app.</summary>
    void OpenPath(string path)
    {
        var url = ChatAddress(path);
        //  Only paths that stay on the chat server are accepted.
        if (url == null) return;
        pendingPath = path;
        _ = Task.Run(async () =>
        {
            //  Open it now if the server is up; otherwise start the server, which opens it afterwards.
            if (await ServerUp()) RunOnUiThread(() => { web.LoadUrl(url); pendingPath = null; });
            else RunOnUiThread(Start);
        });
    }

    /// <summary>The page to open once the server is up: the pending notification target, or the home page.</summary>
    string TargetUrl() => pendingPath != null ? ServerUrl.TrimEnd('/') + pendingPath : ServerUrl;

    /// <summary>Translates a launcher-shortcut action into the matching command name.</summary>
    static string? ActionToCommand(string? action) => action switch
    {
        "io.github.alastergr.pocketiris.UPDATE" => "update",
        "io.github.alastergr.pocketiris.RESTART" => "restart",
        "io.github.alastergr.pocketiris.BROWSER" => "browser",
        "io.github.alastergr.pocketiris.TERMINAL" => "terminal",
        "io.github.alastergr.pocketiris.SCRIPTS" => "scripts",
        _ => null,
    };

    /// <summary>Adds the long-press menu on the launcher icon.</summary>
    void AddShortcuts()
    {
        //  Shortcuts are only a convenience, so any failure is ignored.
        try
        {
            var sm = (ShortcutManager?)GetSystemService(ShortcutService);
            //  Without the shortcut manager there are no launcher shortcuts.
            if (sm == null) return;

            //  Makes one shortcut entry that opens the app with the given action.
            ShortcutInfo Make(string id, string label, string action) =>
                new ShortcutInfo.Builder(this, id)
                    .SetShortLabel(label)!
                    .SetIcon(Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Mipmap.appicon)!)!
                    .SetIntent(new Intent(action).SetClass(this, typeof(MainActivity)))!
                    .Build()!;
            sm.SetDynamicShortcuts(new List<ShortcutInfo>
            {
                Make("browser", "Browser", "io.github.alastergr.pocketiris.BROWSER"),
                Make("terminal", "Terminal", "io.github.alastergr.pocketiris.TERMINAL"),
                Make("scripts", "Scripts", "io.github.alastergr.pocketiris.SCRIPTS"),
                Make("update", "Update", "io.github.alastergr.pocketiris.UPDATE"),
            });
        }
        catch (Exception swallowed) { Diag.Swallowed("MainActivity:437", swallowed); }
    }

    /// <summary>Converts device-independent pixels to real pixels.</summary>
    int Dp(int v) => (int)(v * Resources!.DisplayMetrics!.Density);

    /// <summary>A centred text label.</summary>
    TextView Label(string text, float sp, Color color, bool bold = false)
    {
        var t = new TextView(this) { Text = text, TextSize = sp, Gravity = GravityFlags.Center };
        t.SetTextColor(color);

        //  Make the text bold when asked.
        if (bold) t.SetTypeface(null, TypefaceStyle.Bold);
        return t;
    }

    /// <summary>Shows the start-up overlay with a title, a detail line, and optionally a spinner or a retry button.</summary>
    internal void Show(string title, string info = "", bool busy = true, bool canRetry = false)
    {
        RunOnUiThread(() =>
        {
            overlay.Visibility = ViewStates.Visible;
            status.Text = title;
            detail.Text = info;
            spinner.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
            retry.Visibility = canRetry ? ViewStates.Visible : ViewStates.Gone;
        });
    }

    /// <summary>True if the chat server answers.</summary>
    internal static async Task<bool> ServerUp()
    {
        //  Any failure to connect simply means the server is not up.
        try { using var r = await Http.GetAsync(ServerUrl); return (int)r.StatusCode < 500; }
        catch { return false; }
    }

    /// <summary>Makes sure the server is up (asking Termux to start it if not), then shows the UI.</summary>
    void Start()
    {
        startCts?.Cancel();
        startCts = new CancellationTokenSource();
        var ct = startCts.Token;
        Show("Starting…", "Checking the Claude server");
        _ = Task.Run(async () =>
        {
            //  Any unexpected problem is shown on the overlay, and a cancelled start is silently dropped.
            try
            {
                //  Ask Termux to start the server when it is not already running.
                if (!await ServerUp())
                {
                    //  Stop here when Termux could not be asked to start the server (the overlay explains why).
                    if (!termux.StartServer()) return;
                    Show("Starting the server…", "Termux is launching it in the background (about 10 s)");
                    var deadline = DateTime.UtcNow.AddSeconds(75);

                    //  Wait up to 75 seconds for the server to answer.
                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(1000, ct);
                        //  Stop waiting as soon as the server answers.
                        if (await ServerUp()) break;
                    }

                    //  Give up with an explanation if the server never came up.
                    if (!await ServerUp())
                    {
                        Show("The server did not start",
                            "Open Termux and run  ca-logs  to see why. If Termux refused the request, run  " +
                            "bash /sdcard/Documents/ca/install.sh  once.", busy: false, canRetry: true);
                        return;
                    }
                }
                RunOnUiThread(() => { web.LoadUrl(TargetUrl()); pendingPath = null; });
            }
            catch (TaskCanceledException) { }
            catch (Exception e) { Show("Error", e.Message, busy: false, canRetry: true); }
        });
    }

    /// <summary>
    /// Control commands, from the launcher's long-press menu or from the PC:
    /// <c>adb shell am start -n io.github.alastergr.pocketiris/.ControlActivity --es cmd update</c>
    /// (update | restart | stop | reload | browser | terminal | scripts | run | setup | debug-on | debug-off).
    /// The view and reload commands work from anywhere; the rest only through ControlActivity (the adb shell).
    /// </summary>
    /// <param name="cmd">The command name.</param>
    /// <param name="fromAdb">True when it came through the control entry point (only the adb shell can use it).</param>
    void HandleCommand(string? cmd, bool fromAdb)
    {
        //  Commands that run code in Termux or change safety settings are ignored unless they came from the adb shell.
        if (cmd is "update" or "restart" or "stop" or "debug-on" or "debug-off" or "setup" or "run" && !fromAdb)
        {
            Android.Util.Log.Warn("PocketIris", "ignored command '" + cmd + "': not sent through the control entry point");
            Start();
            return;
        }

        //  Do what the command name says; an unknown name does nothing.
        switch (cmd)
        {
            case "update": RunMaintenance("Updating…", "Installing the newest build and restarting the server", termux.Env() + " bash /sdcard/Documents/ca/ca-update", 240); break;
            case "restart": RunMaintenance("Restarting…", "Restarting the server", termux.Env() + " " + TermuxRunner.Home + "/bin/ca-restart", 90); break;
            case "stop": termux.Run(TermuxRunner.Home + "/bin/ca-stop"); Show("Server stopped", "Tap Try again to start it.", busy: false, canRetry: true); break;
            case "reload": RunOnUiThread(() => web.Reload()); break;
            case "browser": RunOnUiThread(() => ShowView("Browser")); break;
            case "terminal": RunOnUiThread(() => ShowView("Terminal")); break;
            case "scripts": RunOnUiThread(() => ShowView("Scripts")); break;
            case "pocketiris": RunOnUiThread(() => ShowView("PocketIris")); break;
            case "chat": RunOnUiThread(() => ShowView("Chat")); break;
            case "debug-on": SetDebug(true); break;
            case "debug-off": SetDebug(false); break;
            case "setup": termux.Run("bash /sdcard/Documents/ca/install.sh"); Start(); break;
            case "run": termux.RunScript(Intent?.GetStringExtra("script")); break;
        }
    }

    /// <summary>
    /// Development switch: lets the PC inspect and drive the page in the WebView over USB debugging
    /// (Chrome DevTools protocol). Off by default; the choice is remembered.
    /// </summary>
    public void SetDebug(bool on)
    {
        GetSharedPreferences("ca", FileCreationMode.Private)!.Edit()!.PutBoolean("debug", on)!.Apply();
        WebView.SetWebContentsDebuggingEnabled(on);
    }

    /// <summary>True while page debugging from the PC is switched on.</summary>
    public bool IsDebug() => Prefs.GetBoolean("debug", false);

    /// <summary>Switches phone control on or off, and remembers the choice.</summary>
    public void SetPhoneControl(bool on)
    {
        Prefs.Edit()!.PutBoolean("phoneControl", on)!.Apply();
        PhoneControlEnabled = on;
    }

    /// <summary>True if the user chose "Always allow" for controlling this app.</summary>
    public bool IsControlAlwaysAllowed(string pkg) => Prefs.GetStringSet("controlAllowed", new HashSet<string>())!.Contains(pkg);

    /// <summary>Remembers that the user always allows Claude to control this app.</summary>
    public void AllowControlAlways(string pkg)
    {
        var set = new HashSet<string>(Prefs.GetStringSet("controlAllowed", new HashSet<string>())!) { pkg };
        Prefs.Edit()!.PutStringSet("controlAllowed", set)!.Apply();
    }

    /// <summary>Opens Android's accessibility settings, where the user switches the PocketIris service on.</summary>
    public void OpenAccessibilitySettings() => StartActivity(new Intent(Android.Provider.Settings.ActionAccessibilitySettings));

    /// <summary>Switches the fingerprint shortcut in approval dialogs on or off, and remembers the choice.</summary>
    public void SetFingerprintAllows(bool on)
    {
        Prefs.Edit()!.PutBoolean("fingerprintAllows", on)!.Apply();
        Approval.FingerprintAllows = on;
    }

    /// <summary>Runs a Termux command, hides the UI, waits for the server to go down and come back, then reloads.</summary>
    void RunMaintenance(string title, string info, string command, int timeoutSeconds)
    {
        startCts?.Cancel();
        startCts = new CancellationTokenSource();
        var ct = startCts.Token;
        Show(title, info);
        RunOnUiThread(() => web.Visibility = ViewStates.Invisible);
        _ = Task.Run(async () =>
        {
            //  Any unexpected problem is shown on the overlay, and a cancelled wait is silently dropped.
            try
            {
                //  Stop here if Termux could not be asked (the overlay already says why).
                if (!termux.Run(command)) return;
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                bool wentDown = false;
                await Task.Delay(1500, ct);

                //  Watch the server go down and come back up, within the time allowed.
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    bool up = await ServerUp();
                    //  Remember that the server was down at some point.
                    if (!up) wentDown = true;

                    //  Once it is back after having been down, the job is done.
                    if (up && wentDown) { await Task.Delay(1500, ct); break; }
                    await Task.Delay(1000, ct);
                }

                //  Reload the page if the server is back, otherwise explain where to look.
                if (await ServerUp()) RunOnUiThread(() => web.LoadUrl(ServerUrl));
                else Show("The server is not back", "Open Termux and run  ca-logs  or  cat ~/ca-update.log", busy: false, canRetry: true);
            }
            catch (TaskCanceledException) { }
            catch (Exception e) { Show("Error", e.Message, busy: false, canRetry: true); }
        });
    }

    /// <summary>Android answered a permission question: continue with whatever was waiting for it.</summary>
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        bool ok = grantResults.Length > 0 && grantResults[0] == Permission.Granted;

        //  Start the server now that the Termux permission was granted.
        if (requestCode == TermuxRunner.RequestRunCommand && ok) Start();

        //  Tell the location tool whether the user allowed location access.
        if (requestCode == RequestLocation) LocationResult?.TrySetResult(ok);

        //  Tell the web page whether it may use the microphone.
        if (requestCode == RequestMic)
        {
            var p = pendingWebPermission; pendingWebPermission = null;
            //  Tell the page whether it may use the microphone.
            if (p != null) { if (ok) p.Grant(p.GetResources()); else p.Deny(); }
        }
    }

    /// <summary>Another screen (camera or file picker) returned its result to the app.</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        //  Pass Android's screen-capture answer on to the screenshot tool.
        if (requestCode == ScreenCapture.RequestScreen)
        {
            ScreenCapture.Consent?.TrySetResult((resultCode, data));
            return;
        }

        //  Hand the camera photo (if any) to the tool that asked for it.
        if (requestCode == RequestCamera)
        {
            var bmp = resultCode == Result.Ok ? data?.Extras?.Get("data") as Bitmap : null;
            CameraResult?.TrySetResult(bmp);
            return;
        }

        //  Ignore results the app did not ask for.
        if (requestCode != RequestFile) return;
        var uris = WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data);
        fileCallback?.OnReceiveValue(uris);
        fileCallback = null;
    }

    /// <summary>The Back button closes custom views first, then goes back inside the chat, and finally sends the app to the background.</summary>
    public override void OnBackPressed()
    {
        //  Back inside a custom view: step back in the browser page, otherwise return to Chat.
        if (browserPane.Visibility == ViewStates.Visible)
        {
            //  Go back one page in the browser, or return to the chat when there is no earlier page.
            if (browser.Web.CanGoBack()) browser.Web.GoBack(); else ShowView("Chat");
            return;
        }
        //  Leave the Terminal or Scripts view first, if one is showing.
        if (tools.AnyVisible) { ShowView("Chat"); return; }

        //  In the chat itself: go back a page, or leave the app.
        if (web.Visibility == ViewStates.Visible && web.CanGoBack()) web.GoBack();
        else MoveTaskToBack(true);
    }

    /// <summary>The app came to the front: wake the pages and check the server is still running.</summary>
    protected override void OnResume()
    {
        base.OnResume();
        IsForeground = true;
        tools.OnResume();
        StartDotLoop();
        Current = this;
        browser.OnActivityResumed();
        mcp.Start();
        web.OnResume();

        //  The server may have been stopped while the app was in the background, so check and restart it.
        if (web.Visibility == ViewStates.Visible)
            _ = Task.Run(async () => { if (!await ServerUp()) RunOnUiThread(Start); });
    }

    /// <summary>The app left the front: pause the pages and forget that it is the visible activity.</summary>
    protected override void OnPause()
    {
        tools.OnPause();
        dotLoop?.Cancel();
        IsForeground = false;
        //  Forget this screen if it is the current one.
        if (ReferenceEquals(Current, this)) Current = null;
        web.OnPause();
        base.OnPause();
    }

    /// <summary>Handles the chat page's loading events and keeps links inside the safe set of addresses.</summary>
    class Client(MainActivity a) : WebViewClient
    {
        //  The app already keeps the page inside the system bars, so the page must not add its own safe-area padding on top.
        const string NoSafeArea = "(function(){var m=document.querySelector('meta[name=viewport]');if(m)m.content=m.content.replace(/, *viewport-fit=cover/,'');})()";

        /// <summary>A page started loading: strip its extra safe-area padding.</summary>
        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
        {
            base.OnPageStarted(view, url, favicon);
            //  The chat view only ever shows the chat server. A direct load of anything else (which skips the link check below) is stopped,
            //  and the chat is opened instead, so an outside page can never sit in this view next to its bridge and microphone.
            if (url != null && (url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) && !IsServerPage(url))
            {
                Android.Util.Log.Warn("PocketIris", "stopped a non-chat page in the chat view: " + url);
                view?.StopLoading();
                view?.LoadUrl(ServerUrl);
                return;
            }
            view?.EvaluateJavascript(NoSafeArea, null);
        }

        /// <summary>A page finished loading: show it and hide the start-up overlay.</summary>
        public override void OnPageFinished(WebView? view, string? url)
        {
            view?.EvaluateJavascript(NoSafeArea, null);

            //  Add the "Move to trash" shortcut to the chat's own pages.
            if (IsServerPage(url)) view?.EvaluateJavascript(a.ShortcutScript(), null);

            //  Reveal the chat page only when it is the chat server's page.
            if (IsServerPage(url))
                a.RunOnUiThread(() => { a.web.Visibility = ViewStates.Visible; a.overlay.Visibility = ViewStates.Gone; });
        }

        /// <summary>The page failed to load: hide it and try to start the server again.</summary>
        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            //  Only a failure of the main page matters; small resources are ignored.
            if (request?.IsForMainFrame == true)
            {
                a.RunOnUiThread(() => a.web.Visibility = ViewStates.Invisible);
                a.Show("Lost the server", "Trying to start it again…");
                a.RunOnUiThread(a.Start);
            }
        }

        /// <summary>A link was tapped in the chat: stay on the server's pages, and open other public sites in the Browser view.</summary>
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString() ?? "";

            //  Pages of the chat server open normally.
            if (IsServerPage(url)) return false;
            var uri = request!.Url!;

            //  Public web links open in the Browser view; everything else is dropped.
            if ((uri.Scheme == "http" || uri.Scheme == "https") && !BrowserHost.IsBlockedHost(uri.Host ?? ""))
            {
                var target = uri.ToString()!;
                a.browser.UserOpened(uri.Host ?? "");
                a.RunOnUiThread(() => { a.browser.Web.LoadUrl(target); a.ShowBrowserPane(); });
            }
            return true;
        }
    }

    /// <summary>Handles the chat page's requests for a file picker and the microphone.</summary>
    class Chrome(MainActivity a) : WebChromeClient
    {
        /// <summary>The page wants the user to pick a file to upload.</summary>
        public override bool OnShowFileChooser(WebView? webView, IValueCallback? callback, FileChooserParams? p)
        {
            a.fileCallback?.OnReceiveValue(null);
            a.fileCallback = callback;

            //  Open the system file picker; report failure if the phone has none.
            try { a.StartActivityForResult(p!.CreateIntent(), RequestFile); return true; }
            catch { a.fileCallback = null; return false; }
        }

        /// <summary>The page wants to use the microphone (voice input): ask Android first if needed.</summary>
        public override void OnPermissionRequest(PermissionRequest? request)
        {
            //  Ignore an empty request.
            if (request == null) return;
            var resources = request.GetResources() ?? [];

            //  Only the chat server's own page may ask, and only for the microphone (voice input); camera, other media and other pages are refused.
            if (!IsServerPage(request.Origin?.ToString()) || resources.Length != 1 || resources[0] != PermissionRequest.ResourceAudioCapture)
            {
                Android.Util.Log.Warn("PocketIris", $"refused a page permission request from {request.Origin}: {string.Join(",", resources)}");
                request.Deny();
                return;
            }

            //  Ask the user for microphone access the first time; otherwise allow it.
            if (a.CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted)
            {
                a.pendingWebPermission = request;
                a.RequestPermissions(new[] { Manifest.Permission.RecordAudio }, RequestMic);
            }
            else request.Grant(request.GetResources());
        }
    }
}

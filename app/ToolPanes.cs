using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace PocketIris;

/// <summary>
/// The Terminal view (Termux's ttyd shown in a WebView, so the user never opens Termux) and the Scripts view
/// (scripts Claude pushes to Documents/ca/scripts, run in that terminal after the user has seen them).
/// </summary>
public sealed class ToolPanes
{
    public const int TermPort = 3402;
    public const string ScriptsDir = "/sdcard/Documents/ca/scripts";

    static Color Bg => Tokens.Sys.Background;
    static Color Card => Tokens.Sys.Surface;
    static Color Fg => Tokens.Sys.Text;
    static Color Dim => Tokens.Sys.TextDim;
    static Color Accent => Tokens.Sys.Accent;

    readonly Android.App.Activity act;
    public LinearLayout TerminalPane { get; }
    public LinearLayout ScriptsPane { get; }
    readonly WebView term;
    readonly LinearLayout list;
    string loadedArgs = "\0";

    /// <summary>Called just before the terminal opens, so the owner can make sure the terminal server is running.</summary>
    public Action? BeforeTerminal;

    /// <summary>True while the Terminal or the Scripts view is on screen.</summary>
    public bool AnyVisible => TerminalPane.Visibility == ViewStates.Visible || ScriptsPane.Visibility == ViewStates.Visible;

    int Dp(int v) => (int)(v * act.Resources!.DisplayMetrics!.Density);

    /// <summary>Makes this view's own labels follow the chosen text size (the terminal's letters are sized by its server).</summary>
    /// <param name="percent">100 for normal.</param>
    public void SetTextZoom(int percent)
    {
        //  The labels of the Shell and Scripts switch use the same scale as the rest of the app.
        foreach (var label in modeLabels) label.TextSize = Tokens.Text.Md * Appearance.TextScale;
    }

    /// <summary>Loads the terminal page again, so it picks up a restarted terminal server's new font size.</summary>
    public void ReloadTerminal() => term.Reload();

    readonly List<TextView> modeLabels = new();

    /// <summary>Builds both views and adds them (hidden) to the root layout.</summary>
    /// <param name="activity">The owning activity.</param>
    /// <param name="root">The full-screen layout the views are added to.</param>
    /// <param name="authToken">The password the terminal server expects.</param>
    /// <param name="goTo">Called with a view name when a tab is tapped.</param>
    public ToolPanes(Android.App.Activity activity, FrameLayout root, string authToken, Action<string> goTo)
    {
        act = activity;

        //  Terminal view: tab row, the terminal page, then a row of keys a phone keyboard does not have.
        TerminalPane = new LinearLayout(act) { Orientation = Orientation.Vertical, Visibility = ViewStates.Invisible };
        TerminalPane.SetBackgroundColor(Bg);
        TerminalPane.AddView(ModeSwitch("Shell", goTo), PaneHeader.Params(act));
        term = new WebView(act);
        term.SetBackgroundColor(Bg);
        term.Settings.JavaScriptEnabled = true;
        term.Settings.DomStorageEnabled = true;
        term.SetWebViewClient(new TermClient(authToken));
        TerminalPane.AddView(term, new LinearLayout.LayoutParams(-1, 0, 1f));
        TerminalPane.AddView(KeyRow(), new LinearLayout.LayoutParams(-1, -2));
        root.AddView(TerminalPane, new FrameLayout.LayoutParams(-1, -1));

        //  Scripts view: tab row, then a scrolling list of script cards.
        ScriptsPane = new LinearLayout(act) { Orientation = Orientation.Vertical, Visibility = ViewStates.Invisible };
        ScriptsPane.SetBackgroundColor(Bg);
        ScriptsPane.AddView(ModeSwitch("Scripts", goTo), PaneHeader.Params(act));
        var scroll = new ScrollView(act);
        list = new LinearLayout(act) { Orientation = Orientation.Vertical };
        list.SetPadding(Dp(Tokens.Space.Lg), Dp(Tokens.Space.Sm), Dp(Tokens.Space.Lg), Dp(Tokens.Space.X3l));
        scroll.AddView(list, new ViewGroup.LayoutParams(-1, -2));
        ScriptsPane.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));
        root.AddView(ScriptsPane, new FrameLayout.LayoutParams(-1, -1));
    }

    /// <summary>Builds the small Shell | Scripts switch at the top of the Terminal tab's two screens.</summary>
    /// <param name="current">"Shell" or "Scripts": the one that is open.</param>
    /// <param name="goTo">Called with "Terminal" or "Scripts" when the other half is tapped.</param>
    LinearLayout ModeSwitch(string current, Action<string> goTo)
    {
        var row = PaneHeader.Create(act);

        //  One half for each choice; the open one is in the brand colour.
        foreach (var (label, view) in new[] { ("Shell", "Terminal"), ("Scripts", "Scripts") })
        {
            var on = label == current;
            var half = new TextView(act) { Text = label, TextSize = Tokens.Text.Md * Appearance.TextScale, Gravity = Android.Views.GravityFlags.Center };
            modeLabels.Add(half);
            half.SetTextColor(on ? Tokens.Sys.OnAccent : Tokens.Sys.TextDim);
            half.Background = Rounded(on ? Accent : Tokens.Sys.None, Tokens.Radius.Md);

            //  Only the half that is not open reacts to a tap.
            if (!on) half.Click += (_, _) => goTo(view);
            var lp = PaneHeader.ChildParams(0, 1f);
            lp.SetMargins(Dp(Tokens.Space.Hair), 0, Dp(Tokens.Space.Hair), 0);
            row.AddView(half, lp);
        }
        return row;
    }

    /// <summary>Hides both views.</summary>
    public void HideAll()
    {
        TerminalPane.Visibility = ViewStates.Invisible;
        ScriptsPane.Visibility = ViewStates.Invisible;
    }

    /// <summary>Opens the terminal; a new shell is started only when there is something specific to run.</summary>
    /// <param name="args">Null for a normal shell, or for example ["run", "name.sh"] to run a script first.</param>
    public void ShowTerminal(string[]? args)
    {
        BeforeTerminal?.Invoke();
        ScriptsPane.Visibility = ViewStates.Invisible;
        TerminalPane.Visibility = ViewStates.Visible;
        var key = args == null ? "" : string.Join("&", args.Select(a => "arg=" + Uri.EscapeDataString(a)));

        //  Load the terminal page the first time, or whenever a script has to run; otherwise keep the running shell.
        if (key.Length > 0 || loadedArgs == "\0")
        {
            loadedArgs = key;
            term.LoadUrl($"http://127.0.0.1:{TermPort}/" + (key.Length > 0 ? "?" + key : ""));
        }
    }

    /// <summary>Opens the Scripts view and refreshes the list.</summary>
    public void ShowScripts()
    {
        TerminalPane.Visibility = ViewStates.Invisible;
        ScriptsPane.Visibility = ViewStates.Visible;
        Refresh();
    }

    /// <summary>Pauses the terminal page while the app is in the background.</summary>
    public void OnPause() => term.OnPause();

    /// <summary>Wakes the terminal page when the app comes back.</summary>
    public void OnResume() => term.OnResume();

    // ---------- scripts list ----------

    /// <summary>Rebuilds the list of script cards from the scripts folder.</summary>
    void Refresh()
    {
        list.RemoveAllViews();

        //  Without "All files access" the folder cannot be read, so explain and offer the Settings shortcut.
        if (!Android.OS.Environment.IsExternalStorageManager)
        {
            list.AddView(Note("The launcher needs “All files access” to see the scripts Claude pushes to Documents/ca/scripts."));
            var grant = Btn("Allow in Settings", (_, _) =>
            {
                //  Try the page for this app first, and fall back to the general list if the phone has no such page.
                try { act.StartActivity(new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + act.PackageName))); }
                catch { act.StartActivity(new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission)); }
            });
            list.AddView(grant);
            return;
        }
        var dir = new Java.IO.File(ScriptsDir);
        var files = dir.ListFiles()?.Where(f => f.IsFile && IsSafeName(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        //  Show a friendly hint when no script has been pushed yet.
        if (files.Count == 0)
        {
            list.AddView(Note("No scripts yet. When Claude proposes one, it lands here and you run it with one tap."));
            return;
        }

        //  One card per script.
        foreach (var f in files) list.AddView(ScriptCard(f));
    }

    /// <summary>True for plain file names made only of letters, digits, dot, underscore and dash.</summary>
    static bool IsSafeName(string n) => n.Length > 0 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Builds the card for one script: its name, a one-line description taken from its second line, the date and a Run button.</summary>
    View ScriptCard(Java.IO.File f)
    {
        string text = "";

        //  Read the script text; a script that cannot be read is simply shown without a description.
        try { text = File.ReadAllText(f.AbsolutePath); } catch (Exception swallowed) { Diag.Swallowed("ToolPanes:152", swallowed); }
        var lines = text.Split('\n');
        var desc = lines.Skip(1).Take(4).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('#') && l.Length > 2)?.TrimStart('#', ' ') ?? "";
        var modified = DateTimeOffset.FromUnixTimeMilliseconds(f.LastModified()).ToLocalTime();

        var card = new LinearLayout(act) { Orientation = Orientation.Horizontal };
        card.SetGravity(GravityFlags.CenterVertical);
        card.SetPadding(Dp(Tokens.Space.Xl), Dp(Tokens.Space.Lg), Dp(Tokens.Space.Lg), Dp(Tokens.Space.Lg));
        card.Background = Rounded(Card, Tokens.Radius.Lg);
        var col = new LinearLayout(act) { Orientation = Orientation.Vertical };
        var name = new TextView(act) { Text = System.IO.Path.GetFileNameWithoutExtension(f.Name), TextSize = Tokens.Text.Lg * Appearance.TextScale };
        name.SetTextColor(Fg); name.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        col.AddView(name);

        //  Add the description line only when the script has one.
        if (desc.Length > 0)
        {
            var d = new TextView(act) { Text = desc, TextSize = Tokens.Text.Sm * Appearance.TextScale };
            d.SetTextColor(Dim); d.SetPadding(0, Dp(Tokens.Space.Hair), 0, 0);
            col.AddView(d);
        }
        var when = new TextView(act) { Text = modified.ToString("d MMM, HH:mm"), TextSize = Tokens.Text.Xs * Appearance.TextScale };
        when.SetTextColor(Dim); when.SetPadding(0, Dp(Tokens.Space.Xs), 0, 0);
        col.AddView(when);
        card.AddView(col, new LinearLayout.LayoutParams(0, -2, 1f));
        var run = new Button(act) { Text = "Run" };
        run.SetAllCaps(false); run.SetTextColor(Tokens.Sys.OnAccent);
        run.Background = Rounded(Accent, Tokens.Radius.Md);
        run.SetPadding(Dp(Tokens.Space.Xl), 0, Dp(Tokens.Space.Xl), 0);
        run.Click += (_, _) => Confirm(f.Name, text);
        card.AddView(run, new LinearLayout.LayoutParams(-2, Dp(Tokens.Size.Control)));
        var lp = new LinearLayout.LayoutParams(-1, -2);
        lp.SetMargins(0, Dp(Tokens.Space.Md), 0, 0);
        card.LayoutParameters = lp;
        return card;
    }

    /// <summary>Shows the script text and runs it in the terminal only if the user agrees, so they always see what is about to run.</summary>
    void Confirm(string fileName, string text)
    {
        var shown = text.Length > 1800 ? text[..1800] + "\n…" : text;
        var tv = new TextView(act) { Text = shown, TextSize = Tokens.Text.Xs * Appearance.TextScale, Typeface = Typeface.Monospace };
        tv.SetTextColor(Fg); tv.SetPadding(Dp(Tokens.Space.X2l), Dp(Tokens.Space.Md), Dp(Tokens.Space.X2l), Dp(Tokens.Space.Md));
        var sv = new ScrollView(act); sv.AddView(tv);
        new Android.App.AlertDialog.Builder(act, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!
            .SetTitle("Run " + fileName + "?")!
            .SetView(sv)!
            .SetPositiveButton("Run in terminal", (_, _) => ShowTerminal(["run", fileName]))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ---------- small view helpers ----------

    /// <summary>A rounded rectangle background in the given colour.</summary>
    GradientDrawable Rounded(Color c, int radiusDp)
    {
        var g = new GradientDrawable(); g.SetColor(c); g.SetCornerRadius(Dp(radiusDp)); return g;
    }

    /// <summary>A grey explanatory paragraph.</summary>
    TextView Note(string t)
    {
        var v = new TextView(act) { Text = t, TextSize = Tokens.Text.Lg * Appearance.TextScale };
        v.SetTextColor(Dim); v.SetPadding(Dp(Tokens.Space.Xs), Dp(Tokens.Space.X3l), Dp(Tokens.Space.Xs), Dp(Tokens.Space.Lg));
        return v;
    }

    /// <summary>A plain button that runs the given action when tapped.</summary>
    Button Btn(string text, EventHandler onClick)
    {
        var b = new Button(act) { Text = text };
        b.SetAllCaps(false); b.SetMinWidth(0); b.SetMinimumWidth(0);
        b.SetPadding(Dp(Tokens.Space.Lg), 0, Dp(Tokens.Space.Lg), 0);
        b.Click += onClick;
        return b;
    }

    // ---------- on-screen keys the phone keyboard lacks ----------

    /// <summary>Builds the scrolling row of terminal keys (Esc, Tab, Ctrl-C, arrows and a few symbols).</summary>
    View KeyRow()
    {
        var scroll = new HorizontalScrollView(act) { HorizontalScrollBarEnabled = false };
        scroll.SetBackgroundColor(Tokens.Sys.Sunken);
        var row = new LinearLayout(act) { Orientation = Orientation.Horizontal };
        row.SetPadding(Dp(Tokens.Space.Xs), Dp(Tokens.Space.Hair), Dp(Tokens.Space.Xs), Dp(Tokens.Space.Hair));

        //  Adds one key button that sends the given characters to the terminal when tapped.
        void Key(string label, string data)
        {
            var b = new Button(act) { Text = label };
            b.SetAllCaps(false); b.SetMinWidth(0); b.SetMinimumWidth(0); b.SetTextColor(Fg);
            b.SetPadding(Dp(Tokens.Space.Lg), 0, Dp(Tokens.Space.Lg), 0);
            b.Background = Rounded(Card, Tokens.Radius.Sm);
            b.Click += (_, _) => SendKeys(data);
            var lp = new LinearLayout.LayoutParams(-2, Dp(Tokens.Size.Control)); lp.SetMargins(Dp(Tokens.Space.Xs), Dp(Tokens.Space.Hair), Dp(Tokens.Space.Xs), Dp(Tokens.Space.Hair));
            row.AddView(b, lp);
        }
        Key("Esc", "\u001b"); Key("Tab", "\t"); Key("Ctrl-C", "\u0003"); Key("Ctrl-D", "\u0004");
        Key("↑", "\u001b[A"); Key("↓", "\u001b[B"); Key("←", "\u001b[D"); Key("→", "\u001b[C");
        Key("|", "|"); Key("~", "~"); Key("/", "/"); Key("-", "-"); Key("$", "$");
        scroll.AddView(row);
        return scroll;
    }

    /// <summary>Types the given characters into the terminal page as if they came from the keyboard.</summary>
    void SendKeys(string data)
    {
        var json = PageBridge.Quote(data);
        term.EvaluateJavascript($"(function(){{var t=window.term; if(t&&t.input) t.input({json}, true);}})()", null);
    }

    /// <summary>Signs in to the terminal server with the token this app handed Termux at start-up, and keeps the page on this phone.</summary>
    sealed class TermClient(string token) : WebViewClient
    {
        /// <summary>Answers the terminal's password prompt automatically, and only for the phone's own address.</summary>
        public override void OnReceivedHttpAuthRequest(WebView? view, HttpAuthHandler? handler, string? host, string? realm)
        {
            //  Send the password only to this phone's own address; refuse anything else.
            if (host == "127.0.0.1") handler?.Proceed("ca", token); else handler?.Cancel();
        }

        /// <summary>Stops the terminal page from navigating to any other address.</summary>
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) =>
            request?.Url?.Host != "127.0.0.1";
    }
}

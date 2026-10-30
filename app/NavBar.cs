using Android.App;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PocketIris;

/// <summary>
/// The one navigation bar of the app, shown on every screen including Chat: Chat, Browser, Terminal and PocketIris.
/// It sits at the top (always visible) or at the bottom (hidden while the keyboard is open), as the Appearance setting says.
/// The PocketIris tab carries a small dot while something that deserves attention is on: a trusted session, phone control
/// or the debugging link.
/// </summary>
public sealed class NavBar
{
    /// <summary>The views the bar can open, in order. "Scripts" belongs to the Terminal tab and is not listed.</summary>
    public static readonly string[] Views = ["Chat", "Browser", "Terminal", "PocketIris"];

    readonly Activity activity;
    readonly Dictionary<string, (TextView Label, View Dot)> tabs = new();
    int lastWidth = -1;

    /// <summary>The bar itself, to be placed in the screen layout.</summary>
    public LinearLayout View { get; }

    /// <summary>Builds the bar.</summary>
    /// <param name="owner">The activity that owns the screens.</param>
    /// <param name="goTo">Called with the view's name when a tab is tapped.</param>
    public NavBar(Activity owner, Action<string> goTo)
    {
        activity = owner;
        var density = activity.Resources!.DisplayMetrics!.Density;
        int Dp(int v) => (int)(v * density);
        View = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        View.SetBackgroundColor(Tokens.Sys.Sunken);
        View.SetPadding(Dp(Tokens.Space.Sm), Dp(Tokens.Space.Sm), Dp(Tokens.Space.Sm), Dp(Tokens.Space.Sm));

        //  Make one equal-width tab for every view name.
        foreach (var name in Views)
        {
            var cell = new FrameLayout(activity);
            var label = new TextView(activity) { Text = name, TextSize = Tokens.Text.Md * Appearance.TextScale, Gravity = GravityFlags.Center };
            label.SetPadding(0, Dp(Tokens.Space.Md), 0, Dp(Tokens.Space.Md));
            cell.AddView(label, new FrameLayout.LayoutParams(-1, -2));

            //  A small round dot in the corner of the tab, hidden until it is needed.
            var dotShape = new GradientDrawable();
            dotShape.SetShape(ShapeType.Oval);
            dotShape.SetColor(Tokens.Sys.Accent);
            var dot = new View(activity) { Visibility = ViewStates.Gone };
            dot.Background = dotShape;
            cell.AddView(dot, new FrameLayout.LayoutParams(Dp(Tokens.Space.Md), Dp(Tokens.Space.Md), GravityFlags.Top | GravityFlags.End) { TopMargin = Dp(Tokens.Space.Sm), RightMargin = Dp(Tokens.Space.Md) });
            var target = name;
            cell.Click += (_, _) => goTo(target);
            //  Each tab gets its text's width (set in ApplyTextScale) plus an equal share of the space left over, so all have the same side padding.
            var lp = new LinearLayout.LayoutParams(-2, -2, 1f);
            lp.SetMargins(Dp(Tokens.Space.Hair), 0, Dp(Tokens.Space.Hair), 0);
            View.AddView(cell, lp);
            tabs[name] = (label, dot);
        }
        View.LayoutChange += (_, _) =>
        {
            //  Only a new width needs the tabs worked out again; posted, because a layout pass is running right now.
            if (View.Width == lastWidth) return;
            lastWidth = View.Width;
            View.Post(ApplyTextScale);
        };
        Select("Chat");
    }

    /// <summary>
    /// Sizes the tabs for the chosen text size: every name the same size, as large as the chosen scale allows while all still fit with at least
    /// a small gap on each side; each tab is as wide as its name, and the row's spare space is shared equally, so all tabs have the same padding.
    /// </summary>
    public void ApplyTextScale()
    {
        var metrics = activity.Resources!.DisplayMetrics!;
        float Px(float sp) => Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Sp, sp, metrics);
        int Dp(int v) => (int)(v * metrics.Density);
        var wanted = Tokens.Text.Md * Appearance.TextScale;
        var best = wanted;

        //  Measures one name at a text size, in pixels.
        float Measure(TextView label, float sp)
        {
            using var paint = new Android.Graphics.Paint(label.Paint) { TextSize = Px(sp) };
            return paint.MeasureText(label.Text ?? "");
        }

        var room = View.Width - View.PaddingLeft - View.PaddingRight - tabs.Count * (2 * Dp(Tokens.Space.Hair) + 2 * Dp(Tokens.Space.Md));
        var total = tabs.Values.Sum(t => Measure(t.Label, wanted));
        //  Names too wide for the row shrink together (before the first layout there is no width to compare with).
        if (View.Width > 0 && total > room) best = Math.Max(wanted * room / total, Tokens.Text.Xs);

        foreach (var (label, _) in tabs.Values)
        {
            label.SetMaxLines(1);
            //  Apply the text size only when it differs, so nothing is laid out again for nothing.
            if (Math.Abs(label.TextSize - Px(best)) > 0.5f) label.TextSize = best;
            var cell = (View)label.Parent!;
            var width = (int)Math.Ceiling(Measure(label, best));
            //  The same for the tab's own width; the equal weights then share out what is left.
            if (cell.LayoutParameters!.Width != width) { cell.LayoutParameters.Width = width; cell.RequestLayout(); }
        }
    }

    /// <summary>Highlights the tab of the view that is open. Scripts counts as the Terminal tab.</summary>
    /// <param name="view">The name of the open view.</param>
    public void Select(string view)
    {
        var current = view == "Scripts" ? "Terminal" : view;
        var density = activity.Resources!.DisplayMetrics!.Density;

        //  Give each tab its look: the current one in the brand colour, the others quiet.
        foreach (var (name, (label, dot)) in tabs)
        {
            var isCurrent = name == current;
            label.SetTextColor(isCurrent ? Tokens.Sys.OnAccent : Tokens.Sys.TextDim);
            var shape = new GradientDrawable();
            shape.SetCornerRadius((int)(Tokens.Radius.Md * density));
            shape.SetColor(isCurrent ? Tokens.Sys.Accent : Tokens.Sys.None);
            label.Background = shape;
            ((GradientDrawable)dot.Background!).SetColor(isCurrent ? Tokens.Sys.OnAccent : Tokens.Sys.Accent);
        }
    }

    /// <summary>Shows or hides the attention dot on the PocketIris tab according to what is switched on right now.</summary>
    public void RefreshDot()
    {
        var on = TrustedSession.Active || MainActivity.PhoneControlEnabled || BluetoothDebug.Enabled;
        tabs["PocketIris"].Dot.Visibility = on ? ViewStates.Visible : ViewStates.Gone;
    }
}

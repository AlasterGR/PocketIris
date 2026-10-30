using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PocketIris;

/// <summary>
/// The one header row of the app's own tabs (Browser: address bar; Terminal: Shell | Scripts; PocketIris: its title). Every header has
/// exactly the same height, colour and padding, so moving between the tabs feels calm; only what sits inside differs. The height is led by
/// the Browser's address bar: one line of text at the chosen text size plus a small gap above and below, with no extra space around the row,
/// so it grows and shrinks with the text size setting.
/// </summary>
public static class PaneHeader
{
    /// <summary>Every header row made so far, so a new text size can resize them all at once.</summary>
    static readonly List<WeakReference<LinearLayout>> Rows = new();

    /// <summary>Every header button made so far, with whether it is the narrow kind (half as wide as tall).</summary>
    static readonly List<(WeakReference<SymbolButton> Button, bool Narrow)> Buttons = new();

    /// <summary>A new, empty header row; add the tab's own controls to it, each filling the height (use <see cref="ChildParams"/>).</summary>
    /// <param name="c">The activity.</param>
    public static LinearLayout Create(Context c)
    {
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetBackgroundColor(Tokens.Sys.Sunken);
        row.SetPadding(Dp(c, Tokens.Space.Md), 0, Dp(c, Tokens.Space.Md), 0);
        Rows.Add(new WeakReference<LinearLayout>(row));
        return row;
    }

    /// <summary>
    /// The header's height in pixels: one line of normal text at the chosen text size (the address bar's text) plus a small gap above and below.
    /// </summary>
    /// <param name="c">The activity.</param>
    public static int Height(Context c)
    {
        var metrics = c.Resources!.DisplayMetrics!;
        using var paint = new Paint { TextSize = Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Sp, Tokens.Text.Md * Appearance.TextScale, metrics) };
        var line = paint.GetFontMetricsInt()!;
        return line.Descent - line.Ascent + 2 * Dp(c, Tokens.Space.Sm);
    }

    /// <summary>How a header sits in its tab: full width, the shared height.</summary>
    public static LinearLayout.LayoutParams Params(Context c) => new(-1, Height(c));

    /// <summary>How a control sits in a header: as tall as the header, with the given width (-2 = as wide as it needs, 0 with a weight = share the rest).</summary>
    public static LinearLayout.LayoutParams ChildParams(int width = -2, float weight = 0) => new(width, -1, weight);

    /// <summary>A header button in the brand colour (the Browser's back and reload; later the theme switch) with a symbol that grows with the text size.</summary>
    /// <param name="c">The activity.</param>
    /// <param name="symbol">The symbol it shows, for example "‹" or "↻".</param>
    /// <param name="description">What it does, for screen readers (for example "Back").</param>
    /// <param name="narrow">True for a button half as wide as it is tall (back); false for a square one (reload).</param>
    public static View AccentButton(Context c, string symbol, string description, bool narrow = false)
    {
        var b = new SymbolButton(c, symbol) { ContentDescription = description, Clickable = true, LongClickable = true, Focusable = true };
        var shape = new GradientDrawable();
        shape.SetColor(Tokens.Sys.Accent);
        shape.SetCornerRadius(Dp(c, Tokens.Radius.Sm));
        //  A light ripple shows the press, so the button visibly reacts like every other control.
        b.Background = new RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Tokens.Sys.PressOnAccent), shape, null);
        b.LayoutParameters = ChildParams(ButtonWidth(c, narrow));
        Buttons.Add((new WeakReference<SymbolButton>(b), narrow));
        return b;
    }

    /// <summary>The width of a header button in pixels: as wide as the header is tall, or half that for a narrow one.</summary>
    static int ButtonWidth(Context c, bool narrow) => narrow ? Height(c) / 2 : Height(c);

    /// <summary>Resizes every header and header button after the text size setting changed.</summary>
    /// <param name="c">The activity.</param>
    public static void ApplyTextScale(Context c)
    {
        var height = Height(c);
        //  Each header that still exists gets the new height.
        foreach (var weak in Rows)
        {
            if (!weak.TryGetTarget(out var row) || row.LayoutParameters == null) continue;
            row.LayoutParameters.Height = height;
            row.RequestLayout();
        }
        //  Each button gets the new width; its symbol redraws at the new size by itself.
        foreach (var (weak, narrow) in Buttons)
        {
            if (!weak.TryGetTarget(out var b)) continue;
            if (b.LayoutParameters != null) { b.LayoutParameters.Width = ButtonWidth(c, narrow); b.RequestLayout(); }
            b.Invalidate();
        }
    }

    /// <summary>Turns dp into pixels for this screen.</summary>
    static int Dp(Context c, int v) => (int)(v * c.Resources!.DisplayMetrics!.Density);

    /// <summary>
    /// A header button that draws its symbol centred on the symbol's own ink, not on a text line: text keeps room for accents above and sits on
    /// a baseline, which left "↻" and "‹" visibly below the middle of their boxes. The size follows the text size setting.
    /// </summary>
    public sealed class SymbolButton(Context c, string symbol) : View(c)
    {
        readonly Paint paint = new(PaintFlags.AntiAlias) { Color = Tokens.Sys.OnAccent, TextAlign = Paint.Align.Left };
        readonly Rect ink = new();

        /// <summary>Draws the background (by the base class), then the symbol with the middle of its ink on the middle of the button.</summary>
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            paint.TextSize = Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Sp, Tokens.Text.Xl * Appearance.TextScale, Resources!.DisplayMetrics!);
            paint.GetTextBounds(symbol, 0, symbol.Length, ink);
            canvas.DrawText(symbol, Width / 2f - ink.ExactCenterX(), Height / 2f - ink.ExactCenterY(), paint);
        }
    }
}

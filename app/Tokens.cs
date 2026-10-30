using Android.Graphics;

namespace PocketIris;

/// <summary>
/// The look of PocketIris's own native screens, in two tiers:
///   Tier 1, <see cref="Raw"/>: plain values with no meaning (a colour, a number).
///   Tier 2, <see cref="Sys"/>, <see cref="Space"/>, <see cref="Radius"/>, <see cref="Text"/>, <see cref="Size"/>: what the value is FOR.
/// Screens use tier 2 only; tier 2 refers to tier 1 only; no screen file may contain a colour or size literal
/// (<c>tools\check-tokens.ps1</c> fails when one does). The PocketIris web page gets the same values as CSS variables
/// (<see cref="CssVariables"/>), so native and web can never drift apart.
/// </summary>
public static class Tokens
{
    /// <summary>Tier 1: raw values with no meaning of their own.</summary>
    public static class Raw
    {
        public static readonly Color Ink900 = Color.ParseColor("#1F1E1D");
        public static readonly Color Ink850 = Color.ParseColor("#262524");
        public static readonly Color Ink800 = Color.ParseColor("#2E2D2B");
        public static readonly Color Ink700 = Color.ParseColor("#3D3C39");
        public static readonly Color Stone100 = Color.ParseColor("#E8E6DC");
        public static readonly Color Stone400 = Color.ParseColor("#9C9A92");
        public static readonly Color Terracotta500 = Color.ParseColor("#C96442");
        public static readonly Color White = Color.ParseColor("#FFFFFF");
        public static readonly Color Clear = Color.ParseColor("#00000000");
        public static readonly Color WhiteVeil = Color.ParseColor("#46FFFFFF");

        public const int Dp2 = 2, Dp4 = 4, Dp6 = 6, Dp8 = 8, Dp12 = 12, Dp16 = 16, Dp20 = 20, Dp24 = 24, Dp32 = 32;
        public const int Dp1 = 1, Dp10 = 10, Dp14 = 14, Dp18 = 18, Dp40 = 40, Dp46 = 46, Dp560 = 560, Dp760 = 760, Dp1000 = 1000;
        public const float Sp12 = 12, Sp13 = 13, Sp14 = 14, Sp16 = 16, Sp19 = 19, Sp24 = 24;
    }

    /// <summary>Tier 2: colours by purpose.</summary>
    public static class Sys
    {
        /// <summary>The page behind everything.</summary>
        public static Color Background => Raw.Ink900;
        /// <summary>A card, a dialog or a field sitting on the background.</summary>
        public static Color Surface => Raw.Ink800;
        /// <summary>A strip that sits lower than the background (the tab row, the script output).</summary>
        public static Color Sunken => Raw.Ink850;
        /// <summary>A quiet button or chip on a surface.</summary>
        public static Color ControlQuiet => Raw.Ink700;
        /// <summary>Normal text.</summary>
        public static Color Text => Raw.Stone100;
        /// <summary>Secondary text and inactive labels.</summary>
        public static Color TextDim => Raw.Stone400;
        /// <summary>The brand colour: primary buttons, the current tab, progress.</summary>
        public static Color Accent => Raw.Terracotta500;
        /// <summary>Text or icons on top of the accent colour.</summary>
        public static Color OnAccent => Raw.White;
        /// <summary>The ripple that shows a press on an accent-coloured button.</summary>
        public static Color PressOnAccent => Raw.WhiteVeil;
        /// <summary>No fill at all.</summary>
        public static Color None => Raw.Clear;
    }

    /// <summary>Tier 2: gaps and padding, in dp.</summary>
    public static class Space
    {
        public const int Hair = Raw.Dp2, Xs = Raw.Dp4, Sm = Raw.Dp6, Md = Raw.Dp8, Lg = Raw.Dp12, Xl = Raw.Dp16, X2l = Raw.Dp20, X3l = Raw.Dp24, X4l = Raw.Dp32;
    }

    /// <summary>Tier 2: corner rounding, in dp.</summary>
    public static class Radius
    {
        public const int Sm = Raw.Dp10, Md = Raw.Dp14, Lg = Raw.Dp18, Xl = Raw.Dp24;
    }

    /// <summary>Tier 2: text sizes, in sp (they grow with Android's own font-size setting).</summary>
    public static class Text
    {
        public const float Xs = Raw.Sp12, Sm = Raw.Sp13, Md = Raw.Sp14, Lg = Raw.Sp16, Xl = Raw.Sp19, X2l = Raw.Sp24;
    }

    /// <summary>Tier 2: heights of things you tap, in dp.</summary>
    public static class Size
    {
        public const int Control = Raw.Dp40, ControlLg = Raw.Dp46;
    }

    /// <summary>The three layout states of PocketIris's own screens, by the width of their view (limits: 700 and 890).</summary>
    public static class Layout
    {
        /// <summary>The widest a phone-sized view can be, in dp.</summary>
        public const int PhoneMaxDp = 700;
        /// <summary>The widest a tablet-sized view can be, in dp.</summary>
        public const int TabletMaxDp = 889;
        /// <summary>Gives the layout state for a view width: "phone", "tablet" or "desktop".</summary>
        /// <param name="widthDp">The view's width in dp.</param>
        public static string ForWidth(int widthDp) => widthDp <= PhoneMaxDp ? "phone" : widthDp <= TabletMaxDp ? "tablet" : "desktop";
    }

    /// <summary>
    /// The same values as CSS variables for the PocketIris web page (tier 1 as <c>--pc-raw-*</c>, tier 2 as <c>--pc-sys-*</c>),
    /// so the page never has a colour or size of its own.
    /// </summary>
    public static string CssVariables()
    {
        //  Writes a colour as #rrggbb (or as transparent), the way CSS wants it.
        static string Hex(Color c) => c.A == 0 ? "transparent" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return ":root{" +
            $"--pc-raw-ink-900:{Hex(Raw.Ink900)};--pc-raw-ink-850:{Hex(Raw.Ink850)};--pc-raw-ink-800:{Hex(Raw.Ink800)};--pc-raw-ink-700:{Hex(Raw.Ink700)};" +
            $"--pc-raw-stone-100:{Hex(Raw.Stone100)};--pc-raw-stone-400:{Hex(Raw.Stone400)};--pc-raw-terracotta-500:{Hex(Raw.Terracotta500)};" +
            $"--pc-raw-white:{Hex(Raw.White)};--pc-raw-clear:transparent;" +
            "--pc-sys-background:var(--pc-raw-ink-900);--pc-sys-surface:var(--pc-raw-ink-800);--pc-sys-sunken:var(--pc-raw-ink-850);" +
            "--pc-sys-control-quiet:var(--pc-raw-ink-700);--pc-sys-text:var(--pc-raw-stone-100);--pc-sys-text-dim:var(--pc-raw-stone-400);" +
            "--pc-sys-accent:var(--pc-raw-terracotta-500);--pc-sys-on-accent:var(--pc-raw-white);--pc-sys-none:var(--pc-raw-clear);" +
            $"--pc-raw-space-2:{Raw.Dp2}px;--pc-raw-space-4:{Raw.Dp4}px;--pc-raw-space-6:{Raw.Dp6}px;--pc-raw-space-8:{Raw.Dp8}px;--pc-raw-space-12:{Raw.Dp12}px;" +
            $"--pc-raw-space-16:{Raw.Dp16}px;--pc-raw-space-20:{Raw.Dp20}px;--pc-raw-space-24:{Raw.Dp24}px;--pc-raw-space-32:{Raw.Dp32}px;" +
            "--pc-sys-space-hair:var(--pc-raw-space-2);--pc-sys-space-xs:var(--pc-raw-space-4);--pc-sys-space-sm:var(--pc-raw-space-6);--pc-sys-space-md:var(--pc-raw-space-8);" +
            "--pc-sys-space-lg:var(--pc-raw-space-12);--pc-sys-space-xl:var(--pc-raw-space-16);--pc-sys-space-2xl:var(--pc-raw-space-20);--pc-sys-space-3xl:var(--pc-raw-space-24);--pc-sys-space-4xl:var(--pc-raw-space-32);" +
            $"--pc-raw-radius-10:{Raw.Dp10}px;--pc-raw-radius-14:{Raw.Dp14}px;--pc-raw-radius-18:{Raw.Dp18}px;--pc-raw-radius-24:{Raw.Dp24}px;" +
            "--pc-sys-radius-sm:var(--pc-raw-radius-10);--pc-sys-radius-md:var(--pc-raw-radius-14);--pc-sys-radius-lg:var(--pc-raw-radius-18);--pc-sys-radius-xl:var(--pc-raw-radius-24);" +
            $"--pc-raw-text-12:{Raw.Sp12}px;--pc-raw-text-13:{Raw.Sp13}px;--pc-raw-text-14:{Raw.Sp14}px;--pc-raw-text-16:{Raw.Sp16}px;--pc-raw-text-19:{Raw.Sp19}px;--pc-raw-text-24:{Raw.Sp24}px;" +
            $"--pc-raw-size-40:{Raw.Dp40}px;--pc-raw-size-46:{Raw.Dp46}px;--pc-sys-size-control:var(--pc-raw-size-40);--pc-sys-size-control-lg:var(--pc-raw-size-46);" +
            $"--pc-raw-border-1:{Raw.Dp1}px;--pc-raw-width-560:{Raw.Dp560}px;--pc-raw-width-760:{Raw.Dp760}px;--pc-raw-width-1000:{Raw.Dp1000}px;" +
            "--pc-sys-border-width:var(--pc-raw-border-1);--pc-sys-content-phone:var(--pc-raw-width-560);--pc-sys-content-tablet:var(--pc-raw-width-760);--pc-sys-content-desktop:var(--pc-raw-width-1000);" +
            "--pc-raw-scale-100:1;--pc-raw-scale-115:1.15;--pc-raw-scale-132:1.32;--pc-raw-scale-150:1.5;" +
            "--pc-sys-scale-normal:var(--pc-raw-scale-100);--pc-sys-scale-large:var(--pc-raw-scale-115);--pc-sys-scale-very-large:var(--pc-raw-scale-132);--pc-sys-scale-huge:var(--pc-raw-scale-150);" +
            "}" +
            //  The text sizes use the text scale, so they must be defined on the same element that receives the scale (.pc-root), not on :root.
            ".pc-root{" +
            "--pc-sys-text-xs:calc(var(--pc-raw-text-12) * var(--pc-sys-text-scale,1));--pc-sys-text-sm:calc(var(--pc-raw-text-13) * var(--pc-sys-text-scale,1));" +
            "--pc-sys-text-md:calc(var(--pc-raw-text-14) * var(--pc-sys-text-scale,1));--pc-sys-text-lg:calc(var(--pc-raw-text-16) * var(--pc-sys-text-scale,1));" +
            "--pc-sys-text-xl:calc(var(--pc-raw-text-19) * var(--pc-sys-text-scale,1));--pc-sys-text-2xl:calc(var(--pc-raw-text-24) * var(--pc-sys-text-scale,1));" +
            "}";
    }
}

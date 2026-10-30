using System.Text;

namespace PocketIris;

/// <summary>
/// The user's appearance choices for PocketIris's own screens, kept in the app's private settings:
/// where the navigation bar sits (top, always visible, or bottom, hidden while the keyboard is open),
/// the layout state (auto, phone, tablet or desktop; auto follows the width of the view), and the text size.
/// The app owns these; the PocketIris page and the navigation bar only read them.
/// </summary>
public static class Appearance
{
    static readonly string[] NavChoices = ["top", "bottom"];
    static readonly string[] LayoutChoices = ["auto", "phone", "tablet", "desktop"];
    static readonly string[] TextChoices = ["normal", "large", "very-large", "huge"];

    /// <summary>The width of the content area in dp, set by the activity whenever its layout changes.</summary>
    static int viewWidthDp = 400;

    /// <summary>Raised whenever any appearance value or the measured width state changes.</summary>
    public static event Action? Changed;

    static Android.Content.ISharedPreferences Prefs =>
        Android.App.Application.Context.GetSharedPreferences("ca", Android.Content.FileCreationMode.Private)!;

    /// <summary>Where the navigation bar sits: "top" (the default) or "bottom".</summary>
    public static string Nav => Read("navPosition", NavChoices, "top");

    /// <summary>The chosen layout: "auto" (the default), "phone", "tablet" or "desktop".</summary>
    public static string Layout => Read("layoutMode", LayoutChoices, "auto");

    /// <summary>The text size: "normal" (the default), "large", "very-large" or "huge".</summary>
    public static string Text => Read("textScale", TextChoices, "normal");

    /// <summary>The text size as a percentage for the web views that show other people's pages (the chat and the terminal), from the same scale as the PocketIris page.</summary>
    public static int TextZoomPercent => Text switch { "large" => 115, "very-large" => 132, "huge" => 150, _ => 100 };

    /// <summary>The text size as a multiplier for the app's own native screens (the tab row, the browser bar, the scripts list).</summary>
    public static float TextScale => TextZoomPercent / 100f;

    /// <summary>The layout in use right now: the chosen one, or for "auto" the one that fits the view's width.</summary>
    public static string Effective => Layout == "auto" ? Tokens.Layout.ForWidth(viewWidthDp) : Layout;

    /// <summary>Reads one choice and falls back to the default when the saved value is not a known one.</summary>
    static string Read(string key, string[] allowed, string fallback)
    {
        var value = Prefs.GetString(key, fallback) ?? fallback;
        return Array.IndexOf(allowed, value) >= 0 ? value : fallback;
    }

    /// <summary>Tells the appearance how wide the content area is now, so "auto" can pick phone, tablet or desktop.</summary>
    /// <param name="widthDp">The width in dp.</param>
    public static void SetViewWidth(int widthDp)
    {
        //  Only a real change in the resulting layout needs everyone to redraw.
        var before = Effective;
        viewWidthDp = widthDp;
        //  Tell everyone only when the layout really changed.
        if (Effective != before) Changed?.Invoke();
    }

    /// <summary>Saves one choice, if the key and value are known ones.</summary>
    /// <param name="key">"nav", "layout" or "text".</param>
    /// <param name="value">One of that key's allowed values.</param>
    /// <returns>True when the choice was accepted.</returns>
    public static bool Set(string key, string value)
    {
        var (pref, allowed) = key switch
        {
            "nav" => ("navPosition", NavChoices),
            "layout" => ("layoutMode", LayoutChoices),
            "text" => ("textScale", TextChoices),
            _ => ("", Array.Empty<string>()),
        };

        //  Refuse anything that is not on the list, so the page can never store junk.
        if (pref.Length == 0 || Array.IndexOf(allowed, value) < 0) return false;
        Prefs.Edit()!.PutString(pref, value)!.Apply();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Which cards of the PocketIris page are open, as "id=1,id=0" (empty until the user opens or closes one), so the page looks the same on the next visit.</summary>
    public static string Cards => Prefs.GetString("openCards", "") ?? "";

    /// <summary>Remembers whether one card of the PocketIris page is open.</summary>
    /// <param name="id">The card's id: lower-case letters only.</param>
    /// <param name="open">True when the card is open.</param>
    public static void SetCard(string id, bool open)
    {
        //  Only short plain names are stored, so the page can never put anything else into the settings.
        if (id.Length is 0 or > 20 || !id.All(c => c is >= 'a' and <= 'z')) return;
        var cards = Cards.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith(id + "=")).Append(id + (open ? "=1" : "=0"));
        Prefs.Edit()!.PutString("openCards", string.Join(",", cards.TakeLast(10)))!.Apply();
    }

    /// <summary>The choices as JSON for the page (written by hand: the trimmed release build has no reflection-based JSON).</summary>
    public static string ToJson() =>
        new StringBuilder("{\"nav\":\"").Append(Nav).Append("\",\"layout\":\"").Append(Layout).Append("\",\"effective\":\"").Append(Effective)
            .Append("\",\"text\":\"").Append(Text).Append("\"}").ToString();
}

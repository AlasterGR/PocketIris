using Android.App;
using Android.Content;

namespace PocketIris;

/// <summary>
/// Remembers which PC last spoke to this phone and over which route ("cable" or "wireless"), so the Settings page can say
/// "Connected to device NAME" and be truthful about it. The PC tools say hello through <see cref="HelloReceiver"/>.
/// </summary>
public static class PcContact
{
    /// <summary>How long a hello counts as "connected" (minutes).</summary>
    const int FreshMinutes = 5;

    static ISharedPreferences Prefs => Application.Context.GetSharedPreferences("ca", FileCreationMode.Private)!;

    /// <summary>Stores a hello from a PC.</summary>
    /// <param name="pc">The PC's name (letters, digits, dash, underscore, dot and spaces only).</param>
    /// <param name="route">"cable" or "wireless".</param>
    public static void Record(string pc, string route)
    {
        //  Only the two known routes and plain names are stored, so nothing odd can reach the page.
        if (route is not ("cable" or "wireless") || pc.Length is 0 or > 60 || !pc.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')) return;
        Prefs.Edit()!.PutString("pc_" + route, pc)!.PutLong("pcAt_" + route, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!.Apply();
    }

    /// <summary>One short sentence about the latest hello over the given route, or what is missing.</summary>
    /// <param name="route">"cable" or "wireless".</param>
    public static string Line(string route)
    {
        var name = Prefs.GetString("pc_" + route, null);
        var at = Prefs.GetLong("pcAt_" + route, 0);
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at;

        //  Nobody has spoken over this route recently: say so plainly instead of claiming a connection.
        if (name == null) return "No device has connected this way yet.";
        return age > FreshMinutes * 60 ? $"Last connected to device {name}." : $"Connected to device {name}.";
    }
}

/// <summary>Receives the PC tools' hello. Android lets only the adb shell send to it (the manifest requires the DUMP permission).</summary>
[BroadcastReceiver(Name = "io.github.alastergr.pocketiris.HelloReceiver", Exported = true, Permission = "android.permission.DUMP")]
[IntentFilter(new[] { "io.github.alastergr.pocketiris.HELLO" })]
public sealed class HelloReceiver : BroadcastReceiver
{
    /// <summary>Stores the name and route the PC sent.</summary>
    public override void OnReceive(Context? context, Intent? intent) =>
        PcContact.Record(intent?.GetStringExtra("pc") ?? "", intent?.GetStringExtra("route") ?? "");
}

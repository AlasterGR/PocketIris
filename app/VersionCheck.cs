using System.Text.Json;

namespace PocketIris;

/// <summary>
/// Version awareness: compares what is installed in Termux (written by ca-versions to /sdcard/Documents/ca/versions.json) with the
/// known-good set (compat.json, pushed there by the PC tools). A difference gives a quiet line on the PocketIris page and one
/// notification per new set of versions. Whenever the installed versions change, a read-only health check runs (the self-tests that
/// need no dialog, plus "does the chat server answer" and "does the helper answer") and its result is shown with the versions.
/// </summary>
public static class VersionCheck
{
    public const string VersionsFile = "/sdcard/Documents/ca/versions.json";
    public const string CompatFile = "/sdcard/Documents/ca/compat.json";

    /// <summary>The parts that are compared, with the names the user sees.</summary>
    static readonly (string Key, string Name)[] Parts =
    [
        ("claudeCode", "Claude Code"), ("node", "Node"), ("npm", "npm"), ("yep", "Yep (chat server)"),
        ("termux", "Termux"), ("ttyd", "ttyd (terminal)"), ("openssh", "OpenSSH"),
    ];

    static Android.Content.ISharedPreferences Prefs =>
        Android.App.Application.Context.GetSharedPreferences("ca", Android.Content.FileCreationMode.Private)!;

    /// <summary>The comparison as "state|text": state is "ok", "mismatch" or "unknown"; the text says it in plain words, with the last health check.</summary>
    public static string Status()
    {
        var (state, text, _) = Compare();
        var health = Prefs.GetString("versionsHealth", "") ?? "";
        //  Without a known state or a health result there is nothing to add.
        if (state == "unknown" || health.Length == 0) return state + "|" + text;
        //  Add the last health check (it ran when the versions last changed), before an undo command so that one stays last.
        var undo = text.IndexOf(" To undo,", StringComparison.Ordinal);
        return state + "|" + (undo < 0 ? text + " " + health : text[..undo] + " " + health + text[undo..]);
    }

    /// <summary>Reads both files and compares them part by part.</summary>
    /// <returns>The state, a plain sentence, and a fingerprint of the installed versions (empty when unknown).</returns>
    static (string State, string Text, string Seen) Compare()
    {
        var installed = ReadStrings(VersionsFile, null);
        var known = ReadStrings(CompatFile, "knownGood");
        //  Without both files there is nothing to compare; say which one is missing.
        if (installed == null) return ("unknown", "Versions not recorded yet (Termux writes them when the server starts).", "");
        //  The known-good list comes from the PC; until it is on the phone there is nothing to compare with.
        if (known == null) return ("unknown", "The known-good list (compat.json) is not on the phone yet; the PC tools put it there.", "");

        var guard = GuardNotice(installed);
        //  The fingerprint includes the guard's last revert, so a new revert is told once even when the versions now match again.
        var seen = string.Join(",", Parts.Select(p => installed.GetValueOrDefault(p.Key, ""))) + "|" + installed.GetValueOrDefault("guardAt", "");
        var differences = new List<string>();

        //  A part counts as different only when both sides name a version and they are not the same.
        foreach (var (key, name) in Parts)
        {
            var have = installed.GetValueOrDefault(key, "");
            var want = known.GetValueOrDefault(key, "");
            //  Both sides must name a version for a difference to count.
            if (have.Length > 0 && want.Length > 0 && have != want) differences.Add($"{name} {have} (known good: {want})");
        }
        //  A recent automatic revert is worth the quiet line too, even when everything matches again because of it.
        if (differences.Count == 0 && guard.Length == 0) return ("ok", "All versions match the known-good set.", seen);
        var text = differences.Count == 0 ? "All versions match the known-good set." : "Changed since the last tested set: " + string.Join("; ", differences) + ".";
        //  The undo command goes last, so it can be copied without a stray full stop.
        if (guard.Length == 0) return ("mismatch", text, seen);
        var undo = guard.IndexOf(" To undo,", StringComparison.Ordinal);
        return ("mismatch", guard[..undo] + " " + text + guard[undo..], seen);
    }

    /// <summary>Says in words that the guard set a Claude Code version aside in the last 7 days, or returns an empty text.</summary>
    /// <param name="installed">The recorded versions, with guardAt ("yyyy-MM-dd HH:mm:ss", phone time), guardFrom and guardTo.</param>
    static string GuardNotice(Dictionary<string, string> installed)
    {
        var at = installed.GetValueOrDefault("guardAt", "");
        //  No revert recorded, an unreadable time, or one older than a week: nothing to say.
        if (!DateTime.TryParseExact(at, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when)
            || DateTime.Now - when > TimeSpan.FromDays(7)) return "";
        var from = installed.GetValueOrDefault("guardFrom", "");
        var to = installed.GetValueOrDefault("guardTo", "");
        return $"Claude Code {from} failed PocketIris's check on {when:d MMM HH:mm} and was set aside; {to} runs instead. To undo, run in the Terminal: ca-claude-version unrevert {from}";
    }

    /// <summary>Reads the text values of a JSON file (or of one object inside it) into a dictionary; null when the file is missing or broken.</summary>
    static Dictionary<string, string>? ReadStrings(string path, string? inside)
    {
        //  A missing or half-written file simply means "not known".
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = inside == null ? doc.RootElement : doc.RootElement.GetProperty(inside);
            var result = new Dictionary<string, string>();
            //  Keep only the plain text values; nested lists such as termuxPackages are not compared here.
            foreach (var p in root.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) result[p.Name] = p.Value.GetString() ?? "";
            return result;
        }
        catch (Exception problem) { Diag.Swallowed("VersionCheck.ReadStrings " + path, problem); return null; }
    }

    /// <summary>
    /// Compares the versions; when the installed set is new since the last look, runs the health check; when it also differs from the
    /// known-good set, posts one notification (never twice for the same set). Safe to call often: it only reads two small files otherwise.
    /// </summary>
    public static async Task Run()
    {
        var (state, text, seen) = Compare();
        //  Nothing is known yet, so there is nothing to check or tell.
        if (state == "unknown") return;

        //  Run the health check once per new set of installed versions.
        if (seen != Prefs.GetString("versionsSeen", ""))
        {
            var health = await HealthCheck();
            Prefs.Edit()!.PutString("versionsSeen", seen)!.PutString("versionsHealth", health)!.Apply();
            Android.Util.Log.Info("PocketIris", $"version check -> {state}: {text} {health}");
        }

        //  Tell the user once per new mismatching set; a matching set clears the memory so a later change is told again.
        if (state == "mismatch" && seen != Prefs.GetString("versionsNotified", ""))
        {
            AppNotifier.VersionsChanged(Status().Split('|', 2)[1]);
            Prefs.Edit()!.PutString("versionsNotified", seen)!.Apply();
        }
        else if (state == "ok") Prefs.Edit()!.Remove("versionsNotified")!.Apply();
    }

    /// <summary>The read-only health check: the slip and address self-tests, the chat server and the helper. Shows no dialog and changes nothing.</summary>
    /// <returns>One sentence, for example "Quick health check passed (4 of 4)."</returns>
    static async Task<string> HealthCheck()
    {
        var failed = new List<string>();
        //  Each check that does not pass is named, so the user knows where to look.
        if (!PageBridge.SlipTest().StartsWith("PASS")) failed.Add("permission slips");
        //  The address checks keep Claude's browser away from the phone itself and the home network.
        if (!PageBridge.GuardsPass()) failed.Add("address checks");
        //  The chat server must answer.
        if (!await MainActivity.ServerUp()) failed.Add("chat server");
        //  The sidecar must answer with the app's key.
        if ((await SidecarClient.Call("GET", "/status", "")).Status != 200) failed.Add("trash and notification helper");
        return failed.Count == 0
            ? "Quick health check passed (4 of 4)."
            : $"Quick health check: {4 - failed.Count} of 4 passed; not working: {string.Join(", ", failed)}.";
    }
}

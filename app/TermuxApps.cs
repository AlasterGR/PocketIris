using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;

namespace PocketIris;

/// <summary>
/// Updates for Termux and its add-on apps (API, Boot, Widget), which are Android apps rather than Termux packages. The check compares
/// each installed version with the latest non-beta GitHub release of its official repository. The update downloads the release's APK for
/// this phone, checks it against the SHA-256 the release publishes, and hands it to Android's installer, which always asks the user to
/// confirm (a normal app can never install silently). Android also refuses any APK signed by another key, so only an update from the same
/// source can replace an installed app. If installing cannot be done, the release page opens in the phone's browser instead.
/// </summary>
public static class TermuxApps
{
    /// <summary>The apps, with their official repositories and the names the user sees.</summary>
    public static readonly (string Package, string Repo, string Name)[] Apps =
    [
        ("com.termux", "termux/termux-app", "Termux"), ("com.termux.api", "termux/termux-api", "Termux:API"),
        ("com.termux.boot", "termux/termux-boot", "Termux:Boot"), ("com.termux.widget", "termux/termux-widget", "Termux:Widget"),
    ];

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    static ISharedPreferences Prefs => Application.Context.GetSharedPreferences("ca", FileCreationMode.Private)!;

    /// <summary>The last check and install, as JSON for the PocketIris page (written by hand; the release build has no reflection JSON).</summary>
    public static string Status() => Prefs.GetString("termuxApps", "{}") ?? "{}";

    /// <summary>True while a check or an install is running, so the page shows it and does not start a second one.</summary>
    public static volatile string Busy = "";

    /// <summary>The installed version of one app, or "" when it is not installed.</summary>
    static string Installed(Context c, string package)
    {
        //  A missing app simply has no version.
        try { return c.PackageManager!.GetPackageInfo(package, 0)!.VersionName ?? ""; } catch (Exception) { return ""; }
    }

    /// <summary>
    /// Asks GitHub for the latest non-beta release of each app and records, per app: installed, latest, the APK for this phone, the checksum
    /// file and the release page. Returns how many apps are behind.
    /// </summary>
    public static async Task<int> Check(Context c)
    {
        Busy = "checking";
        var rows = new List<string>();
        var behind = 0;
        //  One question to GitHub per app; an app whose release cannot be read keeps an empty "latest".
        try
        {
            foreach (var (package, repo, name) in Apps)
            {
                var installed = Installed(c, package);
                string latest = "", apk = "", apkUrl = "", sumsUrl = "", page = $"https://github.com/{repo}/releases";
                //  A failed request leaves this app unknown instead of stopping the others.
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
                    request.Headers.UserAgent.ParseAdd("PocketIris");
                    using var response = await Http.SendAsync(request);
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    latest = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v');
                    page = root.GetProperty("html_url").GetString() ?? page;
                    var assets = root.GetProperty("assets").EnumerateArray().Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? "")).ToList();
                    //  This phone is arm64: its own APK first, then one without any processor in its name (the add-ons ship only one).
                    var pick = assets.FirstOrDefault(a => a.Name.EndsWith(".apk") && a.Name.Contains("arm64-v8a"));
                    if (pick.Name is null or "") pick = assets.FirstOrDefault(a => a.Name.EndsWith(".apk") && !a.Name.Contains("armeabi") && !a.Name.Contains("x86") && !a.Name.Contains("universal"));
                    (apk, apkUrl) = (pick.Name ?? "", pick.Url ?? "");
                    sumsUrl = assets.FirstOrDefault(a => a.Name.Contains("sha256")).Url ?? "";
                }
                catch (Exception problem) { Diag.Swallowed("TermuxApps.Check " + repo, problem); }
                var isBehind = installed.Length > 0 && latest.Length > 0 && Newer(latest, installed);
                //  Count only installed apps that have a newer release.
                if (isBehind) behind++;
                rows.Add($"{PageBridge.Quote(package)}:{{\"name\":{PageBridge.Quote(name)},\"installed\":{PageBridge.Quote(installed)},\"latest\":{PageBridge.Quote(latest)},\"behind\":{(isBehind ? "true" : "false")},\"apk\":{PageBridge.Quote(apk)},\"apkUrl\":{PageBridge.Quote(apkUrl)},\"sumsUrl\":{PageBridge.Quote(sumsUrl)},\"page\":{PageBridge.Quote(page)}}}");
            }
            var json = $"{{\"checkedAt\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"behind\":{behind},\"apps\":{{{string.Join(",", rows)}}},\"last\":{PageBridge.Quote(Last())}}}";
            Prefs.Edit()!.PutString("termuxApps", json)!.Apply();
            //  Tell the user once per new set of releases, the same way as the other updates.
            if (behind > 0 && Prefs.GetString("termuxAppsNotified", "") != json.GetHashCode().ToString())
            {
                AppNotifier.UpdatesAvailable($"{behind} Termux app update{(behind == 1 ? "" : "s")} available", "Open PocketIris > Updates to install.");
                Prefs.Edit()!.PutString("termuxAppsNotified", json.GetHashCode().ToString())!.Apply();
            }
            return behind;
        }
        finally { Busy = ""; }
    }

    /// <summary>The once-a-month check, run when the app starts: only when the last one is older than 30 days.</summary>
    public static async Task CheckIfDue(Context c)
    {
        long last = 0;
        //  A missing or unreadable state counts as "never checked".
        try { using var doc = JsonDocument.Parse(Status()); if (doc.RootElement.TryGetProperty("checkedAt", out var t)) last = t.GetInt64(); } catch (Exception) { last = 0; }
        //  Not due yet: nothing to do.
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last < 30L * 86400) return;
        await Check(c);
    }

    /// <summary>
    /// Downloads, verifies and hands to Android's installer every app that is behind, one after another; each install shows Android's own
    /// confirmation. Returns false when one could not be prepared, so the caller can offer the release page instead.
    /// </summary>
    /// <param name="a">The activity that hands the APK to Android.</param>
    /// <param name="forcePackage">For the self-test only: install this one app's current release even though it is not behind.</param>
    public static async Task<bool> InstallBehind(Activity a, string? forcePackage = null)
    {
        Busy = "installing";
        //  Everything below reports what happened through the "last" line the page shows.
        try
        {
            using var doc = JsonDocument.Parse(Status());
            if (!doc.RootElement.TryGetProperty("apps", out var apps)) { SetLast("Check first; nothing is known yet."); return false; }
            foreach (var app in apps.EnumerateObject())
            {
                var v = app.Value;
                //  Only apps with a newer release are installed (or the one app the self-test names).
                if (forcePackage != null ? app.Name != forcePackage : !v.GetProperty("behind").GetBoolean()) continue;
                var name = v.GetProperty("name").GetString()!;
                var apkUrl = v.GetProperty("apkUrl").GetString() ?? "";
                var apkName = v.GetProperty("apk").GetString() ?? "";
                var sumsUrl = v.GetProperty("sumsUrl").GetString() ?? "";
                //  Without a matching APK and checksum list nothing can be verified, so nothing is installed.
                if (apkUrl.Length == 0 || sumsUrl.Length == 0) { SetLast($"{name}: the release has no APK for this phone or no checksum list."); return false; }
                SetLast($"Downloading {name} {v.GetProperty("latest").GetString()}…");
                var bytes = await Http.GetByteArrayAsync(apkUrl);
                var sums = await Http.GetStringAsync(sumsUrl);
                var have = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var want = sums.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(apkName))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant() ?? "";
                //  The download must match the checksum the release publishes, or it is not offered for install.
                if (want.Length == 0 || want != have) { SetLast($"{name}: the download does not match the release's SHA-256; not installed."); return false; }
                Install(a, app.Name, bytes);
                SetLast($"{name}: verified; Android is asking you to confirm the install.");
            }
            return true;
        }
        catch (Exception problem) { Diag.Swallowed("TermuxApps.InstallBehind", problem); SetLast("Could not prepare the install: " + problem.Message); return false; }
        finally { Busy = ""; }
    }

    /// <summary>Gives one verified APK to Android's package installer; Android answers through <see cref="InstallResult"/>, first with "ask the user".</summary>
    static void Install(Activity a, string package, byte[] apk)
    {
        var installer = a.PackageManager!.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(package);
        var id = installer.CreateSession(parameters);
        using var session = installer.OpenSession(id);
        using (var output = session.OpenWrite("base.apk", 0, apk.Length))
        {
            output.Write(apk, 0, apk.Length);
            session.Fsync(output);
        }
        var answer = new Intent(a, typeof(InstallResult)).PutExtra("package", package);
        var pending = PendingIntent.GetBroadcast(a, id, answer, PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent)!;
        session.Commit(pending.IntentSender);
    }

    /// <summary>Remembers the latest message for the page (kept in the state, so it survives reopening the app).</summary>
    public static void SetLast(string text)
    {
        var state = Status();
        var marker = "\"last\":";
        var at = state.LastIndexOf(marker, StringComparison.Ordinal);
        //  Replace the "last" field when the state has one; otherwise start a small state with only the message.
        var json = at < 0 ? $"{{\"last\":{PageBridge.Quote(text)}}}" : state[..(at + marker.Length)] + PageBridge.Quote(text) + "}";
        Prefs.Edit()!.PutString("termuxApps", json)!.Apply();
    }

    /// <summary>The latest message, or "".</summary>
    static string Last()
    {
        //  An unreadable state has no message.
        try { using var doc = JsonDocument.Parse(Status()); return doc.RootElement.TryGetProperty("last", out var l) ? l.GetString() ?? "" : ""; } catch (Exception) { return ""; }
    }

    /// <summary>True when version a (such as 0.119.0) is newer than version b.</summary>
    static bool Newer(string a, string b)
    {
        var x = a.Split('.', '-', '+').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var y = b.Split('.', '-', '+').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        //  Compare part by part; the first difference decides.
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var (p, q) = (i < x.Length ? x[i] : 0, i < y.Length ? y[i] : 0);
            if (p != q) return p > q;
        }
        return false;
    }
}

/// <summary>Android's answers about an install: first "ask the user" (its confirmation screen is shown), then success or failure.</summary>
[BroadcastReceiver(Name = "io.github.alastergr.pocketiris.InstallResult", Exported = false)]
public sealed class InstallResult : BroadcastReceiver
{
    /// <summary>Shows Android's confirmation when asked, and records how the install ended.</summary>
    public override void OnReceive(Context? context, Intent? intent)
    {
        //  Without the context or the answer there is nothing to do.
        if (context == null || intent == null) return;
        var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, -999);
        var id = intent.GetStringExtra("package") ?? "";
        var package = TermuxApps.Apps.FirstOrDefault(a => a.Package == id).Name ?? "the app";
        //  Android needs the user's own confirmation: show its screen.
        if (status == (int)PackageInstallStatus.PendingUserAction)
        {
#pragma warning disable CA1422, CS0618
            var confirm = (Intent?)intent.GetParcelableExtra(Intent.ExtraIntent);
#pragma warning restore CA1422, CS0618
            //  Android always includes the screen to show; if not, say so.
            if (confirm != null) { confirm.AddFlags(ActivityFlags.NewTask); context.StartActivity(confirm); }
            else TermuxApps.SetLast($"{package}: Android did not offer its confirmation screen.");
            return;
        }
        var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? "";
        TermuxApps.SetLast(status == (int)PackageInstallStatus.Success ? $"{package} installed. Press Check now to confirm." : $"{package} was not installed: {message}");
    }
}

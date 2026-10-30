using Android.Webkit;
using Java.Interop;

namespace PocketIris;

/// <summary>
/// The bridge the Settings page uses to talk to the app (it appears in the page as <c>window.PocketIris</c>): the safety switches,
/// the debugging link, the trusted session, the Android settings shortcuts and the self-tests of the app's own safety features
/// The self-tests only show mock dialogs and report what happened; nothing is downloaded or changed.
/// </summary>
public sealed class PageBridge(MainActivity activity, WebView web) : Java.Lang.Object
{
    /// <summary>Runs one test in the background and sends its report back to the page.</summary>
    /// <param name="name">The test to run: info, dialog, fingerprint, slip or guards.</param>
    [JavascriptInterface, Export("runTest")]
    public void RunTest(string name)
    {
        Android.Util.Log.Info("PocketIris", "self-test requested: " + name);
        _ = Task.Run(async () =>
        {
            string report;

            //  A test that crashes still reports the problem instead of leaving the page waiting.
            try { report = await Run(name); }
            catch (Exception e) { report = "Test crashed: " + e; }
            Android.Util.Log.Info("PocketIris", "self-test " + name + " -> " + report);
            var js = $"window.__pcTestResult&&window.__pcTestResult({Quote(name)},{Quote(report)})";
            activity.RunOnUiThread(() => web.EvaluateJavascript(js, null));
        });
    }

    /// <summary>Reads one of the app's switches for the Settings page.</summary>
    /// <param name="name">"fingerprint" (the fingerprint shortcut) or "debug" (page debugging from the PC).</param>
    [JavascriptInterface, Export("getFlag")]
    public bool GetFlag(string name) => name == "fingerprint" ? Approval.FingerprintAllows : name == "control" ? MainActivity.PhoneControlEnabled : name == "debug" && activity.IsDebug();

    /// <summary>Changes the debugging link (after the dialog and Android's lock check for anything but Off), then reports back to the page.</summary>
    /// <param name="mode">"off", "wifi", "wifibt" or "bt".</param>
    [JavascriptInterface, Export("linkSet")]
    public void LinkSet(string mode)
    {
        _ = Task.Run(async () =>
        {
            //  Run the change; a problem is reported as text instead of leaving the page waiting.
            string text;
            //  A problem is shown as text instead of leaving the page waiting.
            try { text = await BluetoothDebug.SetMode(activity, BluetoothDebug.Parse(mode)); }
            catch (Exception e) { text = "Could not switch: " + e.Message; }
            var js = $"window.__pcLink&&window.__pcLink({Quote(BluetoothDebug.Name(BluetoothDebug.Mode))},{Quote(text)})";
            activity.RunOnUiThread(() => web.EvaluateJavascript(js, null));
        });
    }

    /// <summary>Returns the Cable switch as "1|text" (on) or "0|text" (off), so the page can show it.</summary>
    [JavascriptInterface, Export("cableStatus")]
    public string CableStatus() => (UsbCable.IsOn(activity) ? "1|" : "0|") + UsbCable.StatusText(activity);

    /// <summary>Switches the Cable way on or off (asking first when Android's USB debugging must change), then tells the page.</summary>
    /// <param name="on">True for On.</param>
    [JavascriptInterface, Export("cableSet")]
    public void CableSet(bool on)
    {
        _ = Task.Run(async () =>
        {
            //  A problem must not leave the page waiting, so any failure is only logged and the page re-reads the real state.
            try { await UsbCable.Set(activity, on); }
            catch (Exception e) { Diag.Swallowed("PageBridge:cableSet", e); }
            activity.RunOnUiThread(() => web.EvaluateJavascript("window.__pcCable&&window.__pcCable()", null));
        });
    }

    /// <summary>Returns the current mode and a sentence about it, as "mode|text" (for example "wifi|On (wifi)…"), so the page can show it.</summary>
    [JavascriptInterface, Export("linkStatus")]
    public string LinkStatus() => BluetoothDebug.Name(BluetoothDebug.Mode) + "|" + BluetoothDebug.StatusText + (BluetoothDebug.Enabled ? " " + PcContact.Line("wireless") : "");

    /// <summary>Opens one of a few Android settings pages (for Bluetooth debugging); any other name is ignored.</summary>
    /// <param name="name">"tether" (tethering and hotspot), "developer" (developer options) or "bluetooth".</param>
    [JavascriptInterface, Export("openSettings")]
    public void OpenSettings(string name)
    {
        //  Pick the settings page by name; nothing else can be opened from here.
        var action = name switch
        {
            "tether" => "android.settings.TETHER_SETTINGS",
            "developer" => Android.Provider.Settings.ActionApplicationDevelopmentSettings,
            "bluetooth" => Android.Provider.Settings.ActionBluetoothSettings,
            _ => null,
        };
        //  Ignore a page name that is not on the list.
        if (action == null) return;
        activity.RunOnUiThread(() =>
        {
            //  Some phones have no such page; the user then opens it by hand.
            try { activity.StartActivity(new Android.Content.Intent(action)); } catch (Exception swallowed) { Diag.Swallowed("PageBridge:75", swallowed); }
        });
    }

    /// <summary>Opens Android's accessibility settings so the user can switch the PocketIris service on.</summary>
    [JavascriptInterface, Export("openAccessibility")]
    public void OpenAccessibility() => activity.RunOnUiThread(activity.OpenAccessibilitySettings);

    /// <summary>
    /// Changes one of the app's switches from the Settings page. Turning a switch OFF needs no question (it can only make the app
    /// safer). Turning one ON first asks the user in a native dialog; a touch on the fingerprint sensor is enough, and a web page
    /// cannot touch the sensor, so nothing a page does can switch a safety feature on by itself.
    /// </summary>
    /// <param name="name">"fingerprint", "control" or "debug".</param>
    /// <param name="on">The new value.</param>
    [JavascriptInterface, Export("setFlag")]
    public void SetFlag(string name, bool on)
    {
        //  Switching off is always allowed.
        if (!on) { activity.RunOnUiThread(() => Apply(name, false)); return; }

        //  Switching on asks first, and then tells the page to read the switches again, whatever the answer was.
        _ = Task.Run(async () =>
        {
            var (title, what) = name switch
            {
                "fingerprint" => ("Turn on the fingerprint shortcut?", "While an approval question is open, touching the fingerprint sensor will count as Allow."),
                "control" => ("Turn on phone control?", "Lets Claude read the screen and tap, type and scroll in other apps that you approve. Android's accessibility switch for PocketIris must also be on."),
                "debug" => ("Turn on page debugging from the PC?", "Lets a PC with a debugging connection inspect and drive the chat page. Turn it off when you are not developing."),
                _ => ("", ""),
            };

            //  Ignore names that are not one of the three switches.
            if (title.Length == 0) return;
            var answer = await Approval.Ask(activity, title, what, allowLabel: "Turn on", allowDelegate: false, fingerprintAllows: true);

            //  Only a real "yes" changes the switch.
            if (answer == Decision.AllowOnce) activity.RunOnUiThread(() => Apply(name, true));
            activity.RunOnUiThread(() => web.EvaluateJavascript("window.__pcFlags&&window.__pcFlags()", null));
        });
    }

    /// <summary>Applies one switch change (must run on the screen thread, because it touches the web view settings).</summary>
    void Apply(string name, bool on)
    {
        //  Set the switch that was named.
        if (name == "fingerprint") activity.SetFingerprintAllows(on);
        else if (name == "control") activity.SetPhoneControl(on);
        else if (name == "debug") activity.SetDebug(on);
    }

    /// <summary>Starts a trusted session after asking the user once, then tells the page to read the state again.</summary>
    [JavascriptInterface, Export("trustStart")]
    public void TrustStart()
    {
        _ = Task.Run(async () =>
        {
            await TrustedSession.Start(activity);
            activity.RunOnUiThread(() => web.EvaluateJavascript("window.__pcTrust&&window.__pcTrust()", null));
        });
    }

    /// <summary>Ends the trusted session now.</summary>
    [JavascriptInterface, Export("trustEnd")]
    public void TrustEnd() => TrustedSession.End();

    /// <summary>Returns "1|text" while a trusted session is on and "0|text" otherwise, for the Settings page.</summary>
    [JavascriptInterface, Export("trustStatus")]
    public string TrustStatus() => (TrustedSession.Active ? "1|" : "0|") + TrustedSession.StatusText;

    /// <summary>Returns the app's own version, for the "About" line of the Settings page (for example "0.2.0 (build 2)").</summary>
    [JavascriptInterface, Export("appVersion")]
    public string AppVersion()
    {
        //  If the phone cannot say, show a dash instead of failing.
        try
        {
            var info = activity.PackageManager!.GetPackageInfo(activity.PackageName!, 0)!;
            return $"{info.VersionName} (build {info.LongVersionCode})";
        }
        catch (Exception swallowed) { Diag.Swallowed("PageBridge.AppVersion", swallowed); return "-"; }
    }

    /// <summary>Returns the appearance choices as JSON: navigation bar position, layout, the layout in use, and text size.</summary>
    [JavascriptInterface, Export("getAppearance")]
    public string GetAppearance() => Appearance.ToJson();

    /// <summary>Saves one appearance choice; the app redraws itself and tells the page.</summary>
    /// <param name="key">"nav", "layout" or "text".</param>
    /// <param name="value">One of that key's allowed values.</param>
    [JavascriptInterface, Export("setAppearance")]
    public void SetAppearance(string key, string value) => Appearance.Set(key, value);

    /// <summary>Returns the update state Termux keeps (what is installed, what is newest, when it was checked, a running update, the last result) as JSON.</summary>
    [JavascriptInterface, Export("updatesStatus")]
    public string UpdatesStatus()
    {
        //  No file yet just means nothing was checked so far.
        try { return File.Exists(UpdatesFile) ? File.ReadAllText(UpdatesFile) : "{}"; }
        catch (Exception swallowed) { Diag.Swallowed("PageBridge.UpdatesStatus", swallowed); return "{}"; }
    }

    const string UpdatesFile = "/sdcard/Documents/ca/updates.json";
    static readonly string[] UpdateParts = ["claude", "yep", "pkgs"];

    /// <summary>Asks Termux to look for updates of one part now (a notice only; nothing is installed).</summary>
    /// <param name="what">"claude", "yep" or "pkgs".</param>
    [JavascriptInterface, Export("checkUpdate")]
    public void CheckUpdate(string what)
    {
        //  Only the three known parts can be checked.
        if (Array.IndexOf(UpdateParts, what) < 0) return;
        activity.Termux.Run($"{TermuxRunner.Home}/bin/ca-updates check {what}");
    }

    /// <summary>
    /// Installs the update of one part after a native question: an update changes code, so a page alone can never start one.
    /// Termux then installs it, checks health and rolls back on failure; the page follows the progress through <see cref="UpdatesStatus"/>.
    /// </summary>
    /// <param name="what">"claude", "yep" or "pkgs".</param>
    /// <param name="summary">What the page shows about it (for example "2.1.312 to 2.1.313"), repeated in the question.</param>
    [JavascriptInterface, Export("runUpdate")]
    public void RunUpdate(string what, string summary)
    {
        //  Only the three known parts can be updated.
        if (Array.IndexOf(UpdateParts, what) < 0) return;
        _ = Task.Run(async () =>
        {
            var (title, detail) = what switch
            {
                "claude" => ("Update Claude Code?", "Claude Code " + summary + ". Running sessions keep the old version until they restart; if the new one fails PocketIris's check, the previous one comes back."),
                "yep" => ("Update the chat server (Yep)?", "Yep " + summary + ". The chat is unavailable for about two minutes. Its data is saved first; if the new version fails, the old one and its data come back."),
                _ => ("Upgrade the Termux packages?", summary + ". A list of what is installed is saved first. Packages cannot be rolled back, so the chat server is restarted and checked afterwards."),
            };
            var answer = await Approval.Ask(activity, title, detail, allowLabel: "Update", allowDelegate: false, fingerprintAllows: true);
            //  Only a real "yes" starts it.
            if (answer == Decision.AllowOnce) activity.Termux.Run($"{activity.Termux.Env()} {TermuxRunner.Home}/bin/ca-updates run {what}");
            activity.RunOnUiThread(() => web.EvaluateJavascript("window.__pcUpdates&&window.__pcUpdates()", null));
        });
    }

    /// <summary>The Termux apps' installed versions (read fresh from Android), the last GitHub check and install message, and whether one is running, as JSON.</summary>
    [JavascriptInterface, Export("termuxAppsStatus")]
    public string TermuxAppsStatus()
    {
        var installed = string.Join(",", PocketIris.TermuxApps.Apps.Select(a =>
        {
            var version = "";
            //  A missing app is simply shown as not installed.
            try { version = activity.PackageManager!.GetPackageInfo(a.Package, 0)!.VersionName ?? ""; } catch (Exception) { version = ""; }
            return Quote(a.Package) + ":" + Quote(version);
        }));
        return $"{{\"installed\":{{{installed}}},\"busy\":{Quote(PocketIris.TermuxApps.Busy)},\"state\":{PocketIris.TermuxApps.Status()}}}";
    }

    /// <summary>Compares the Termux apps with their latest non-beta GitHub releases (a notice only; nothing is installed).</summary>
    [JavascriptInterface, Export("checkTermuxApps")]
    public void CheckTermuxApps() => _ = Task.Run(async () =>
    {
        await PocketIris.TermuxApps.Check(activity);
        activity.RunOnUiThread(() => web.EvaluateJavascript("window.__pcUpdates&&window.__pcUpdates()", null));
    });

    /// <summary>
    /// Installs the Termux apps that are behind, after a native question: each APK is downloaded, checked against the release's SHA-256 and
    /// handed to Android's installer, which asks again. When that cannot be done, the release page opens in the phone's own browser.
    /// </summary>
    /// <param name="summary">What the page shows (for example "Termux:API 0.53.0 to 0.54.0"), repeated in the question.</param>
    [JavascriptInterface, Export("installTermuxApps")]
    public void InstallTermuxApps(string summary) => _ = Task.Run(async () =>
    {
        var answer = await Approval.Ask(activity, "Update the Termux apps?", summary + ". Each download is checked against the release's SHA-256, then Android asks you to confirm the install. " +
            "Updating Termux itself stops the chat server for a moment; PocketIris starts it again.", allowLabel: "Update", allowDelegate: false, fingerprintAllows: true);
        //  Only a real "yes" starts it; when the install cannot be prepared, the release page opens so it can be done by hand.
        if (answer == Decision.AllowOnce && !await PocketIris.TermuxApps.InstallBehind(activity)) OpenFirstReleasePage();
        activity.RunOnUiThread(() => web.EvaluateJavascript("window.__pcUpdates&&window.__pcUpdates()", null));
    });

    /// <summary>Opens the release page of the first app that is behind in the phone's own browser, which can download and install an APK (the Browser tab cannot).</summary>
    void OpenFirstReleasePage()
    {
        //  The page address comes from GitHub's answer; only github.com pages are opened.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(PocketIris.TermuxApps.Status());
            var page = doc.RootElement.GetProperty("apps").EnumerateObject().Select(a => a.Value).FirstOrDefault(v => v.GetProperty("behind").GetBoolean()).GetProperty("page").GetString() ?? "";
            if (!page.StartsWith("https://github.com/", StringComparison.Ordinal)) return;
            var view = new Android.Content.Intent(Android.Content.Intent.ActionView, Android.Net.Uri.Parse(page)).AddFlags(Android.Content.ActivityFlags.NewTask);
            activity.RunOnUiThread(() => activity.StartActivity(view));
        }
        catch (Exception swallowed) { Diag.Swallowed("PageBridge.OpenFirstReleasePage", swallowed); }
    }

    /// <summary>Opens one part's official project page in the Browser tab (the card's titles). The addresses are fixed here, never taken from the page.</summary>
    /// <param name="what">"claude", "yep", "pkgs" or "apps".</param>
    [JavascriptInterface, Export("openProject")]
    public void OpenProject(string what)
    {
        var url = what switch
        {
            "claude" => "https://github.com/anthropics/claude-code",
            "yep" => "https://github.com/kzahel/yepanywhere",
            "pkgs" => "https://github.com/termux/termux-packages",
            "apps" => "https://github.com/termux/termux-app/releases",
            _ => "",
        };
        //  Unknown names open nothing.
        if (url.Length > 0) activity.RunOnUiThread(() => activity.OpenInBrowser(url));
    }

    /// <summary>Returns the version comparison as "state|text" (state "ok", "mismatch" or "unknown"); see <see cref="VersionCheck"/>.</summary>
    [JavascriptInterface, Export("versionStatus")]
    public string VersionStatus() => VersionCheck.Status();

    /// <summary>Returns which cards of the page were open last time, as "id=1,id=0" (empty when nothing was remembered yet).</summary>
    [JavascriptInterface, Export("getCards")]
    public string GetCards() => Appearance.Cards;

    /// <summary>Remembers that one card of the page was opened or closed.</summary>
    /// <param name="id">The card's id.</param>
    /// <param name="open">True when it is open now.</param>
    [JavascriptInterface, Export("setCard")]
    public void SetCard(string id, bool open) => Appearance.SetCard(id, open);

    /// <summary>
    /// Says why a control is not available right now, in plain words, or returns an empty text when it is available.
    /// </summary>
    /// <param name="what">"control" (phone control) or "link" (the debugging link).</param>
    [JavascriptInterface, Export("reason")]
    public string Reason(string what)
    {
        //  Phone control needs Android's own accessibility switch for PocketIris.
        if (what == "control")
            return PhoneControlService.Instance == null ? "Android's accessibility switch for PocketIris is off. Switch it on in Android Settings > Accessibility, then come back." : "";

        //  The debugging link needs a one-time permission that only the PC can grant.
        if (what == "link")
            return BluetoothDebug.HasPermission(activity) ? "" : "A one-time permission is missing. On the PC run: adb shell pm grant io.github.alastergr.pocketiris android.permission.WRITE_SECURE_SETTINGS";
        return "";
    }

    /// <summary>
    /// Relays one request of the page to the sidecar (which holds trash and restore) and sends the answer back to the page
    /// through window.__pcSidecar(id, status, text). Only a short list of requests is allowed (see <see cref="SidecarClient"/>).
    /// </summary>
    /// <param name="id">The page's number for this call, returned with the answer.</param>
    /// <param name="method">"GET" or "POST".</param>
    /// <param name="path">The sidecar path.</param>
    /// <param name="body">The JSON body, or empty.</param>
    [JavascriptInterface, Export("sidecar")]
    public void Sidecar(string id, string method, string path, string body)
    {
        _ = Task.Run(async () =>
        {
            var (status, text) = await SidecarClient.Call(method, path, body);
            var js = $"window.__pcSidecar&&window.__pcSidecar({Quote(id)},{status},{Quote(text)})";
            activity.RunOnUiThread(() => web.EvaluateJavascript(js, null));
        });
    }

    /// <summary>Lets the page check that it is running inside the app.</summary>
    [JavascriptInterface, Export("available")]
    public bool Available() => true;

    /// <summary>Turns text into a JavaScript string literal (the release build is trimmed, so the reflection-based JSON writer is not available).</summary>
    public static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder("\"");

        //  Escape every character that could break out of the string.
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "", '<' => "\\u003c", _ when c < 32 => " ", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    /// <summary>Runs the named test and returns a one-paragraph report.</summary>
    async Task<string> Run(string name)
    {
        //  Pick the test by name; an unknown name gets a plain message.
        switch (name)
        {
            case "info":
                return Approval.SensorReport(activity) + $"; app {activity.PackageName}; fingerprint-to-allow switch: {Approval.FingerprintAllows}; phone control switch: {MainActivity.PhoneControlEnabled}; accessibility service running: {PhoneControl.ServiceConnected}";

            case "dialog":
            {
                var t0 = DateTime.UtcNow;
                var d = await Approval.Ask(activity, "TEST: mock approval", "This is only a test. Choose any button, or touch the sensor.", offerAlways: true);
                return $"You chose: {d}. Sensor: {Approval.LastAcceptedTouches} accepted, {Approval.LastRejectedTouches} rejected touches. Took {(DateTime.UtcNow - t0).TotalSeconds:0.0} s. " +
                       (d == Decision.AllowOnce && Approval.LastAcceptedTouches > 0 ? "Fingerprint allowed it." : "");
            }

            case "fingerprint":
            {
                var d = await Approval.Ask(activity, "TEST: wrong finger, then right finger",
                    "Step 1: touch the sensor with a finger that is NOT enrolled (the dialog must stay open).\nStep 2: touch with an enrolled finger (it must close and say Allow).\nOr press Deny to stop.", allowDelegate: false);
                var wrongOk = Approval.LastRejectedTouches > 0;
                return $"Result: {d}. Rejected touches (wrong finger): {Approval.LastRejectedTouches}. Accepted touches (right finger): {Approval.LastAcceptedTouches}. " +
                       (d == Decision.AllowOnce && Approval.LastAcceptedTouches > 0
                           ? (wrongOk ? "PASS: a wrong finger did not unlock it, the right finger did." : "Right finger worked; no wrong finger was tried, so the rejection part is unproven.")
                           : "Not allowed by fingerprint.") +
                       (Approval.LastSensorError.Length > 0 ? $" Sensor message: {Approval.LastSensorError}." : "");
            }

            case "slip":
                return SlipTest();

            case "termuxinstall":
            {
                //  The Termux-apps update path end to end, on the smallest app and its current release: GitHub, download, SHA-256, Android's installer.
                await PocketIris.TermuxApps.Check(activity);
                var ok = await PocketIris.TermuxApps.InstallBehind(activity, "com.termux.boot");
                return (ok ? "Prepared and verified; Android is asking you to confirm. " : "Not prepared. ") + "Details in the Updates card's Termux apps line.";
            }

            case "network":
            {
                var sb = new System.Text.StringBuilder("Network addresses of this phone: ");

                //  List every network connection that is up, with its ordinary (IPv4) address.
                foreach (var item in NetUtil.DescribeAll()) sb.Append(item + "; ");
                sb.Append("debugging over the network (adb): ");

                //  Check whether the phone is listening for adb over the network on the usual port.
                try
                {
                    using var c = new System.Net.Sockets.TcpClient();
                    var open = await Task.WhenAny(c.ConnectAsync("127.0.0.1", 5555), Task.Delay(800));
                    sb.Append(c.Connected ? "port 5555 is open." : "port 5555 is closed (switch on Wireless debugging in Developer options).");
                }
                catch { sb.Append("port 5555 is closed (switch on Wireless debugging in Developer options)."); }
                return sb.ToString();
            }

            case "guards":
            {
                var cases = new[] { "127.0.0.1", "localhost", "[::1]", "192.168.1.5", "10.0.0.2", "github.com", "example.com" };
                var lines = cases.Select(h => $"{h}: loopback={BrowserHost.IsLoopbackHost(h)}, private={BrowserHost.IsBlockedHost(h)}");
                return "Address checks: " + string.Join("; ", lines);
            }
        }
        return "Unknown test: " + name;
    }

    /// <summary>The "Let Claude decide" slip test: a slip works once, only for its own request, and an invented one never works. The report starts with PASS or FAIL.</summary>
    internal static string SlipTest()
    {
        var text = Approval.Delegate("test:key", "mock decision");
        var token = text.Split("delegated_token=\"")[1].Split('"')[0];
        var wrongKey = Approval.Redeem(token, "other:key");
        var first = Approval.Redeem(token, "test:key");
        var second = Approval.Redeem(token, "test:key");
        var junk = Approval.Redeem("not-a-token", "test:key");
        var pass = !wrongKey && first && !second && !junk;
        return $"{(pass ? "PASS" : "FAIL")}: wrong request key accepted: {wrongKey} (must be False); right key first use: {first} (True); reused: {second} (False); invented token: {junk} (False).";
    }

    /// <summary>The address test as a yes or no: the phone itself counts as local and private, home-network addresses as private, public sites as neither.</summary>
    internal static bool GuardsPass() =>
        new[] { "127.0.0.1", "localhost", "[::1]" }.All(h => BrowserHost.IsLoopbackHost(h) && BrowserHost.IsBlockedHost(h))
        && new[] { "192.168.1.5", "10.0.0.2" }.All(h => !BrowserHost.IsLoopbackHost(h) && BrowserHost.IsBlockedHost(h))
        && new[] { "github.com", "example.com" }.All(h => !BrowserHost.IsLoopbackHost(h) && !BrowserHost.IsBlockedHost(h));
}

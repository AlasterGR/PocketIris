using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Biometrics;
using Android.OS;
using Android.Provider;
using Android.Util;

namespace PocketIris;

/// <summary>How the PC is allowed to debug this phone (adb) when the USB cable is not used.</summary>
public enum LinkMode
{
    /// <summary>Nothing is opened.</summary>
    Off,
    /// <summary>Over the home Wi-Fi: PC and phone on the same network.</summary>
    Wifi,
    /// <summary>Over the Bluetooth network, with the phone also on Wi-Fi (Android's Wireless debugging needs that).</summary>
    WifiBluetooth,
    /// <summary>Over the Bluetooth network only, using adb's classic network port 5555 (set once per phone restart over USB).</summary>
    BluetoothOnly,
}

/// <summary>
/// The "Debugging link" choice in Settings > Development: lets the user's PC debug this phone (adb) without the cable.
/// Choosing any mode other than Off needs the user's answer in the app's Allow/Deny dialog AND Android's own lock screen check
/// (fingerprint, PIN or pattern). Depending on the mode it switches Android's Wireless debugging on and/or Bluetooth tethering on,
/// finds the debugging port, rings a "doorbell" so a PC watcher can start by itself, and answers the PC's question "which port?"
/// on a small status port that only the matching network (Wi-Fi or Bluetooth) can reach. Choosing Off restores everything.
/// It is always Off after the app restarts. Needs a one-time permission grant over adb (for the Wi-Fi modes):
/// adb shell pm grant io.github.alastergr.pocketiris android.permission.WRITE_SECURE_SETTINGS
/// </summary>
public static class BluetoothDebug
{
    public const int StatusPort = 3499;
    const string WirelessSetting = "adb_wifi_enabled";

    static TcpListener? listener;
    static CancellationTokenSource? cts;

    /// <summary>The mode that is on right now.</summary>
    public static LinkMode Mode { get; private set; } = LinkMode.Off;

    /// <summary>True while any mode other than Off is on.</summary>
    public static bool Enabled => Mode != LinkMode.Off;

    /// <summary>A plain sentence about the current state, shown under the choice.</summary>
    public static string StatusText { get; private set; } = "Off.";

    /// <summary>The short name the web page and the PC use for a mode.</summary>
    public static string Name(LinkMode m) => m switch { LinkMode.Wifi => "wifi", LinkMode.WifiBluetooth => "wifibt", LinkMode.BluetoothOnly => "bt", _ => "off" };

    /// <summary>Turns the short name back into a mode (anything unknown means Off).</summary>
    public static LinkMode Parse(string name) => name switch { "wifi" => LinkMode.Wifi, "wifibt" => LinkMode.WifiBluetooth, "bt" => LinkMode.BluetoothOnly, _ => LinkMode.Off };

    /// <summary>True if the app already holds the permission to switch Wireless debugging.</summary>
    public static bool HasPermission(Context c) =>
        c.CheckSelfPermission("android.permission.WRITE_SECURE_SETTINGS") == Permission.Granted;

    /// <summary>
    /// At app start: if a mode was on when the app last stopped, put Wireless debugging back to how it was before,
    /// so the link is always off after a restart.
    /// </summary>
    public static void RestoreAfterRestart(MainActivity a)
    {
        var prefs = a.GetSharedPreferences("ca", FileCreationMode.Private)!;

        //  Nothing to do when the link was off at the last stop.
        if (!prefs.Contains("btdebugPrevious")) return;
        var previous = prefs.GetInt("btdebugPrevious", 0);
        prefs.Edit()!.Remove("btdebugPrevious")!.Apply();

        //  Put the setting back; without the permission it simply stays as it is.
        try { Settings.Global.PutInt(a.ContentResolver, WirelessSetting, previous); } catch (Exception swallowed) { Diag.Swallowed("BluetoothDebug:78", swallowed); }
    }

    /// <summary>Changes the link to the wanted mode: Off turns everything off; another mode first turns the current one off.</summary>
    /// <param name="a">The activity that is on screen.</param>
    /// <param name="wanted">The mode the user picked.</param>
    /// <returns>A sentence saying what happened.</returns>
    public static async Task<string> SetMode(MainActivity a, LinkMode wanted)
    {
        //  Choosing Off, or choosing the mode that is already on, is simple.
        if (wanted == LinkMode.Off) return await TurnOff(a);
        //  Nothing to change when the link is already in the wanted mode.
        if (wanted == Mode) return StatusText;

        //  Switching from one mode to another: turn the old one off first.
        if (Enabled) await TurnOff(a);
        return await TurnOn(a, wanted);
    }

    /// <summary>Asks the user (dialog, then Android's lock screen check) and, if both agree, switches the mode on.</summary>
    static async Task<string> TurnOn(MainActivity a, LinkMode mode)
    {
        bool usesWireless = mode != LinkMode.BluetoothOnly;
        bool usesBluetooth = mode != LinkMode.Wifi;

        //  The Wi-Fi modes need the one-time permission, so explain how to grant it when it is missing.
        if (usesWireless && !HasPermission(a))
        {
            StatusText = "Needs a one-time permission. On the PC run: adb shell pm grant io.github.alastergr.pocketiris android.permission.WRITE_SECURE_SETTINGS";
            return StatusText;
        }

        //  First the app's own question, with no fingerprint shortcut, no "Always" and no "Let Claude decide".
        var where = mode == LinkMode.Wifi ? "the Wi-Fi network" : mode == LinkMode.WifiBluetooth ? "the Bluetooth network (with Wi-Fi on)" : "the Bluetooth network";
        var answer = await Approval.Ask(a, "Allow debugging this phone from your PC?",
            $"This opens the phone's debugging port to a PC on {where}. Only a PC whose key you approved can use it, " +
            "but anyone with that PC can do almost anything on this phone. Only allow it while you are developing.",
            allowLabel: "Allow", allowDelegate: false, fingerprintAllows: false);
        //  A "Deny" means the link stays off.
        if (answer != Decision.AllowOnce) { StatusText = "Off. You chose Deny."; return StatusText; }

        //  Then Android's own lock screen check: fingerprint, PIN or pattern.
        var proof = await CheckLock(a);
        //  A failed identity check (lock screen) also leaves the link off, and says why.
        if (proof != null) { StatusText = "Off. " + proof; return StatusText; }
        var prefs = a.GetSharedPreferences("ca", FileCreationMode.Private)!;

        //  Remember how Wireless debugging was, then switch it on (the Wi-Fi modes).
        if (usesWireless)
        {
            var before = Settings.Global.GetInt(a.ContentResolver, WirelessSetting, 0);
            //  Remember the original Wireless debugging setting once, so that turning the link off can put it back.
            if (!prefs.Contains("btdebugPrevious")) prefs.Edit()!.PutInt("btdebugPrevious", before)!.Apply();
            Settings.Global.PutInt(a.ContentResolver, WirelessSetting, 1);
        }
        StartStatusServer();
        Mode = mode;
        StatusText = "On (" + Name(mode) + "). Getting ready…";

        //  Switch Bluetooth tethering on (the Bluetooth modes).
        var tetherNote = "";
        //  The Bluetooth modes need Bluetooth tethering, so switch it on.
        if (usesBluetooth)
        {
            var tether = await SetTethering(a, true);
            //  Remember whether tethering was already on, so it is only switched off again if the link turned it on.
            if (tether.Ok) prefs.Edit()!.PutBoolean("btdebugTetherWasOn", tether.WasOn)!.Apply();
            tetherNote = tether.Message + " ";
        }
        _ = RingDoorbell(mode);
        _ = Task.Run(async () =>
        {
            var ports = await WaitForPorts(mode);
            var address = mode == LinkMode.Wifi ? AddressOf("wlan0") : AddressOf("bt-pan");
            var missing = mode == LinkMode.Wifi ? "(no Wi-Fi address)" : "(PC not on the Bluetooth network yet: join it from Windows)";
            StatusText = ports.Count > 0
                ? $"On ({Name(mode)}). {tetherNote}Phone {address ?? missing}, port {string.Join(" or ", ports)}. On the PC run the PocketIris Link shortcut."
                : mode == LinkMode.BluetoothOnly
                    ? $"On (bt). {tetherNote}Port 5555 is closed: with the USB cable plugged in, run the PocketIris Link shortcut once (it runs 'adb tcpip 5555'; repeat after every phone restart)."
                    : $"On ({Name(mode)}). {tetherNote}But no debugging port answered. Check Developer options > Wireless debugging (it needs Wi-Fi).";
        });
        return StatusText;
    }

    /// <summary>Switches the link off and puts Wireless debugging and Bluetooth tethering back to how they were before.</summary>
    static async Task<string> TurnOff(MainActivity a)
    {
        StopStatusServer();
        var was = Mode;
        Mode = LinkMode.Off;
        var prefs = a.GetSharedPreferences("ca", FileCreationMode.Private)!;
        var previous = prefs.GetInt("btdebugPrevious", 0);
        var hadPrevious = prefs.Contains("btdebugPrevious");
        var tetherWasOn = prefs.GetBoolean("btdebugTetherWasOn", true);
        prefs.Edit()!.Remove("btdebugPrevious")!.Remove("btdebugTetherWasOn")!.Apply();

        //  Put Wireless debugging back; if the permission is missing it stays as it is.
        if (hadPrevious) { try { Settings.Global.PutInt(a.ContentResolver, WirelessSetting, previous); } catch (Exception swallowed) { Diag.Swallowed("BluetoothDebug:175", swallowed); } }

        //  Switch Bluetooth tethering off again, but only if this link was the one that turned it on.
        var note = "";
        //  Switch tethering off again only if the link was what turned it on.
        if (was != LinkMode.Wifi && was != LinkMode.Off && !tetherWasOn) note = " " + (await SetTethering(a, false)).Message;
        StatusText = "Off." + note;
        return StatusText;
    }

    /// <summary>
    /// Opens Android's Tethering page, lets the accessibility service flip the Bluetooth tethering switch, then returns to PocketIris.
    /// Without the accessibility service the user has to do this one switch by hand.
    /// </summary>
    static async Task<(bool Ok, bool WasOn, string Message)> SetTethering(MainActivity a, bool on)
    {
        var svc = PhoneControlService.Instance;

        //  The accessibility service must be running for this to work.
        if (svc == null) return (false, true, "Switch Bluetooth tethering " + (on ? "on" : "off") + " yourself (Development > Tethering settings); the accessibility service is not running.");
        a.RunOnUiThread(() => a.StartActivity(new Intent("android.settings.TETHER_SETTINGS")));
        var result = await svc.SetBluetoothTethering(on);

        //  Go back to PocketIris whatever happened.
        svc.PerformGlobalAction(Android.AccessibilityServices.GlobalAction.Back);
        await Task.Delay(700);
        a.RunOnUiThread(() => { try { a.StartActivity(new Intent(a, typeof(MainActivity)).AddFlags(ActivityFlags.ReorderToFront | ActivityFlags.SingleTop)); } catch (Exception swallowed) { Diag.Swallowed("BluetoothDebug:201", swallowed); } });
        return result;
    }

    // ---------- Android's lock screen check ----------

    /// <summary>Shows Android's own fingerprint / PIN / pattern check; returns null if the user passed it, otherwise the reason.</summary>
    internal static Task<string?> CheckLock(MainActivity a)
    {
        var tcs = new TaskCompletionSource<string?>();
        a.RunOnUiThread(() =>
        {
            //  If the phone has no lock screen or refuses the check, report that as the reason.
            try
            {
                var builder = new BiometricPrompt.Builder(a)
                    .SetTitle("Allow debugging from your PC?")!
                    .SetDescription("Confirm with your fingerprint, PIN or pattern.")!;
                builder.SetAllowedAuthenticators(15 | 32768);
                var prompt = builder.Build();
                prompt.Authenticate(new CancellationSignal(), a.MainExecutor!, new LockCallback(tcs));
            }
            catch (Exception e) { tcs.TrySetResult("The phone could not show its lock screen check: " + e.Message); }
        });
        return tcs.Task;
    }

    /// <summary>Receives the result of Android's lock screen check.</summary>
    sealed class LockCallback(TaskCompletionSource<string?> tcs) : BiometricPrompt.AuthenticationCallback
    {
        /// <summary>The user passed the check.</summary>
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => tcs.TrySetResult(null);

        /// <summary>The check ended without success (cancelled, too many tries, or no lock screen).</summary>
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            tcs.TrySetResult(errString?.ToString() ?? "The lock screen check did not pass.");
    }

    // ---------- telling the PC ----------

    /// <summary>
    /// Sends a short "doorbell" message over Wi-Fi so a PC running bt-watch.ps1 can start the connection by itself.
    /// It contains no secrets, only a fixed text and the mode. It is sent three times, two seconds apart, in case one is lost.
    /// </summary>
    static async Task RingDoorbell(LinkMode mode)
    {
        //  If there is no Wi-Fi or sending fails, the user simply starts the PC side by hand.
        try
        {
            var message = Encoding.ASCII.GetBytes("pocketiris-btdebug-on|" + Name(mode));
            using var udp = new UdpClient { EnableBroadcast = true };
            var targets = new List<IPAddress> { IPAddress.Broadcast };

            //  Also send to the Wi-Fi network's own broadcast address, because some routers drop the general one.
            foreach (var ua in NetUtil.Ipv4("wlan0").Where(a => a.IPv4Mask != null))
            {
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                targets.Add(new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }

            //  Ring three times.
            for (int round = 0; round < 3; round++)
            {
                //  Send the message to each broadcast address in turn.
                foreach (var target in targets) await udp.SendAsync(message, message.Length, new IPEndPoint(target, 3498));
                await Task.Delay(2000);
            }
        }
        catch (Exception swallowed) { Diag.Swallowed("BluetoothDebug:270", swallowed); }
    }

    // ---------- finding the debugging port ----------

    static List<int> cachedPorts = new();
    static DateTime cachedAt = DateTime.MinValue;

    /// <summary>
    /// Finds the ports on this phone that answer like adb. Android does not tell apps which port Wireless debugging picked,
    /// so the app tries the range Android chooses from (and adb's classic port 5555) and keeps the ones that answer.
    /// The answer is remembered for 30 seconds so the PC and the settings page do not each trigger a search.
    /// </summary>
    static async Task<List<int>> FindPorts(LinkMode mode, bool force = false)
    {
        //  Reuse a recent answer (a short while even when it was empty) instead of searching again.
        if (!force && cachedAt != default && (DateTime.UtcNow - cachedAt).TotalSeconds < (cachedPorts.Count > 0 ? 30 : 3)) return cachedPorts;
        Task<List<int>> job;

        //  Only one search runs at a time; everyone who asks meanwhile shares its answer.
        lock (SearchLock) { job = searching is { IsCompleted: false } ? searching : (searching = Search(mode)); }
        return await job;
    }

    static readonly object SearchLock = new();
    static Task<List<int>>? searching;

    /// <summary>Does the actual search for <see cref="FindPorts"/>: probes the usual ports and remembers what answered.</summary>
    static async Task<List<int>> Search(LinkMode mode)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<int>();

        //  Classic adb port (set by "adb tcpip 5555"): this is the only one the Bluetooth-only mode can use.
        if (await Probe(5555, acceptPlain: true)) found.Add(5555);

        //  Wireless debugging's random port: try every port in Android's usual range, a few hundred at a time.
        if (mode != LinkMode.BluetoothOnly)
        {
            using var gate = new SemaphoreSlim(300);
            var tasks = Enumerable.Range(32768, 60999 - 32768 + 1).Select(async port =>
            {
                await gate.WaitAsync();
                //  Remember the port when something that speaks adb answers there.
                try { if (await Probe(port, acceptPlain: false)) found.Add(port); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        cachedPorts = found.Distinct().OrderBy(p => p).ToList();
        cachedAt = DateTime.UtcNow;
        Log.Info("PocketIris", "debugging link: ports that answer like adb: " + string.Join(",", cachedPorts));
        return cachedPorts;
    }

    /// <summary>
    /// True if the port speaks adb. The app says "hello" in adb's own language and looks at the reply: Wireless debugging answers
    /// STLS ("let us switch to encryption"); the classic port (5555) answers AUTH or CNXN.
    /// </summary>
    static async Task<bool> Probe(int port, bool acceptPlain)
    {
        //  Any failure simply means "not adb".
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            //  Give up on a port that does not answer within a moment.
            if (await Task.WhenAny(connect, Task.Delay(400)) != connect || !client.Connected) return false;
            var stream = client.GetStream();

            //  adb's hello message: the command CNXN, its version and size, then the text "host::".
            var text = Encoding.ASCII.GetBytes("host::\0");
            var hello = new byte[24 + text.Length];
            void Put(int at, uint v) { for (int i = 0; i < 4; i++) hello[at + i] = (byte)(v >> (8 * i)); }
            Put(0, 0x4e584e43); Put(4, 0x01000001); Put(8, 4096); Put(12, (uint)text.Length);
            Put(16, (uint)text.Sum(b => (int)b)); Put(20, 0x4e584e43 ^ 0xffffffff);
            Array.Copy(text, 0, hello, 24, text.Length);
            await stream.WriteAsync(hello);

            //  Read the first reply message and look at its command.
            var reply = new byte[24];
            var read = stream.ReadAsync(reply, 0, 24);
            //  Give up on a port that does not reply with at least a few bytes.
            if (await Task.WhenAny(read, Task.Delay(1500)) != read || await read < 4) return false;
            var command = (uint)(reply[0] | reply[1] << 8 | reply[2] << 16 | reply[3] << 24);

            //  STLS is Wireless debugging; AUTH or CNXN is the classic port.
            return command == 0x534c5453 || (acceptPlain && (command == 0x48545541 || command == 0x4e584e43));
        }
        catch { return false; }
    }

    /// <summary>Searches for the debugging port for up to 20 seconds and returns the ports found (empty if none).</summary>
    static async Task<List<int>> WaitForPorts(LinkMode mode)
    {
        //  Search again every few seconds; the port appears a moment after the setting is switched on.
        for (int i = 0; i < 5; i++)
        {
            var ports = await FindPorts(mode, force: i > 0);
            //  Use the ports found so far when there are any.
            if (ports.Count > 0) return ports;
            await Task.Delay(3000);
        }
        return new List<int>();
    }

    /// <summary>The phone's address on the named network connection (wlan0 or bt-pan), or null when it is not up.</summary>
    static string? AddressOf(string interfaceName) => NetUtil.Ipv4(interfaceName).FirstOrDefault()?.Address.ToString();

    // ---------- the small status port for the PC ----------

    /// <summary>Starts answering the PC's question "which port?" on the status port.</summary>
    static void StartStatusServer()
    {
        //  Do nothing if it already runs.
        if (listener != null) return;

        //  If the port is taken, the PC simply cannot ask until the link is turned off and on again.
        try { listener = new TcpListener(IPAddress.Any, StatusPort); listener.Start(); }
        catch { listener = null; return; }
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            //  Serve one PC request after another until the link is turned off.
            while (!ct.IsCancellationRequested)
            {
                //  A failed accept only matters if we were not asked to stop.
                try { var c = await listener!.AcceptTcpClientAsync(ct); _ = Task.Run(() => Serve(c)); }
                catch { if (ct.IsCancellationRequested) break; }
            }
        }, ct);
    }

    /// <summary>Stops the status port.</summary>
    static void StopStatusServer()
    {
        cts?.Cancel();
        //  Stopping a server that is already stopped is not a problem.
        try { listener?.Stop(); } catch (Exception swallowed) { Diag.Swallowed("BluetoothDebug:408", swallowed); }
        listener = null;
    }

    /// <summary>Answers one request, but only from a PC on the network the chosen mode uses and only while the link is on.</summary>
    static async Task Serve(TcpClient client)
    {
        using var _ = client;

        //  Answer at most four PCs at the same time; anyone else is turned away, so the port cannot be used to load the phone.
        if (!ServeSlots.Wait(0)) return;

        //  A client that goes away is simply dropped.
        try
        {
            //  Refuse everything that does not come in over the network of the chosen mode.
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            var network = Mode == LinkMode.Wifi ? "wlan0" : "bt-pan";
            //  Ignore requests while the link is off or when they come from outside the chosen network.
            if (!Enabled || !InSameNetwork(network, remote)) return;
            var stream = client.GetStream();
            var buf = new byte[1024];

            //  A client that sends nothing is dropped after five seconds.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.ReadAsync(buf, wait.Token);
            var ports = await FindPorts(Mode);
            var body = $"{{\"mode\":\"{Name(Mode)}\",\"ports\":[{string.Join(",", ports)}]}}";
            var reply = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        }
        catch (Exception swallowed) { Diag.Swallowed("BluetoothDebug:439", swallowed); }
        finally { ServeSlots.Release(); }
    }

    /// <summary>The number of status requests answered at the same time.</summary>
    static readonly SemaphoreSlim ServeSlots = new(4, 4);

    /// <summary>True if the address belongs to the same network as the phone's connection of that name.</summary>
    static bool InSameNetwork(string interfaceName, IPAddress remote)
    {
        var other = remote.MapToIPv4().GetAddressBytes();

        //  Say yes when the other computer is in the same network as one of the phone's addresses (compared under the network mask).
        foreach (var ua in NetUtil.Ipv4(interfaceName).Where(a => a.IPv4Mask != null))
        {
            var mine = ua.Address.GetAddressBytes();
            var mask = ua.IPv4Mask.GetAddressBytes();
            //  Same network when both addresses agree in every bit the network mask keeps.
            if (Enumerable.Range(0, 4).All(i => (mine[i] & mask[i]) == (other[i] & mask[i]))) return true;
        }
        return false;
    }
}

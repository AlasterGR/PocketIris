using Android.Content;
using Android.Provider;

namespace PocketIris;

/// <summary>
/// The "Cable" way of letting a PC work with this phone. The switch is Off by default and turns itself On when a cable is attached
/// and USB debugging is already enabled. Switching it On while USB debugging is off asks first (dialog, then Android's lock check)
/// and then enables USB debugging, remembering how it was so that switching Off can put it back.
/// It never touches the wireless link: the two ways are independent, so pulling the cable or switching the cable Off keeps a
/// working wireless connection running untouched.
/// </summary>
public static class UsbCable
{
    const string DebugSetting = "adb_enabled";

    /// <summary>True when a USB cable with a data connection to another device is attached (read from Android's sticky USB broadcast).</summary>
    public static bool Attached(Context c)
    {
        //  Ask Android for its last USB state; if it cannot be read, assume no cable.
        try
        {
            using var filter = new IntentFilter("android.hardware.usb.action.USB_STATE");
            var state = c.RegisterReceiver(null, filter);
            return state != null && state.GetBooleanExtra("connected", false);
        }
        catch (Exception swallowed) { Diag.Swallowed("UsbCable:Attached", swallowed); return false; }
    }

    /// <summary>True when USB debugging is switched on in Android's developer options.</summary>
    public static bool DebuggingOn(Context c) => Settings.Global.GetInt(c.ContentResolver, DebugSetting, 0) == 1;

    static ISharedPreferences Prefs(Context c) => c.GetSharedPreferences("ca", FileCreationMode.Private)!;

    /// <summary>True when the Cable switch counts as On: chosen by the user, or automatic because a cable is attached and USB debugging is already on.</summary>
    public static bool IsOn(Context c)
    {
        var prefs = Prefs(c);
        var attached = Attached(c);

        //  Once the cable is out, forget an earlier "Off" so the automatic On works again the next time a cable is attached.
        if (!attached && prefs.GetBoolean("cableAutoOff", false)) prefs.Edit()!.PutBoolean("cableAutoOff", false)!.Apply();
        return prefs.GetBoolean("cableUser", false) || (attached && DebuggingOn(c) && !prefs.GetBoolean("cableAutoOff", false));
    }

    /// <summary>One sentence for the page saying what the Cable switch is doing right now.</summary>
    public static string StatusText(Context c)
    {
        var attached = Attached(c);
        var debugging = DebuggingOn(c);

        //  Say which of the needed things are missing, or that everything is ready.
        if (IsOn(c))
            return attached ? "On: cable attached and USB debugging enabled. " + PcContact.Line("cable") : "On: waiting for a cable. USB debugging is " + (debugging ? "enabled." : "off.");
        return attached ? "Off. A cable is attached; switch On to use it." : "Off. Switches itself On when a cable is attached and USB debugging is enabled.";
    }

    /// <summary>Switches the Cable way on or off, asking first whenever that means changing an Android setting.</summary>
    /// <param name="a">The activity on screen.</param>
    /// <param name="on">True to switch On.</param>
    /// <returns>A sentence saying what happened.</returns>
    public static async Task<string> Set(MainActivity a, bool on)
    {
        var prefs = Prefs(a);

        //  Switching Off: remember the choice, and give USB debugging back as it was if this app was the one that turned it on.
        if (!on)
        {
            prefs.Edit()!.PutBoolean("cableUser", false)!.PutBoolean("cableAutoOff", true)!.Apply();

            //  Put the setting back only when this app changed it and has the permission to change it again.
            if (prefs.Contains("cablePrevious") && BluetoothDebug.HasPermission(a))
            {
                try { Settings.Global.PutInt(a.ContentResolver, DebugSetting, prefs.GetInt("cablePrevious", 0)); } catch (Exception swallowed) { Diag.Swallowed("UsbCable:restore", swallowed); }
                prefs.Edit()!.Remove("cablePrevious")!.Apply();
            }
            return StatusText(a);
        }

        //  Switching On when everything is already enabled needs no change to Android.
        if (DebuggingOn(a)) { prefs.Edit()!.PutBoolean("cableUser", true)!.PutBoolean("cableAutoOff", false)!.Apply(); return StatusText(a); }

        //  Without the one-time permission the app cannot enable USB debugging itself, so send the user to the right Android page.
        if (!BluetoothDebug.HasPermission(a))
        {
            a.RunOnUiThread(() => { try { a.StartActivity(new Intent(Settings.ActionApplicationDevelopmentSettings).AddFlags(ActivityFlags.NewTask)); } catch (Exception swallowed) { Diag.Swallowed("UsbCable:open", swallowed); } });
            return "USB debugging is off. Developer options is open: switch on USB debugging, then come back and switch the cable On.";
        }

        //  Ask before enabling USB debugging: first the app's own question, then Android's lock check.
        var answer = await Approval.Ask(a, "Turn on USB debugging?",
            "This lets a PC connected by cable work with this phone. Only a PC whose key you approved can use it. The app turns it off again when you switch the cable Off.",
            allowLabel: "Allow", allowDelegate: false, fingerprintAllows: false);

        //  A "Deny" leaves everything as it was.
        if (answer != Decision.AllowOnce) return "Off. You chose Deny.";
        var proof = await BluetoothDebug.CheckLock(a);

        //  A failed lock check also leaves everything as it was.
        if (proof != null) return "Off. " + proof;
        prefs.Edit()!.PutInt("cablePrevious", 0)!.PutBoolean("cableUser", true)!.PutBoolean("cableAutoOff", false)!.Apply();
        Settings.Global.PutInt(a.ContentResolver, DebugSetting, 1);
        return StatusText(a);
    }
}

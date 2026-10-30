using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PocketIris;

/// <summary>
/// Keeps the app process alive in the background so Claude's browser tools (the MCP server on port 3401)
/// keep answering. It shows a quiet ongoing notification; tapping it opens the app.
/// </summary>
[Service(Name = "io.github.alastergr.pocketiris.KeepAliveService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class KeepAliveService : Service
{
    /// <summary>This service is only started, never bound to.</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Shows the ongoing notification, makes sure the tool server is running, and asks Android to restart the service if it is stopped.</summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        nm?.CreateNotificationChannel(new NotificationChannel("keepalive", "Running in the background", NotificationImportance.Min)
        {
            Description = "Keeps Claude's browser tools available while the app is in the background",
        });
        var open = PendingIntent.GetActivity(this, 2,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var n = new Notification.Builder(this, "keepalive")
            .SetContentTitle("PocketIris")!
            .SetContentText("Browser tools are available")!
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .Build();
        StartForeground(1, n, ForegroundService.TypeSpecialUse);
        McpServer.Instance.Start();
        return StartCommandResult.Sticky;
    }
}

/// <summary>Notifications the app posts without needing the activity.</summary>
public static class AppNotifier
{
    /// <summary>Claude needs a question answered in the app but the app is in the background: offer a one-tap way in.</summary>
    /// <param name="title">What is happening.</param>
    /// <param name="text">What to do.</param>
    public static void NeedsYou(string title, string text) =>
        Post(7003, "browser", "Browser requests", NotificationImportance.High, 4, title, text);

    /// <summary>A trusted session started: show a lasting notice with an End button, so the user always sees that it is on.</summary>
    /// <param name="until">When the session ends by itself.</param>
    public static void TrustedSessionOn(DateTime until) =>
        Post(7004, "trusted", "Trusted session", NotificationImportance.Default, 5,
            "Trusted session is on", $"Claude may open sites and control approved apps until {until.ToLocalTime():HH:mm}",
            ongoing: true, icon: Android.Resource.Drawable.IcDialogAlert,
            endButton: new Intent(Application.Context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop).PutExtra("end-trust", true));

    /// <summary>The trusted session ended: remove the notice.</summary>
    public static void TrustedSessionOff()
    {
        //  If the notice cannot be removed, the user can swipe it away.
        try { ((NotificationManager?)Application.Context.GetSystemService(Context.NotificationService))?.Cancel(7004); }
        catch (Exception swallowed) { Diag.Swallowed("AppNotifier.TrustedSessionOff", swallowed); }
    }

    /// <summary>Updates are available (here: Termux apps); a tap opens the PocketIris tab, where the Updates card is.</summary>
    /// <param name="title">What is available.</param>
    /// <param name="text">Where to go.</param>
    public static void UpdatesAvailable(string title, string text) =>
        Post(7006, "updates", "Updates available", NotificationImportance.Default, 9, title, text, openPocketIris: true);

    /// <summary>Something in Termux now has a different version than the tested set: say what, once (a quiet notice).</summary>
    /// <param name="text">Which versions differ, and the result of the quick health check.</param>
    public static void VersionsChanged(string text) =>
        Post(7005, "versions", "Version changes", NotificationImportance.Low, 7, "Versions changed since the last test", text);

    /// <summary>The screenshot is done but Android would not bring the app back to the front: offer a one-tap way back.</summary>
    public static void ScreenshotDone() =>
        Post(7002, "browser", "Browser requests", NotificationImportance.High, 3, "Screenshot taken", "Tap to return to PocketIris");

    /// <summary>Claude wants the browser but the app is not on screen: tell the user.</summary>
    public static void BrowserNeeded() =>
        Post(7001, "browser", "Browser requests", NotificationImportance.High, 1, "Claude wants to use the browser", "Tap to open PocketIris",
            showBrowser: true);

    /// <summary>
    /// Posts one notification that opens the app when tapped. A problem is only written to the log, because every caller can
    /// carry on without the notice (the tool call times out with an explanation, the settings page shows the state, or the user taps the app icon).
    /// </summary>
    /// <param name="id">The notification number (a new one with the same number replaces the old).</param>
    /// <param name="channel">The channel's id.</param>
    /// <param name="channelName">The channel's name, as the user sees it in Android's settings.</param>
    /// <param name="importance">How loudly Android should show it.</param>
    /// <param name="requestCode">A number that keeps this notice's tap target apart from the others.</param>
    /// <param name="title">The headline.</param>
    /// <param name="text">The detail line.</param>
    /// <param name="ongoing">True for a notice that cannot be swiped away.</param>
    /// <param name="icon">The small icon, from Android's own set.</param>
    /// <param name="showBrowser">True when tapping should show the Browser view.</param>
    /// <param name="endButton">When given, adds an "End now" button that sends this intent.</param>
    /// <param name="openPocketIris">True when tapping should show the PocketIris tab.</param>
    static void Post(int id, string channel, string channelName, NotificationImportance importance, int requestCode, string title, string text,
        bool ongoing = false, int icon = Android.Resource.Drawable.IcDialogInfo, bool showBrowser = false, Intent? endButton = null, bool openPocketIris = false)
    {
        //  A notice that cannot be posted is only logged.
        try
        {
            var ctx = Application.Context;
            var nm = (NotificationManager?)ctx.GetSystemService(Context.NotificationService);

            //  Without a notification manager there is nothing to show.
            if (nm == null) return;
            nm.CreateNotificationChannel(new NotificationChannel(channel, channelName, importance));
            var openIntent = new Intent(ctx, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop);
            //  A tap on a browser notice opens the Browser view directly.
            if (showBrowser) openIntent.PutExtra("show-browser", true);
            //  An update notice opens the PocketIris tab, where the Update buttons are.
            if (openPocketIris) openIntent.PutExtra("cmd", "pocketiris");
            var open = PendingIntent.GetActivity(ctx, requestCode, openIntent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var builder = new Notification.Builder(ctx, channel)
                .SetContentTitle(title)!
                .SetContentText(text)!
                .SetStyle(new Notification.BigTextStyle().BigText(text))!
                .SetSmallIcon(icon)!
                .SetContentIntent(open)!
                .SetOngoing(ongoing)!
                .SetAutoCancel(!ongoing)!;

            //  Add the "End now" button only when one was asked for.
            if (endButton != null)
            {
                var end = PendingIntent.GetActivity(ctx, requestCode + 1, endButton, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                builder.AddAction(new Notification.Action.Builder(Android.Resource.Drawable.IcMenuCloseClearCancel, "End now", end).Build());
            }
            nm.Notify(id, builder.Build());
        }
        catch (Exception swallowed) { Diag.Swallowed("AppNotifier.Post " + id, swallowed); }
    }
}

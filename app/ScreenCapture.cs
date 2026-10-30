using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using Android.Views;

namespace PocketIris;

/// <summary>
/// phone_screenshot: lets Claude look at the whole phone screen once, after two questions to the user: the app's own approval
/// dialog and Android's "start recording or casting?" screen. Android forces both on every capture, so nothing can look at the
/// screen in the background. The service below takes one picture and stops. The picture is sent to Claude as a small PNG.
/// </summary>
public static class ScreenCapture
{
    public const int RequestScreen = 16;

    /// <summary>The answer of Android's screen-capture consent screen, filled in by MainActivity.</summary>
    public static TaskCompletionSource<(Result Code, Intent? Data)>? Consent;

    /// <summary>The picture taken by the service (PNG, base64), or null if it failed.</summary>
    public static TaskCompletionSource<string?>? Picture;

    /// <summary>Asks the user, takes one screenshot of the whole screen, and returns it as a base64 PNG (or null with a reason).</summary>
    /// <param name="a">The activity that is on screen.</param>
    /// <param name="hideApp">True to step out of the way first so Claude sees what is behind the app (the home screen or the last app).</param>
    /// <returns>The picture, or a short explanation of why there is none.</returns>
    public static async Task<(string? Png, string Message)> Capture(MainActivity a, bool hideApp)
    {
        //  First the app's own question (skipped in a trusted session); "Let Claude decide" is not offered because a screen can show private things.
        var answer = TrustedSession.Active ? Decision.AllowOnce : await Approval.Ask(a, "Let Claude look at your screen?",
            "Claude gets one picture of the whole screen" + (hideApp ? " (PocketIris steps aside first so it sees the app behind)." : ".") +
            " Android will ask once more. Only allow it if you asked for it, and no passwords or private messages are showing.",
            allowDelegate: false);
        //  Stop when the user declined to share the screen.
        if (answer != Decision.AllowOnce && answer != Decision.AllowAlways) return (null, "The user declined to share the screen.");

        //  Then Android's own question, which can only be answered by the user.
        Consent = new TaskCompletionSource<(Result, Intent?)>();
        var mpm = (MediaProjectionManager)a.GetSystemService(Context.MediaProjectionService)!;
        a.RunOnUiThread(() =>
        {
            //  Newer phones can be told to offer only the whole screen.
            var intent = OperatingSystem.IsAndroidVersionAtLeast(34)
                ? mpm.CreateScreenCaptureIntent(MediaProjectionConfig.CreateConfigForDefaultDisplay())
                : mpm.CreateScreenCaptureIntent();
            a.StartActivityForResult(intent, RequestScreen);
        });
        var done = await Task.WhenAny(Consent.Task, Task.Delay(60000));

        //  Give up if the user did not answer in a minute or said no.
        if (done != Consent.Task) return (null, "The user did not answer Android's screen-capture question in time.");
        var (code, data) = Consent.Task.Result;
        //  Stop when the user declined Android's question.
        if (code != Result.Ok || data == null) return (null, "The user declined Android's screen-capture question.");

        //  Measure the real screen so the picture has the right size.
        int w, h, dpi = (int)(a.Resources!.DisplayMetrics!.DensityDpi);
        //  Newer phones tell the real screen size directly.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var b = a.WindowManager!.CurrentWindowMetrics.Bounds;
            w = b.Width(); h = b.Height();
        }
        else { w = a.Resources.DisplayMetrics.WidthPixels; h = a.Resources.DisplayMetrics.HeightPixels; }

        //  Start the capture service while the app is still on screen (Android requires that), then step aside.
        Picture = new TaskCompletionSource<string?>();
        var svc = new Intent(a, typeof(ScreenCaptureService));
        svc.PutExtra("code", (int)code).PutExtra("data", data).PutExtra("w", w).PutExtra("h", h).PutExtra("dpi", dpi).PutExtra("delay", hideApp ? 1500 : 300);
        a.StartForegroundService(svc);
        //  Step aside first, if asked, so the picture shows what is behind the app.
        if (hideApp) a.RunOnUiThread(() => a.MoveTaskToBack(true));
        var shot = await Task.WhenAny(Picture.Task, Task.Delay(20000));

        //  Bring the app back (if Android allows it from the background) so the user is not left on another screen.
        if (hideApp) a.RunOnUiThread(() =>
        {
            //  Android may refuse to open the app from the background; the user then returns to it by hand.
            try { a.StartActivity(new Intent(a, typeof(MainActivity)).AddFlags(ActivityFlags.ReorderToFront | ActivityFlags.SingleTop)); } catch (Exception swallowed) { Diag.Swallowed("ScreenCapture:85", swallowed); }
        });
        //  If Android kept the app in the background, leave a notification that returns to it with one tap.
        if (hideApp)
        {
            await Task.Delay(800);
            //  Offer a way back when Android did not return to the app by itself.
            if (!a.IsForeground) AppNotifier.ScreenshotDone();
        }
        //  Say so when no picture arrived.
        if (shot != Picture.Task || Picture.Task.Result == null) return (null, "The screenshot could not be taken.");
        return (Picture.Task.Result, "Screenshot of the whole screen" + (hideApp ? " (PocketIris stepped aside; if the app did not come back, tap its icon)." : "."));
    }
}

/// <summary>Foreground service that Android requires for screen capture: takes one picture, hands it over and stops itself.</summary>
[Service(Name = "io.github.alastergr.pocketiris.ScreenCaptureService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public class ScreenCaptureService : Service
{
    /// <summary>This service is only started, never bound to.</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Shows the required notification, then captures one frame after the requested delay.</summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        nm?.CreateNotificationChannel(new NotificationChannel("capture", "Screen capture", NotificationImportance.Low));
        var n = new Notification.Builder(this, "capture")
            .SetContentTitle("PocketIris is taking a screenshot")!
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)!
            .SetOngoing(true)!
            .Build();
        StartForeground(2, n, ForegroundService.TypeMediaProjection);

        //  Do the capture in the background; any problem is reported as "no picture".
        _ = Task.Run(async () =>
        {
            string? png = null;
            //  Take the picture; a problem leaves no picture and is only written to the log.
            try { png = await TakeOne(intent!); }
            catch (Exception e) { Log.Info("PocketIris", "screenshot failed: " + e.Message); }
            ScreenCapture.Picture?.TrySetResult(png);
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        });
        return StartCommandResult.NotSticky;
    }

    /// <summary>Records exactly one frame of the screen and returns it as a base64 PNG, or null.</summary>
    async Task<string?> TakeOne(Intent intent)
    {
        int w = intent.GetIntExtra("w", 0), h = intent.GetIntExtra("h", 0), dpi = intent.GetIntExtra("dpi", 320);
        int delay = intent.GetIntExtra("delay", 300);
        var code = (Result)intent.GetIntExtra("code", 0);
#pragma warning disable CA1422, CS0618
        var data = (Intent?)intent.GetParcelableExtra("data");
#pragma warning restore CA1422, CS0618

        //  Without a size or the consent data there is nothing to capture.
        if (w <= 0 || h <= 0 || data == null) return null;
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        var projection = mpm.GetMediaProjection((int)code, data)!;
        var stopped = new TaskCompletionSource<bool>();
        projection.RegisterCallback(new ProjectionCallback(() => stopped.TrySetResult(true)), new Handler(Looper.MainLooper!));
        using var reader = ImageReader.NewInstance(w, h, (ImageFormatType)1, 2)!;
        var display = projection.CreateVirtualDisplay("pocketiris", w, h, dpi, (DisplayFlags)16, reader.Surface, null, null)!;

        //  Wait so the user's app switch can finish, then keep the newest frame.
        await Task.Delay(delay);
        Image? image = null;
        //  Try up to twenty times, because the first picture takes a moment to arrive.
        for (int i = 0; i < 20 && image == null; i++)
        {
            image = reader.AcquireLatestImage();

            //  No frame yet: try again in a moment.
            if (image == null) await Task.Delay(150);
        }
        string? result = null;

        //  Turn the frame into a small PNG when one arrived.
        if (image != null)
        {
            var plane = image.GetPlanes()![0];
            int pixelStride = plane.PixelStride, rowStride = plane.RowStride;
            using var full = Bitmap.CreateBitmap(rowStride / pixelStride, h, Bitmap.Config.Argb8888!)!;
            full.CopyPixelsFromBuffer(plane.Buffer!);
            using var cropped = Bitmap.CreateBitmap(full, 0, 0, w, h);
            using var scaled = cropped.Width > 720 ? Bitmap.CreateScaledBitmap(cropped, 720, cropped.Height * 720 / cropped.Width, true)! : cropped;
            using var ms = new MemoryStream();
            scaled.Compress(Bitmap.CompressFormat.Png!, 90, ms);
            result = Convert.ToBase64String(ms.ToArray());
            image.Close();
        }
        display.Release();
        projection.Stop();
        return result;
    }

    /// <summary>Required by Android: told when the capture session ends.</summary>
    sealed class ProjectionCallback(Action onStop) : MediaProjection.Callback
    {
        /// <summary>The capture session ended.</summary>
        public override void OnStop() => onStop();
    }
}

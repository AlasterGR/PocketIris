using Android.App;
using Android.Content;

namespace PocketIris;

/// <summary>
/// A "trusted session": one confirmation (a touch on the fingerprint sensor, or the Start button) lets Claude carry on for a
/// limited time without stopping for every routine question. The session never survives an app restart and ends by itself.
///
/// While it is on, these routine questions are answered "allow" automatically:
///   - opening a new public website in the Browser view,
///   - controlling an app with the screen tools (apps that are never allowed stay blocked),
///   - the app's own question before a screenshot (Android's separate screen-capture question is still shown).
///
/// It never skips: downloads, private or local network addresses, turning the safety switches on, the debugging link,
/// the list of blocked apps, password protection, the action limit, or any question Android itself asks.
/// A persistent notification shows that it is on and has an End button.
/// </summary>
public static class TrustedSession
{
    /// <summary>How long one session lasts.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(30);

    static DateTime until = DateTime.MinValue;
    static Timer? timer;

    /// <summary>Raised whenever a session starts or ends, so the screen can update.</summary>
    public static event Action? Changed;

    /// <summary>True while a session is on.</summary>
    public static bool Active => DateTime.UtcNow < until;

    /// <summary>How much time is left, or zero when no session is on.</summary>
    public static TimeSpan Remaining => Active ? until - DateTime.UtcNow : TimeSpan.Zero;

    /// <summary>A sentence for the settings page: whether a session is on and how long it still lasts.</summary>
    public static string StatusText
    {
        get
        {
            //  Say how long is left when a session is on.
            if (Active) return $"On, about {Math.Max(1, (int)Math.Ceiling(Remaining.TotalMinutes))} minutes left";
            return "Off. Starting one asks you once (a touch on the fingerprint sensor is enough).";
        }
    }

    /// <summary>Asks the user once, and starts a session if they agree.</summary>
    /// <param name="a">The activity to show the question in.</param>
    /// <returns>True when a session is on afterwards.</returns>
    public static async Task<bool> Start(MainActivity a)
    {
        //  A session that is already on is simply kept.
        if (Active) return true;
        var minutes = (int)Length.TotalMinutes;
        var answer = await Approval.Ask(a, "Start a trusted session?",
            $"For {minutes} minutes Claude can open new websites, control apps and take screenshots without asking you each time.\n\n" +
            "It still asks before downloads, private addresses and changes to your safety settings. Blocked apps, password fields and Android's own questions stay as they are.\n\n" +
            "You can end it at any time from the notification or in Settings > Development.",
            allowLabel: $"Start ({minutes} min)", allowDelegate: false, fingerprintAllows: true);

        //  Only a real "yes" starts a session.
        if (answer != Decision.AllowOnce) return false;
        until = DateTime.UtcNow + Length;
        timer?.Dispose();
        timer = new Timer(_ => End(), null, Length, Timeout.InfiniteTimeSpan);
        AppNotifier.TrustedSessionOn(until);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ends the session now (also called when its time is up).</summary>
    public static void End()
    {
        //  Nothing to do when no session is on.
        if (!Active && timer == null) return;
        until = DateTime.MinValue;
        timer?.Dispose();
        timer = null;
        AppNotifier.TrustedSessionOff();
        Changed?.Invoke();
    }
}

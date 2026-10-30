namespace PocketIris;

/// <summary>Small helpers for things that go wrong quietly.</summary>
public static class Diag
{
    /// <summary>
    /// Records a problem that the app deliberately carries on after (for example a notification that could not be shown),
    /// so that it leaves a trace in the phone's log (tag "PocketIris") instead of vanishing.
    /// </summary>
    /// <param name="where">A short name for the place, so the trace can be found in the code.</param>
    /// <param name="problem">What went wrong.</param>
    public static void Swallowed(string where, Exception problem) =>
        Android.Util.Log.Debug("PocketIris", "ignored in " + where + ": " + problem.Message);
}

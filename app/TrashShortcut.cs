using System.Text;
using Android.Webkit;
using Java.Interop;

namespace PocketIris;

/// <summary>
/// The only bridge the chat page gets (as <c>window.PocketTrash</c>): it can ask for one session to be moved to the trash or
/// restored, nothing else, and both are reversible. The chat page can show text from the internet, so it gets no other power.
/// </summary>
public sealed class TrashShortcut(MainActivity activity, WebView web) : Java.Lang.Object
{
    /// <summary>Moves one session to the trash, then tells the page how it went.</summary>
    /// <param name="projectId">Yep's id of the project, as it appears in the chat page's addresses.</param>
    /// <param name="sessionId">The session's id.</param>
    [JavascriptInterface, Export("move")]
    public void Move(string projectId, string sessionId) => Relay("move", "/trash/move", projectId, sessionId);

    /// <summary>Puts one session back from the trash, then tells the page how it went.</summary>
    /// <param name="projectId">Yep's id of the project.</param>
    /// <param name="sessionId">The session's id.</param>
    [JavascriptInterface, Export("restore")]
    public void Restore(string projectId, string sessionId) => Relay("restore", "/trash/restore", projectId, sessionId);

    /// <summary>Sends the request to the sidecar in the background and reports the result to the page.</summary>
    void Relay(string action, string path, string projectId, string sessionId)
    {
        //  Only plain ids are accepted, so nothing else can be smuggled into the request.
        if (!IsPlain(projectId) || !IsPlain(sessionId)) return;
        _ = Task.Run(async () =>
        {
            var body = new StringBuilder("{\"projectId\":\"").Append(projectId).Append("\",\"sessionId\":\"").Append(sessionId).Append("\"}").ToString();
            var (status, _) = await SidecarClient.Call("POST", path, body);
            var js = $"window.__pcTrashDone&&window.__pcTrashDone({PageBridge.Quote(action)},{PageBridge.Quote(projectId)},{PageBridge.Quote(sessionId)},{status})";
            activity.RunOnUiThread(() => web.EvaluateJavascript(js, null));
        });
    }

    /// <summary>True for a short text made only of letters, digits, dash, underscore and dot.</summary>
    static bool IsPlain(string text) => text.Length is > 0 and <= 200 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

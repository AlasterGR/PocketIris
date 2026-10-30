using System.Net.Http.Headers;
using System.Text;

namespace PocketIris;

/// <summary>
/// Talks to the sidecar (the helper in Termux that moves sessions to the trash and restores them) on 127.0.0.1:3405,
/// with the secret key only the app and Termux know. Only a short list of requests is allowed.
/// </summary>
public static class SidecarClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The requests the page may make: (method, path).</summary>
    static readonly HashSet<string> Allowed =
        ["GET /status", "GET /sessions", "GET /trash", "POST /trash/move", "POST /trash/restore"];

    /// <summary>Sends one allowed request to the sidecar.</summary>
    /// <param name="method">"GET" or "POST".</param>
    /// <param name="path">The sidecar path, for example /trash.</param>
    /// <param name="body">The JSON body for a POST, or empty.</param>
    /// <returns>The HTTP status (0 when the sidecar did not answer) and the answer text.</returns>
    public static async Task<(int Status, string Text)> Call(string method, string path, string body)
    {
        //  Refuse everything that is not on the short list.
        if (!Allowed.Contains(method + " " + path)) return (400, "{\"error\":\"Not allowed\"}");
        //  A sidecar that does not answer is reported as status 0, never as a crash.
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "http://127.0.0.1:3405" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppSecret.Sidecar.Value);

            //  A POST carries its JSON body.
            if (method == "POST") request.Content = new StringContent(string.IsNullOrEmpty(body) ? "{}" : body, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception problem)
        {
            Diag.Swallowed("SidecarClient.Call " + path, problem);
            return (0, "{\"error\":\"The helper did not answer\"}");
        }
    }
}

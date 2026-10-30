using System.Text;

namespace PocketIris;

/// <summary>
/// A secret key that proves a request comes from one of PocketIris's own helpers in Termux. Each key is made once at random, kept
/// in the app's private settings, and handed to Termux when the app starts something there (the helper then asks for it in an
/// Authorization header). There is one key for the tool server (Claude) and one for the sidecar (notifications and trash).
/// </summary>
public sealed class AppSecret(string prefName, int bytes)
{
    /// <summary>The key of the tool server on port 3401.</summary>
    public static readonly AppSecret Mcp = new("mcpToken", 24);

    /// <summary>The key of the sidecar on port 3405.</summary>
    public static readonly AppSecret Sidecar = new("sidecarToken", 24);

    string? cached;

    /// <summary>Returns the key, creating and saving it the very first time.</summary>
    public string Value
    {
        get
        {
            //  Reuse the key once it has been read or made in this run.
            if (cached != null) return cached;
            var prefs = Android.App.Application.Context.GetSharedPreferences("ca", Android.Content.FileCreationMode.Private)!;
            var t = prefs.GetString(prefName, null);

            //  Create and save a new random key the very first time.
            if (t == null)
            {
                t = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
                prefs.Edit()!.PutString(prefName, t)!.Apply();
            }
            return cached = t;
        }
    }

    /// <summary>True when the Authorization header carries exactly this key (compared in constant time).</summary>
    /// <param name="authorization">The value of the Authorization header, or null.</param>
    public bool Matches(string? authorization)
    {
        //  No header, or not the "Bearer" kind, means no access.
        if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(Value);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, expected);
    }
}

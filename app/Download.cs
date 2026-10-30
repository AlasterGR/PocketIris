using System.Net;

namespace PocketIris;

/// <summary>
/// browser_download: fetch one file over http(s) into Download/PocketIris. Every restriction is a question to the user
/// (Allow / Let Claude decide / Deny, fingerprint touch = Allow): a site other than github.com, a redirect or address on the
/// local network, a file over 600 MB or 4 GB, and the final "download this file" confirmation. Addresses on this phone itself
/// (loopback) ask with a warning and need a tap on Allow. Nothing is opened or installed.
/// </summary>
public sealed partial class BrowserHost
{
    const long AskAboveBytes = 600L * 1024 * 1024;
    const long AskAgainAboveBytes = 4L * 1024 * 1024 * 1024;
    static readonly string[] GitHubHosts = ["github.com", "www.github.com"];
    public const string DownloadDir = "/sdcard/Download/PocketIris";

    /// <summary>True if the host name or address means this phone itself.</summary>
    public static bool IsLoopbackHost(string host) => IsLoopback(host);

    /// <summary>True if the host name or address means this phone itself (localhost, 127.x, ::1, 0.0.0.0).</summary>
    static bool IsLoopback(string host)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        return host == "localhost" || host.EndsWith(".localhost") || (IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));
    }

    /// <summary>Asks the user about one restriction; returns normally if they allowed it, otherwise throws the message Claude should see.</summary>
    /// <param name="key">Identifies the exact request, for the "Let Claude decide" slip.</param>
    /// <param name="title">The question.</param>
    /// <param name="message">What exactly would happen.</param>
    /// <param name="what">A short description for Claude's message.</param>
    /// <param name="risky">True for the phone itself: no fingerprint shortcut and no "Let Claude decide"; the user must tap Allow.</param>
    async Task Ask(string key, string title, string message, string what, bool risky = false)
    {
        var answer = await Approval.Ask(activity, title, message, allowDelegate: !risky, fingerprintAllows: !risky);

        //  Tell Claude plainly when the user said no, or hand it a slip when the user left the decision to it.
        if (answer == Decision.Deny) throw new BrowserException("The user declined: " + what + ".");
        //  Give Claude a one-time slip when the user left the decision to it.
        if (answer == Decision.LetClaudeDecide) throw new BrowserException(Approval.Delegate(key, what));
    }

    /// <summary>Downloads one file after the user approves it, and returns a sentence saying where it was saved.</summary>
    /// <param name="url">The address of the file.</param>
    /// <param name="delegatedToken">The slip from an earlier "Let Claude decide", if any.</param>
    public async Task<string> Download(string url, string? delegatedToken = null)
    {
        await EnsureVisible();
        var key = "download:" + url;
        bool delegated = Approval.Redeem(delegatedToken, key);

        //  Make sure the address is a usable web address.
        if (string.IsNullOrWhiteSpace(url)) throw new BrowserException("No url given.");
        //  Add "https://" when the address has no scheme.
        if (!url.Contains("://")) url = "https://" + url;
        //  Refuse addresses that are not ordinary web addresses.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new BrowserException("Only http and https addresses can be downloaded.");

        //  Asks about addresses on this phone or the local network (both for the first address and for every redirect).
        async Task CheckHost(Uri u, string how)
        {
            //  The phone itself gets the strongest warning; a private network gets an ordinary question.
            if (IsLoopback(u.Host))
                await Ask(key, "Download from the phone itself?",
                    $"{how} {u.Host}, a service running on THIS phone (your chat server, terminal or the app's own tools). A web page could steer Claude here to copy private data. Only allow it if you asked for it.",
                    $"download from the phone's own service {u.Host}", risky: true);
            else if (IsBlockedHost(u.Host) && !delegated)
                await Ask(key, "Download from your local network?", $"{how} {u.Host}, a private network address.", $"download from the private address {u.Host}");
        }

        //  Ask about any site that is not GitHub and not an address already handled below.
        if (!GitHubHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && !IsBlockedHost(uri.Host) && !IsLoopback(uri.Host))
            await ApproveHost(uri.Host, key, delegated);
        await CheckHost(uri, "The address is");

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PocketIris/1.0");

        //  Follow redirects by hand (at most six) so each new address can be checked first.
        HttpResponseMessage? resp = null;
        //  Take one step per redirect, checking each new address first.
        for (int hop = 0; hop < 6; hop++)
        {
            resp?.Dispose();
            resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);

            //  A redirect: check where it leads, then follow it.
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                uri = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(uri, resp.Headers.Location);
                //  Refuse a redirect that leads anywhere but to a web address.
                if (uri.Scheme != "https" && uri.Scheme != "http") throw new BrowserException("Blocked: the download redirected to a non-web address.");
                await CheckHost(uri, "The download redirected to");
                continue;
            }
            break;
        }

        //  Stop if the server did not answer with the file.
        if (resp == null || !resp.IsSuccessStatusCode) throw new BrowserException($"The server answered {(int?)resp?.StatusCode}.");
        using var _ = resp;
        long? size = resp.Content.Headers.ContentLength;
        var name = FileNameFor(resp, uri);
        var sizeText = size == null ? "unknown size" : size > 1 << 20 ? $"{size / 1048576.0:0.0} MB" : $"{size / 1024.0:0} KB";

        //  Ask again for very big files, and refuse files that cannot fit on the phone.
        if (size > AskAboveBytes && !delegated)
            await Ask(key, "Very large download", $"{name}\n{sizeText} from {uri.Host}", $"download a very large file ({sizeText})");
        //  Ask again before a very large or huge download, unless Claude has a valid slip.
        if (size > AskAgainAboveBytes && !delegated)
            await Ask(key, "Huge download (over 4 GB)", $"{name}\n{sizeText} from {uri.Host}\nFree space on the phone: {FreeGb():0.0} GB. For files this big, `curl -L -C -` in Termux can resume if the connection drops.", $"download a huge file ({sizeText})");
        //  Refuse a download that would not fit on the phone, leaving some space free.
        if (size != null && size > FreeBytes() - (500L << 20)) throw new BrowserException($"Not enough free space on the phone ({FreeGb():0.0} GB free, file is {sizeText}).");

        //  The final confirmation: the user always sees the name, size and source before anything is saved.
        if (!delegated)
            await Ask(key, "Let Claude download this file?", $"{name}\n{sizeText}\nFrom {uri.Host}\nSaved to Download/PocketIris. It is not opened or installed.",
                $"download {name} ({sizeText}) from {uri.Host}");

        Directory.CreateDirectory(DownloadDir);
        var path = Path.Combine(DownloadDir, name);

        //  Never overwrite an existing file: add a number to the name instead.
        for (int i = 1; File.Exists(path); i++) path = Path.Combine(DownloadDir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        long written = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920]; int n;

            //  Copy the file piece by piece.
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                written += n;

                //  Every 64 MB, stop and clean up if the phone is about to run out of space.
                if (written % (64L << 20) < n && written > FreeBytes() - (200L << 20)) { dst.Close(); File.Delete(path); throw new BrowserException("Stopped: the phone is running out of free space."); }
                await dst.WriteAsync(buf.AsMemory(0, n));
            }
        }
        return $"Downloaded {written} bytes to {path} (from {uri.Host}). The file was not opened or installed.";
    }

    /// <summary>The free space left on the phone's storage, in bytes.</summary>
    static long FreeBytes()
    {
        //  If the phone will not say, assume there is plenty.
        try { return new Android.OS.StatFs("/sdcard").AvailableBytes; } catch { return long.MaxValue / 2; }
    }

    /// <summary>The free space left on the phone's storage, in gigabytes.</summary>
    static double FreeGb() => FreeBytes() / 1e9;

    /// <summary>Picks a safe file name from the server's headers or the address, keeping only plain characters.</summary>
    static string FileNameFor(HttpResponseMessage resp, Uri uri)
    {
        var n = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName;
        n = (n ?? Path.GetFileName(uri.AbsolutePath)).Trim('"');
        n = new string(n.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '+' or '(' or ')').ToArray()).Trim('.', ' ');
        return n.Length == 0 ? "download.bin" : n.Length > 120 ? n[^120..] : n;
    }
}

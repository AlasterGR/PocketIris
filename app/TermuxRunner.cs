using Android.Content;
using Android.Content.PM;

namespace PocketIris;

/// <summary>
/// Everything the app asks Termux to do: starting the chat server and its helpers, running a script, and handing over
/// the secrets Termux needs (the terminal password and the tool-server key). Termux runs the commands in the background,
/// with no Termux window, through its RUN_COMMAND service.
/// </summary>
public sealed class TermuxRunner(MainActivity a)
{
    public const string Package = "com.termux";
    public const string Home = "/data/data/com.termux/files/home";
    const string Bash = "/data/data/com.termux/files/usr/bin/bash";
    const string RunCommandPermission = "com.termux.permission.RUN_COMMAND";

    /// <summary>The request number of the permission question, so the activity can tell which answer arrived.</summary>
    public const int RequestRunCommand = 11;

    /// <summary>Returns the password for the terminal server; it is made once at random and remembered.</summary>
    public string TermToken()
    {
        var t = a.Prefs.GetString("termToken", null);

        //  Create and save a new random password the very first time.
        if (t == null)
        {
            t = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            a.Prefs.Edit()!.PutString("termToken", t)!.Apply();
        }
        return t;
    }

    /// <summary>The environment settings handed to Termux whenever the app starts something there: the terminal password and the keys of the tool server and the sidecar.</summary>
    public string Env() => $"export CA_TERM_FONT={(int)Math.Round(14 * Appearance.TextScale)} CA_TERM_TOKEN={TermToken()} CA_MCP_TOKEN={AppSecret.Mcp.Value} CA_SIDECAR_TOKEN={AppSecret.Sidecar.Value};";

    /// <summary>Asks Termux to start the chat server; returns false if something is missing (the overlay explains what).</summary>
    public bool StartServer() => Run($"{Env()} {Home}/bin/ca-start");

    /// <summary>
    /// Starts the terminal server, Remote Control and the sidecar in Termux if they are not running, gives Claude the tool-server key (all four scripts
    /// exit quietly when there is nothing to do), keeps the Claude launcher from updating by itself, lets the guard check a changed Claude Code version
    /// (the app's tool server is up now, which the check needs), records the installed versions for <see cref="VersionCheck"/>, and looks for a newer
    /// Claude Code (a notice only; installing waits for the Updates button).
    /// </summary>
    public void EnsureHelpers() => _ = Task.Run(() =>
    {
        //  A failure here only means the terminal or Remote Control is not started; it is not worth an error screen.
        try { Run($"{Env()} {Home}/bin/ca-term; {Home}/bin/ca-rc; {Home}/bin/ca-mcp-register >> {Home}/ca-mcp.log 2>&1; {Home}/bin/ca-sidecar; {Home}/bin/ca-updates hold-launcher; {Home}/bin/ca-claude-version guard > /dev/null 2>&1; {Home}/bin/ca-versions > /dev/null 2>&1; {Home}/bin/ca-updates check claude > /dev/null 2>&1"); }
        catch (Exception swallowed) { Diag.Swallowed("TermuxRunner.EnsureHelpers", swallowed); }
    });

    /// <summary>
    /// <c>--es cmd run --es script NAME</c>: runs /sdcard/Documents/ca/NAME in Termux, output to /sdcard/Documents/ca/run.log.
    /// Only a plain file name in that folder is accepted, so adb cannot run arbitrary command lines through the app.
    /// </summary>
    /// <param name="name">The script's file name.</param>
    public void RunScript(string? name)
    {
        //  Accept only a plain file name, never a path or a command line.
        if (string.IsNullOrEmpty(name) || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9._-]+$") || name == "run.log") return;
        const string dir = "/sdcard/Documents/ca/";
        Run($"( echo \"== $(date '+%F %T') {name}\"; bash {dir}{name}; echo \"== exit $?\" ) > {dir}run.log 2>&1");
    }

    /// <summary>Asks Termux (in the background, with no Termux window) to run a shell command line.</summary>
    /// <param name="commandLine">The command line, run by bash in Termux's home folder.</param>
    /// <returns>False when Termux is missing or refused (the overlay then explains what to do).</returns>
    public bool Run(string commandLine)
    {
        //  Stop with an explanation if Termux is not installed.
        try { a.PackageManager!.GetPackageInfo(Package, 0); }
        catch
        {
            a.Show("Termux is not installed", "PocketIris needs the Termux app (F-Droid or GitHub build).", busy: false, canRetry: true);
            return false;
        }

        //  Ask the user for the permission to run commands in Termux if it has not been given yet.
        if (a.CheckSelfPermission(RunCommandPermission) != Permission.Granted)
        {
            a.Show("Permission needed", "Allow “Run commands in Termux” so this app can start the server for you.", busy: false, canRetry: true);
            a.RunOnUiThread(() => a.RequestPermissions(new[] { RunCommandPermission }, RequestRunCommand));
            return false;
        }

        //  Send the command to Termux, and explain if Termux refuses it.
        try
        {
            var i = new Intent("com.termux.RUN_COMMAND");
            i.SetClassName(Package, "com.termux.app.RunCommandService");
            i.PutExtra("com.termux.RUN_COMMAND_PATH", Bash);
            i.PutExtra("com.termux.RUN_COMMAND_ARGUMENTS", new[] { "-c", commandLine });
            i.PutExtra("com.termux.RUN_COMMAND_WORKDIR", Home);
            i.PutExtra("com.termux.RUN_COMMAND_BACKGROUND", true);
            a.StartForegroundService(i);
            return true;
        }
        catch (Exception e)
        {
            a.Show("Could not reach Termux", e.Message + "\nIn Termux run:  bash /sdcard/Documents/ca/install.sh", busy: false, canRetry: true);
            return false;
        }
    }
}

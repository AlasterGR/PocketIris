# PocketIris

An Android app for running Claude Code on your phone, in one screen.

PocketIris starts everything Claude Code needs inside [Termux](https://termux.dev) and puts it behind four tabs: **Chat** (a chat page for Claude Code), **Browser** (a browser Claude can use, with your approval), **Terminal** (a shell and ready-made scripts) and **PocketIris** (its own settings). It also gives Claude a set of phone tools (open a site, take a screenshot, read the battery, control an app you approve) behind an approval dialog that a fingerprint touch can answer.

> **Status:** a personal project, built for and tested on one phone (POCO F5, Android 15 / HyperOS 3, arm64). It works there every day; anything else is untested.
>
> **PocketIris is for Claude Code and is not affiliated with or endorsed by Anthropic.** "Claude" and "Claude Code" are Anthropic's names. You need your own Claude account.
>
> This app was developed with the help of AI.

## How it fits together

```
Android app (app/)                                   Termux (official app, unchanged)
 ├─ Chat tab: web view of 127.0.0.1:3400  ───────────►  Yep Anywhere chat server (official npm package, unchanged)
 ├─ tool server for Claude on 127.0.0.1:3401  ◄──────    Claude Code (official build, via the claude-code-android launcher)
 │    (browser, screenshots, phone tools; secret key)     registered as the "phone-browser" MCP server
 ├─ talks to the sidecar on 127.0.0.1:3405  ─────────►  sidecar (termux/ca-sidecar.cjs): notifications, trash and restore,
 │                                                          update checks
 ├─ Terminal tab: web view of ttyd on 127.0.0.1:3402 ►  ttyd + the scripts in termux/scripts/
 └─ starts and stops all of it through Termux's RUN_COMMAND
PC (Windows, tools/): build, deploy, smoke test, health checks, wireless debugging link
```

**The one rule:** Termux, Claude Code and Yep Anywhere run only their official code. Everything PocketIris adds lives around them: the app, small Termux scripts, a sidecar process and a preload shim (`termux/ca-preload.cjs`) that fills two Android gaps without changing Yep's files. The smoke test checks that Yep is byte-for-byte the published package.

## What it does
- **Chat** with Claude Code through Yep Anywhere's web UI, with a "Move to trash" entry added to its session menu (Undo for 10 seconds, a Trash list with Restore).
- **Notifications** when Claude needs an answer, finishes, or a session ends unexpectedly.
- **Claude's phone tools** (an MCP server inside the app): browse public sites in the Browser tab, screenshots, downloads, battery, location, vibrate, clipboard, share sheet, and optionally **phone control** (read the screen and tap or type in apps you approve one by one, through an accessibility service).
- **One approval dialog** for every sensitive request: Allow, Always allow, "Let Claude decide" (a single-use, 10-minute permission slip for exactly that request), Deny. A fingerprint touch counts as Allow. A **trusted session** (30 minutes, one touch) answers routine requests for you, but never downloads, private addresses or safety settings.
- **Updates only when you press the button:** the app compares installed versions with a known-good list (`compat.json`), tells you when something newer is out, and sets a new Claude Code aside if it fails a check.
- **PC tools:** a smoke test of about 100 checks, a Yep deploy that rolls back code and data together on failure, a wireless (Wi-Fi or Bluetooth) debugging link, page inspection over Chrome DevTools.

## Safety model (short)
- The tool server and the sidecar answer only requests with their secret keys, and refuse web pages (`Origin`), wrong `Host` names, wrong content types and oversized bodies.
- The chat page gets only a tiny trash bridge. Only the app's own settings page gets the full bridge, and switching a safety feature **on** always asks natively first.
- Commands that run code in Termux reach the app only through an entry point that Android reserves for the adb shell.
- Phone control needs its switch, Android's accessibility switch and per-app approval, and stops after 60 actions in 10 minutes. Password fields are never typed into.

These protect against other apps and web pages. Claude itself can do whatever you allow it to, so read what it asks before you approve it.

## What you need
- An **arm64 Android phone** (tested on Android 15).
- **Termux** with **Termux:API** and **Termux:Boot** (Termux:Widget optional), all from the **same source**: all GitHub builds or all F-Droid builds, because mixing them breaks them.
- A **Claude account**.
- A **Windows PC** with the .NET 10 SDK and its Android workload (`dotnet workload install android`), a JDK, the Android SDK with `adb`, Node.js and npm, and USB debugging switched on on the phone.

There are no ready-made APKs: you build the app yourself, and it is signed with your own debug key.

## Setting it up

Step 3 onward are the commands used on the test phone. Steps 1 and 2 were done there long ago, so they have not been repeated on a freshly reset phone. If something is missing, please open an issue.

**On the phone, in Termux:**

1. Give Termux access to storage and install the packages:
   ```
   termux-setup-storage
   pkg install nodejs ttyd openssh termux-api jq
   ```
2. Install Claude Code with the community [claude-code-android](https://github.com/ferrumclaudepilgrim/claude-code-android) installer (Anthropic's official linux-arm64 build, patched to run under Termux's glibc; see [the upstream issue](https://github.com/anthropics/claude-code/issues/50270)), then run `claude` once and log in.

**On the PC, from a checkout of this repository (PowerShell):**

3. Copy the Termux files to the phone:
   ```
   adb shell mkdir -p /sdcard/Documents/ca/scripts
   adb push termux\install.sh /sdcard/Documents/ca/
   Get-ChildItem termux -Filter 'ca-*' | ForEach-Object { adb push $_.FullName /sdcard/Documents/ca/ }
   Get-ChildItem termux\scripts -File | ForEach-Object { adb push $_.FullName /sdcard/Documents/ca/scripts/ }
   adb push compat.json /sdcard/Documents/ca/
   ```
4. **In Termux**, run the installer once. It installs the `ca-*` commands, lets the app run commands, and sets up start at boot:
   ```
   bash /sdcard/Documents/ca/install.sh
   ```
5. Build the app. Delete `app\obj` and `app\bin\Release` first, every time, because incremental builds have produced crashing APKs. Then, in `app\`:
   ```
   dotnet publish -c Release "-p:AndroidSdkDirectory=<Android SDK>" "-p:JavaSdkDirectory=<JDK>"
   ```
6. Install it and give it what it needs. The first install may show a question on the phone; answer it there.
   ```
   adb install -r bin\Release\net10.0-android\android-arm64\io.github.alastergr.pocketiris-Signed.apk
   adb shell pm grant io.github.alastergr.pocketiris com.termux.permission.RUN_COMMAND
   adb shell pm grant io.github.alastergr.pocketiris android.permission.POST_NOTIFICATIONS
   adb shell appops set io.github.alastergr.pocketiris MANAGE_EXTERNAL_STORAGE allow
   adb shell am start -n io.github.alastergr.pocketiris/io.github.alastergr.pocketiris.MainActivity
   ```
   The chat will not start yet, because the server is not installed; that comes next.
7. Fetch Yep Anywhere (the version in `tools\yep-version.txt`, checksum-verified) and install it. The deploy also runs the smoke test, which needs page debugging on, so switch that on first:
   ```
   adb shell am start -n io.github.alastergr.pocketiris/io.github.alastergr.pocketiris.ControlActivity --es cmd debug-on
   tools\fetch-yep.ps1
   tools\deploy-yep.ps1 -Tarball deploy\yepanywhere-<version>.tgz
   ```
8. Check everything at any time with `tools\smoke-test.ps1`. It should end with "All checks passed". Keep the phone unlocked while it runs, because some checks show dialogs. Its version checks compare against `compat.json`, the versions on the author's phone, so a newer version on yours is reported as a difference rather than a fault.

**Optional:** to use phone control, switch on "PocketIris phone control" in Android Settings > Accessibility. The wireless link has its own instructions in `tools/BLUETOOTH-DEBUGGING.txt`.

The PC tools find `adb` by themselves: the `POCKETIRIS_ADB` variable, `$LocalAdb` in an untracked `tools\local.ps1`, the PATH, or the Android SDK folders.

## Working on it
- **App change:** build as in step 5, then install as in step 6. Avoid `am force-stop`.
- **Termux script change:** push the file to `/sdcard/Documents/ca/`, then `adb shell am start -n <package>/<package>.ControlActivity --es cmd setup`, which reinstalls the commands and restarts the server.
- **Commands to the app** (`--es cmd`): `update restart stop setup run debug-on debug-off` only through `ControlActivity`, which only the adb shell can reach; `reload browser terminal scripts pocketiris chat` also through `MainActivity`.
- **Look inside a page:** `tools\cdp.ps1 -PageTitle PocketIris -Expr "document.title"` (needs `debug-on`; without `-PageTitle` it targets the chat).
- **Styling:** two tiers of tokens, raw values and meanings. Screens use only the meanings. `app\Tokens.cs` holds the values, and `tools\check-tokens.ps1` fails on a colour or size literal anywhere else.
- **Release builds are trimmed:** no reflection-based JSON (use `Utf8JsonWriter` or manual quoting).

## Repository layout
| Folder | What |
|---|---|
| `app/` | The Android app (.NET 10 for Android, C#, no MAUI): views, approval dialog, tool server, browser, phone tools, settings page (`Assets/pocketiris/`), the trash entry injected into the chat (`Assets/yepshortcut/`) |
| `termux/` | The `ca-*` commands installed into Termux, the sidecar, the preload shim, self-tests, and `scripts/` for the Terminal tab |
| `tools/` | PC tools (PowerShell): smoke test, deploy, health, wireless link, page inspection |
| `compat.json` | The known-good versions of everything PocketIris depends on |

## Credits
- [Yep Anywhere](https://github.com/kzahel/yepanywhere) (MIT licence) provides the chat server and web UI; PocketIris installs it unchanged from npm.
- [Termux](https://termux.dev) and its add-on apps run everything on the phone.
- [claude-code-android](https://github.com/ferrumclaudepilgrim/claude-code-android) makes Claude Code run in Termux.
- [Claude Code](https://github.com/anthropics/claude-code) is Anthropic's; it is not included here and is installed separately.

## Licence
[MIT](LICENSE).

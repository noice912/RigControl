# RigControl on Linux and macOS: how the source is laid out

RigControl started as a Windows app. This page says how the source is split so that a second app, for Linux and
macOS, can share most of it, and what each part may and may not use.

What the first Linux/macOS version is meant to have: a real window with the same interface, settings, Discord
status, and lighting effects through OpenRGB and the lights on your network. Everything else (fans, sensors,
recording, audio, the in-game panel, drivers, CPU and BIOS pages) says "Not available on this system".

**How far this is checked.** No Linux or Mac was used while writing it. Every project that is not Windows-only,
the second app included, builds for `linux-x64`, `osx-arm64` and `osx-x64` on a Windows PC, publishes for all
three, and its tests pass on Windows. Tests on real Linux and macOS machines, and the first real start of the
window (`--smoke`), run on GitHub's build machines through `.github/workflows/ci.yml` ([CI.md](CI.md) says what
each job proves). Until that workflow has run once, none of it has executed anywhere but Windows. Nothing here has
been tried on real Linux or Mac hardware, and the window of the second app has never been opened by anyone.

## The two apps

| | Windows | Linux and macOS |
|---|---|---|
| Project | `src/RigControl.App` | `src/RigControl.Desktop` |
| Window | WPF with WebView2 | Photino.NET (the system's own web view) |
| Starts as | administrator, from a startup task | a normal user, never root |
| Extras | tray icon, overlay, in-game panel, Audio section, first-time setup, helper programs | none of these |

The Windows app stays as it is. It is not being made cross-platform; the second app sits beside it and both use the
shared parts below.

## Which project is built for what

| Kind | Projects | Target |
|---|---|---|
| Platform-neutral | `RigControl.Core`, `RigControl.Hosting`, `RigControl.Desktop`, and the tests `RigControl.Core.Tests`, `RigControl.Hosting.Tests`, `RigControl.Desktop.Tests` | `net8.0` |
| Both flavours | `RigControl.Lighting`, `RigControl.Presence` and their tests | `net8.0` and `net8.0-windows10.0.19041.0` |
| Windows only | `RigControl.App`, `Sensors`, `Nvidia`, `Cooling`, `Capture`, `Drivers`, `Bios`, `Cpu`, `Health`, `Bundled`, everything under `src/Audio`, and their tests | `net8.0-windows` (some `net8.0-windows10.0.19041.0`) |

`Directory.Build.props` says this in one place:

- A project that says nothing gets `net8.0-windows`, exactly as before. No Windows-only project file changed.
- A platform-neutral project sets `<TargetFramework>$(RigPortableTfm)</TargetFramework>`.
- A project with both flavours sets

  ```xml
  <TargetFramework Condition="'$(TargetFramework)' == '$(RigWindowsDefaultTfm)'"></TargetFramework>
  <TargetFrameworks>$(RigDualTfms)</TargetFrameworks>
  ```

  On Windows that builds a neutral dll and a Windows dll. On Linux and macOS `$(RigDualTfms)` is only `net8.0`, so
  no Windows SDK is needed there.

Rules for a project with both flavours:

- The Windows flavour must compile the same code it compiles today, so the Windows app behaves the same.
- Windows-only files are taken out of the neutral flavour in the project file:
  `<Compile Remove="..." Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) != 'windows'" />`.
  Inside a shared file use `#if WINDOWS` (the .NET SDK defines it for the Windows flavour).
- **A Windows project that uses such a project must target `net8.0-windows10.0.19041.0`** (`$(RigWindowsTfm)`).
  A project on plain `net8.0-windows` would silently get the neutral flavour. Today `RigControl.App` and the
  two test projects use the Windows flavour, and all three already target it. `RigControl.Desktop` is `net8.0`
  and gets the neutral flavour, on Windows too (a test checks that no Windows-only type came along).
- Packages that only work on Windows (NAudio, the WinRT projection) are referenced for the Windows flavour only.

The neutral flavour is checked on a Windows PC with, for example:

```
dotnet build src/RigControl.Core -c Debug -r linux-x64
dotnet build src/RigControl.Hosting -c Debug -r osx-arm64
```

The compiler's platform check (warning CA1416) is on for neutral projects: calling a Windows-only API without an
`OperatingSystem.IsWindows()` check is a warning, and the build must stay at zero warnings.

## RigControl.Core (neutral)

Core has no window code and no hardware code. Two places used Windows calls; both now check the system first and do
the same on Windows as before:

- `AppEnvironment.DetectElevated()`: Windows asks whether the process is an administrator. Linux and macOS answer
  "is this root?", which is normally no.
- `ChildProcesses.Attach()`: Windows ties a helper program to RigControl with a job object. Elsewhere it returns
  false and the helper runs on its own.

### What the system offers: `RigControl.Core.Platform`

A module gets these through `ModuleContext.Platform` (an `IPlatformServices`). The standard set is made by
`PlatformServices.Create(dataDirectory)` and is right for the system it runs on, so a host does not have to wire
anything. A host or a test can swap one part: `PlatformServices.Create(dir) with { Autostart = ... }`.

| Part | Windows | Linux and macOS |
|---|---|---|
| `Secrets` (`ISecretProtector`) | `DpapiSecretProtector`: Windows' own protection for the signed-in user. The same call the lighting code uses today, so stored keys stay readable. | `FileKeySecretProtector`: encrypts with a random key kept in `secret.key` in RigControl's folder, a file only its owner can read (mode 0600). See the honest note below. |
| `Processes` (`IProcessQuery`) | Programs in this sign-in session | Linux: programs owned by this user (from `/proc`). macOS: what `ps -x` lists. |
| `Autostart` (`IAutostart`) | Not supported here: the Windows app has its own startup task (`RigControl.App/Autostart.cs`) | Linux: one file, `~/.config/autostart/rigcontrol.desktop`. macOS: one file, `~/Library/LaunchAgents/io.github.rigcontrol.plist`. |
| `Links` (`ILinkOpener`) | Default browser, Explorer | `xdg-open` on Linux, `open` on macOS. Only https links. The address is passed as one argument, never through a shell. |
| `IsElevated`, `Platform` | administrator? / Windows | root? / Linux or macOS |

**Secrets on Linux and macOS, plainly.** There is no keyring use in this version, because that would need extra
system libraries. A pairing key is encrypted before it goes into the settings file, and the encryption key sits
next to it in a file only your user account can open. So a copy of the settings file alone gives nothing away. A
program running as you can read both files, as it could read the settings anyway. PRIVACY.md says the same.

The Windows app does not use these for its own startup task, links or elevation: that code stays in
`RigControl.App` untouched.

### "Not available on this system"

One way for every feature to say it cannot work here (`RigControl.Core.Modules`):

- `ModuleAvailability.Status(why)` gives the status `failed` with a message that starts with
  `Not available on this system`. The interface already hid a page whose module message starts with
  "Not available on this PC" (a feature that does not fit this PC's hardware). It treats the new start the same
  way: the page is left out of the menu and never drawn, Settings lists it calmly as "Not on this system", and
  one panel (`NotOnThisSystem` in `components/ModuleProblem.tsx`) says the sentence wherever a page is asked for
  anyway.
- `UnavailableModule` stands in for a module that has no version for this system. It answers every call with the
  `unavailable` error and the same sentence.
- `ModuleAvailability.Error(why)` is for one method that cannot work here while the rest of the module does.
- `IModuleCapabilities` lets a module that runs on more than one system say which of its parts work here
  (for example `lighting.effects` yes, `lighting.screen` no).

`app.getInfo` now also returns `platform` (`"windows"`, `"linux"`, `"macos"`) and `capabilities` (a sorted list of
names). A module that is really there adds its own name; its parts add `module.part`; the app itself adds
`host.*` names (`host.tray`, `host.elevation`, `host.autostart`, `host.overlay`, `host.audio`, `host.filePicker`,
`host.firstRun`, `host.helpers`, `host.restartPc`). The interface uses the list to hide or explain controls.
On Windows the list has everything, so nothing changes there.

## RigControl.Hosting (neutral)

What both apps need and what has no window code in it:

| File | What it holds |
|---|---|
| `WebUi.targets` | Builds `src/ui` with npm and puts the result inside `RigControl.Hosting.dll`. `-p:SkipUiBuild=true` skips the npm step. (This used to be in the Windows app's project file.) |
| `UiAssets.cs` | Serves those files from memory. Moved from the Windows app. |
| `UiBridge.cs` | `UiMessages.Event` (the `{event, data}` text both apps send) and `UiBridge`, which connects a text-only window to the modules. |
| `AppServices.cs` | `AppServices` and `AppSettings`. Moved from the Windows app. |
| `AppPaths.cs` | Where settings and logs live per system (table below). |
| `ModuleCatalog.cs` | The list of all modules and where each can work; `ModuleCatalog.Build` makes the module list for one system; `HostCapabilities` and `AppInfo` make the `app.getInfo` answer. |
| `HostRuntime.cs` | `HostModules` (register and start modules; the Windows app calls it) and `HostRuntime` (log, settings, event bus, dispatcher and module host in one go, for the second app). |
| `HostAppModule.cs` | The `app` module for the second app: info, modules, settings, logs, links, quit. Windows-only methods answer "Not available on this system." |

Hosting never refers to a feature module. Each app hands `ModuleCatalog.Build` a maker per module it carries; the
rest become stand-ins. That keeps Hosting buildable everywhere.

Where files are kept:

| System | Settings and data | Logs |
|---|---|---|
| Windows | `%LOCALAPPDATA%\RigControl` (unchanged) | `%LOCALAPPDATA%\RigControl\logs` (unchanged) |
| Linux | `$XDG_CONFIG_HOME/RigControl`, normally `~/.config/RigControl` | `$XDG_STATE_HOME/RigControl/logs`, normally `~/.local/state/RigControl/logs` |
| macOS | `~/Library/Application Support/RigControl` | `~/Library/Logs/RigControl` |

On Linux and macOS the folders are made for their owner only.

What stayed in the Windows app on purpose: the WPF windows, WebView2, the overlay and in-game panel, the tray icon,
the startup task, restart as administrator, the first-time setup, the helper programs, the Audio section, and its own
larger `app` module.

## What changed for the Windows app

- The built interface is carried in `RigControl.Hosting.dll` instead of `RigControl.dll`. The published exe is still
  one file and shows the same pages.
- `AppServices`, `AppSettings` and `UiAssets` moved to `RigControl.Hosting` (same code).
- Starting the modules goes through `HostModules` (same steps, same order, same 20-second limit).
- `app.getInfo` has two more fields. The old five are unchanged.
- `RigControl.Core` is built as `net8.0`. It gained a package reference to
  `System.Security.Cryptography.ProtectedData` 8.0.0 (the Windows app already carried it through Lighting).

## RigControl.Lighting and RigControl.Presence (both flavours)

**Lighting.** `OpenRgbClient` only connects to OpenRGB's SDK server on 127.0.0.1:6742; `OpenRgbServer` (Windows
only) derives from it and adds finding and starting OpenRGB. `LightingSystem` is the seam for what a system can
feed the effects with (screen, capture card, OBS, sound): the Windows flavour wires them, the neutral flavour has
none. Lighting keeps its own text-based `ISecretProtector` (in `RigControl.Lighting.Network`), wrapped around
Core's by `PlatformProtector` with the same purpose bytes, so on Windows the stored keys are read exactly as
before. Windows-only files: `ObsSource.cs`, `OpenRgbServer.cs`; the rest uses `#if WINDOWS`.

**Discord status.** `UnixSocketTransport` (`UnixSockets.cs`) reaches Discord through its socket in the user's
private runtime folder and refuses any folder others can open. `RunningApps.cs` gives the app card from process
names. A difference from the plan: `Sources.cs`, `Watching.cs`, `Library.cs` and `Bridge.cs` stay in both flavours
with their Windows parts under `#if WINDOWS`, because the engine needs types that live in them; only
`Sessions.cs`, `Launcher.cs` and `WindowsParts.cs` are left out of the neutral flavour.

## RigControl.Desktop (the Linux and macOS app)

| File | What it holds |
|---|---|
| `Program.cs` | `Main`: the checks before anything starts, the lock, the log, signals, the order of closing |
| `StartOptions.cs` | The command line (`--smoke`, `--ui=`, `--help`, `--version`) and `StartupGate`: the sentences for "not on Windows", "not as root", "no desktop session", "already running", "web view missing" |
| `SingleInstance.cs` | One RigControl per user: `running.lock` in the data folder, held open with no sharing |
| `DesktopSession.cs` | Everything except the window: modules, the `app` module (`HostAppModule`, with the session as its `IHostShell`), `UiBridge`, the page, the smoke check. It talks to the window through `IAppWindow`, so tests run it with a stand-in |
| `PhotinoAppWindow.cs` | The only file that touches Photino.NET |
| `UiSite.cs` | Hands the interface's files to the window from memory and changes three things on the way (below) |
| `PageKey.cs` | The value made for each run that marks messages from RigControl's own page (below) |
| `PageLoader.cs` | Notices whether the page connected, tells a slow page from a refused one, and if needed tries the second way of loading it |
| `MessagePump.cs` | Sends messages to the page from one background thread, so no module ever waits for the window |
| `SmokeProbe.cs` | Watches the messages for "the page asked for `app.getInfo` and said the answer arrived" |

How it starts: refuse on Windows, as root, or on Linux without a desktop session (one sentence, exit code 1);
take the lock (a second start says "RigControl is already running." and exits with 0); open the log and settings;
build the module list with `ModuleCatalog.Build` and makers for `lighting` and `presence` only; start the modules
in the background; open the window. How it ends: the window closing (or `app.quit`, Ctrl+C, SIGTERM, SIGHUP)
stops the message pump, then `HostRuntime` stops the modules, which is where the lights are handed back, then the
lock is let go. There is no tray, no elevation and no second window.

**The page and the two ways of loading it.** Photino can serve a page from memory through a scheme of the app's
own. Vite builds the page with `<script type="module" crossorigin>`, and whether WebKitGTK and WKWebView load a
module script from such a scheme was the open question. What is known from the library's own files: on Linux it
registers the scheme with `webkit_web_context_register_uri_scheme` and nothing more (it is not marked as secure or
as allowed for cross-origin loading), and it has no call to stop the page going somewhere else or to say where a
message came from. Because no Linux or Mac was at hand to try it, the app does not bet on one answer:

1. `scheme`: `app://rigcontrol/index.html`, the files as built.
2. `classic`: the same address with `?classic=1`. `UiSite` rewrites the script tag to a plain deferred script and
   wraps the script in a strict function. This works because a packaged interface is one script file with no
   `import`, `export` or `import.meta` at its top (a test checks this against the real built interface).

Both hand the page over from memory. **The app opens no network port for its page, not even on 127.0.0.1.** (An
early test build had a third way, a small file server on 127.0.0.1; it was removed because the app must open no
incoming port at all. `--ui=loopback` is now an unknown option, and an old `"loopback"` in the settings is ignored
and taken out.)

`PageLoader` gives a way 7 seconds to produce the page's first message. When that time is up it looks at which of
the page's files the window has asked for: if the window has taken both `index.html` and its script for this way,
the page is loading, only slowly (a cold start at sign-in, a busy disk), and it gets up to 25 seconds in all before
anything else is tried. If not, the web view refused it, and the next way is tried. Each step is logged. When
`classic` was needed, that is kept in settings (`desktop.uiMode`) and tried first next time; the usual way writes
nothing. When neither way connects, the window is closed (an empty web view is never left open) and the terminal
and the log say so in plain words. `--ui=scheme|classic` forces one way with no fallback, which is how a build
machine can find out which ways really work:

```
xvfb-run -a ./RigControl --smoke --ui=scheme
xvfb-run -a ./RigControl --smoke --ui=classic
xvfb-run -a ./RigControl --smoke
```

`--smoke` uses a temporary folder and stand-ins for every module (no lights, no Discord, no lock), prints its log
when it ends, and exits with 0 only when the page asked for `app.getInfo`, the answer was handed to the window
**and the page then sent a short note saying the answer arrived** (`bridge.ts` sends it; it is the proof that
messages from the app reach the page). Otherwise it exits with 1 after 60 seconds, saying how far it got. If the
window will not close afterwards the process ends itself 30 seconds later (exit code 3 when the check itself had
passed).

**The page's policy.** For this window `UiSite` replaces the Content-Security-Policy that is built into
`index.html` with a stricter one: nothing loads from anywhere but the app's own files, and `connect-src 'none'`
(the page never connects anywhere; the developer preview's `ws://localhost` allowance is gone). On the scheme the
policy names `app:` next to `'self'`, because it is not known whether these web views count a scheme of the app's
own as "self". A header cannot be set (Photino hands over only the bytes and the type); the files come from memory.

**What Photino does not offer, and what is done instead.**

- *No origin per message.* Photino hands over every message from whatever page the window shows. So each run makes
  a random value (`PageKey`, 32 random bytes as hex), `UiSite` writes it into RigControl's own `index.html` as
  `<meta name="rigcontrol-page-key">`, and `bridge.ts` puts it in front of every message it sends.
  `DesktopSession.FromPage` drops any message without it before anything else happens: it does not count as "the
  page connected", no module sees it, and no events start to flow to the window. The value is never logged.
- *No way to block navigation.* A link or file dropped on the window while RigControl's own script is not running
  (start-up, or a page that never loaded) could still replace the page. Such a page has no key, so it can do
  nothing. The interface has no links that leave it (links go through `app.openExternal` to the user's browser),
  `form-action 'none'` and `base-uri 'none'` are in the policy, and while the interface runs it stops drops.
- *Nothing goes out to a page that has gone quiet.* The key protects messages into the app; the window cannot tell
  where messages out of it land. So RigControl's own page says "still here" (with the key) every 2 seconds, and
  `DesktopSession` hands nothing to the window once nothing keyed has arrived for 6 seconds (`PageQuietLimit`).
  A page that replaced RigControl's gets at most those few seconds of events. A window hidden long enough for the
  web view to slow its timers also stops receiving until it is shown again; values that update on their own catch
  up, others refresh on the next change.
- *Web view settings.* Photino.Native 4.0.22 switches on, on Linux, universal access from `file://` pages, top-level
  navigation to `data:` addresses and opening windows without a click. RigControl passes JSON through
  `SetBrowserControlInitParameters` that switches all of these (and file access from `file://` pages) off, so a
  dropped local file cannot read `app://rigcontrol/index.html` and its key. On macOS the same call sets
  `javaScriptCanOpenWindowsAutomatically` off on the web view's preferences (macOS allows it by default), and
  `SetFileSystemAccessEnabled(false)` turns off `allowFileAccessFromFileURLs`; universal file access is not switched
  on there by the library. How Photino applies these was read from its native source (master) and from the names in
  the 4.0.22 binaries; it has not been seen working on a real Linux PC or Mac.
- The context menu and the developer tools are off.

**What `PhotinoAppWindow` relies on, checked by a test that reads the library's code**: `PhotinoWindow.Invoke` runs
the work at once when it is already on the window's thread, and `SendWebMessage` and `Close` go to that thread by
themselves. So calling them inside `Invoke`, as the app does, cannot wait for itself.

Publishing: `dotnet publish src/RigControl.Desktop -c Release -r linux-x64` (or `osx-arm64`, `osx-x64`) makes a
self-contained folder, not a single file: `Photino.Native.so` / `.dylib` must sit next to the program, and a
single file would unpack it to a temporary folder at each start. Publish on the system it is for: a copy made on
Windows has no executable bit and, for a Mac, no signature. There is no `.app` bundle, `.desktop` entry or package
yet; the program is started from its folder. On Windows the project builds (so the solution is one build) but makes
no `RigControl.exe`, and its `Main` refuses to run there.

## Still to do (each is its own piece of work)

1. **The first run of the GitHub workflow.** The workflow is written (`.github/workflows/ci.yml`, described in
   [CI.md](CI.md)) but has never run. It builds and tests the neutral projects on Linux, macOS and Windows, and
   does the three `--smoke` starts above under `xvfb-run` on Linux. That run is the first time the window, the
   scheme, the message channel, the web view settings, the Unix-only file modes and the Discord socket checks
   execute at all, so expect fixes. If `scheme` passes, `classic` can stay as an unused fallback. If neither passes,
   the window cannot show the interface on that system: the answer must be a fix inside the window (never a local
   server: the app opens no port). No macOS window is opened by any job.
2. A real try on a Linux PC and a Mac: OpenRGB's SDK server, a real Discord (also the Flatpak and Snap ones), the
   start-at-sign-in files, window size and fonts.
3. Packaging: a `.app` bundle and signing for macOS, a `.desktop` entry and an icon for Linux.

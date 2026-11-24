# Audio Controller inside RigControl: merge notes

The Audio section of RigControl is the user's standalone **Audio Controller** app (soundboard, virtual microphone
through VB-Audio Cable, Equalizer APO EQ/effects, "Also play on" mirroring, encrypted library). The code and XAML
were reused, not rewritten. This file lists what was copied, every change and why, how RigControl hosts it, and
what is still limited.

## 1. Source

- Copied from the Audio Controller source folder (read-only), git commit `fb1feca` ("License under GPL-3.0; add
  Terms of Use, Terms of Service and Privacy Policy") **plus the user's uncommitted work** at the time of the copy:
  - `src/AudioController.App/AudioController.App.csproj`: `<Version>1.6.0</Version>` (was 1.5.0). Kept.
  - `AudioController.sln` adds `AudioController.Setup` and `AudioController.Setup.Tests`; the untracked
    `src/AudioController.Setup/`, `tests/AudioController.Setup.Tests/`, `scripts/` and `docs/INSTALLER.md` are the
    standalone app's installer. **Not copied**: RigControl has its own packaging, and the installer downloads and
    installs third-party drivers, which RigControl must not do.
- Copied projects (under `src/Audio/`): `AudioController.Core`, `AudioController.Storage`, `AudioController.Decode`,
  `AudioController.Audio`, `AudioController.App`; tests: `tests/Audio/AudioController.Tests` and
  `tests/Audio/DeviceManualTests.md` (the manual device checklist; it still describes the standalone window).
- `third_party/ffmpeg/ffmpeg.exe` and `ffprobe.exe` are byte-identical to the original `tools/ffmpeg/` (FFmpeg 9.0.2
  essentials build from gyan.dev, GPL-3.0), with `README.md` (the original `tools/ffmpeg/README.md` plus an
  "In RigControl" section), `THIRD_PARTY_NOTICES.md` (the original's, unchanged) and `COPYING.GPLv3.txt` (the
  original's `LICENSE`, the GPL-3.0 text).
- Every other source and test file was compared by SHA-256 with the original and is **byte-identical**. The only
  differences are the ones listed in section 2.

## 2. Changes, with reasons

### Projects

| File | Change | Why |
|---|---|---|
| all 5 copied `*.csproj` and the test csproj | `<LangVersion>12</LangVersion>` | The original's `Directory.Build.props` pinned 12; RigControl's says `latest`. |
| `AudioController.App.csproj` | `OutputType` `WinExe` -> `Library`; references `AudioController.Ffmpeg`; imports `FfmpegPayload.targets` | It is hosted in RigControl's window (no `Application`); FFmpeg travels inside the exe; the import records the FFmpeg files' SHA-256s in `AudioController.dll` itself (section 3). |
| `AudioController.Decode.csproj` | `InternalsVisibleTo AudioController.Tests` | Tests check `FfmpegLocator.GetCandidates` (the search order). |
| `AudioController.Tests.csproj` | paths `..\..\..\src\Audio\...`; references `AudioController.Ffmpeg`; copies `third_party/ffmpeg/*.exe` to `<test bin>\ffmpeg\`; copies the audio XAML to `<test bin>\AudioXaml\` | RigControl's build output lives under `%TEMP%` (long source path), so the locator's DEBUG dev-tree walk (`AudioController.sln` + `tools\ffmpeg`) can no longer reach FFmpeg; the published layout (`<exe dir>\ffmpeg\`) keeps the real-FFmpeg decoder tests running instead of returning early. The XAML copy feeds the resource-key test. |
| new `AudioController.Ffmpeg.csproj` + `EmbeddedFfmpeg.cs` | Embeds `ffmpeg.exe`, `ffprobe.exe` (the two tools the code runs: `FfmpegDecoder` runs ffmpeg for every import and ffprobe to report the real length of an over-long file) and their three notice files as resources `ffmpeg/<name>`. | RigControl ships as one exe. Own assembly so the ~200 MB payload is not re-embedded on every UI change and is only loaded when it has to be written out. The notices travel with the GPL binaries. |
| new `AudioController.Ffmpeg/FfmpegPayload.targets` | The list of payload files and a build target that writes each file's SHA-256 into `[AssemblyMetadata("ffmpeg-sha256:<name>", ...)]` of the importing assembly. Imported by `AudioController.Ffmpeg` (payload + hashes) and `AudioController.App` (hashes only). Hashes on every build, on purpose: a swapped binary with an old timestamp must not leave a stale hash. | One definition of "what is embedded"; the host assembly knows the expected hashes without touching the payload assembly. |

### Removed

- `App.xaml` / `App.xaml.cs`: replaced by `Hosting/AudioHost.cs` (composition root) and `Themes/AudioResources.xaml`
  (resources). RigControl owns the one `Application`.
- `MainWindow.xaml` / `.xaml.cs`: became `AudioView` (below).

### `Themes/AudioResources.xaml` (new)

Exactly what `App.xaml` put in `Application.Resources`: `Themes/DarkTheme.xaml` merged, plus the converters under the
same keys (`BoolToVis`, `InverseBoolToVis`, `NullToVis`, `InverseNullToVis`, `ZeroToVis`, `InverseBool`, `HexToBrush`,
`HexToForeground`, `EqualsMulti`). Pack URI: `pack://application:,,,/AudioController;component/Themes/AudioResources.xaml`
(`AudioHost.ResourcesUri`). A test scans every `StaticResource`/`DynamicResource` key in all audio XAML files and
checks it resolves with only this dictionary merged.

### `AudioView.xaml` (was `MainWindow.xaml`)

- Root `Window` -> `UserControl`, `Window.Resources` -> `UserControl.Resources`. Content unchanged.
- Removed the window-only attributes `Title`, `Width=1280`, `Height=760`, `MinWidth=1000`, `MinHeight=660`.
- One binding: `RelativeSource AncestorType=Window` -> `AncestorType=local:AudioView` (the slot editor's visibility
  read `DataContext.HasSelectedSlot` from the window; inside RigControl the window's DataContext is not the audio view
  model).

### `AudioView.xaml.cs` (was `MainWindow.xaml.cs`)

Every handler (import, drag and drop, slot click/hold, board rename, IUiService) is the original code. Window-only
dependencies were re-homed:

| Original (MainWindow) | Now |
|---|---|
| `DarkTitleBar.Attach(this)` | Dropped: the title bar is RigControl's. `SettingsWindow`/`ReportWindow` still attach it. |
| `RestoreWindowPlacement` / `SaveWindowPlacement` | Dropped; the host owns window placement. `AppSettings.WindowLeft/Top/Width/Height/Maximized` are kept in the library unchanged (the standalone app still reads them). |
| `SourceInitialized` -> `new HotkeyManager(this)` | `AttachToWindow(host)` (called by `AudioHost.AttachToWindow`): `HotkeyManager` on the **host window's** HWND; `HotkeyCaptureBox.CaptureStarted/Ended` -> `Suspend/Resume`; `vm.AttachHotkeys`. Same failure message in the status bar. |
| `Activated += vm.OnWindowActivated` | Host window's `Activated` is forwarded. |
| `Closing` -> unhook capture events, `vm.Shutdown()` | `Detach()` unhooks (host window + capture events); `AudioHost.Dispose` then calls `vm.Shutdown()` (final save, Windows default output back to the headset, hotkeys disposed, engine/routers/devices released). |
| `MessageBox.Show(this, ...)`, `SettingsWindow { Owner = this }`, `OpenFileDialog.ShowDialog(this)`, `ReportWindow` owner | `DialogOwner()`: `Window.GetWindow(view)` if visible, else the host window if visible, else no owner (the dialog centers on screen). A collapsed view still sits in the host window, so its dialogs are owned by it; WPF refuses an owner that was never shown (RigControl starting hidden in the tray), hence the visibility check, as the original `ShowReport` already did. |
| `ShowEffects()`: un-minimize + `Activate()` this window | Same on `Window.GetWindow(view) ?? host window`, then `EffectsPanel.BringIntoView()` / `FocusPanel()` as before. |

Nothing references the old `App` class or `Application.Current.MainWindow`. `EffectsPanel` (unchanged) already
found its window with `Window.GetWindow(this)` and stops the spectrum capture whenever it is not visible, which now also
covers "another RigControl section is shown" (the view is collapsed) and "RigControl is hidden in the tray".

### `Hosting/AudioHost.cs` (new; the contract RigControl.App calls)

`AudioHost.Start(Dispatcher)` does what `App.OnStartup` did, in the same order:

1. Must be on the UI thread (otherwise `Failed`, nothing opened). Merges `ResourcesUri` into
   `Application.Resources` if the host has not (safety net).
2. Single instance: `new Mutex(false, @"Local\AudioController.SingleInstance.v1", out createdNew)` (same name as the
   standalone app). Not new -> `AlreadyRunningElsewhere` with a plain message telling the user to close the standalone
   Audio Controller and restart RigControl; nothing else is opened. An access-denied mutex (an elevated standalone copy)
   counts the same. The handle is **not owned**: the standalone app only checks `createdNew`, so holding the handle
   blocks it just the same, and closing an unowned handle cannot throw `ApplicationException` from another thread
   (the original's `ReleaseMutex` could).
3. Vault: `Vault.OpenDefault()` + `LoadLibrary()`. `CryptographicException`/`InvalidDataException`/`JsonException`
   -> `VaultUnreadable` with the original's message text; nothing is saved (the vault is only disposed, which wipes its
   in-memory key), no audio object is created, the lock is released.
4. Decoder: registers the embedded FFmpeg with `FfmpegLocator` (section 3), `FfmpegLocator.Find()`, `new FfmpegDecoder`;
   none -> `decoder = null` -> import disabled with the original "FFmpeg not found" behaviour.
5. `DeviceManager`, `AudioEngine`, `SystemEqRouter`, `MirrorRouter`, `MainViewModel(...)`, `AudioView(vm)`,
   `State = Running`, `vm.Start()`.
6. Any other exception -> `Failed`, `Message = "Audio Controller could not start.\n\n" + ex.Message`, and the original
   `CleanUp` (view model shutdown, or the individual services when there is no view model yet), vault and lock released.

Instead of message boxes and `Shutdown(n)`, problems end up in `State`/`Message`, and `View` is then a small themed
panel showing the message (RigControl keeps running).

- `AttachToWindow(window)`: once; ignored when not running or after Dispose; marshals to the UI thread.
- `OnSessionEnding()`: `vm.SaveNow()` + `vm.RestoreWindowsDefaultForExit(final: false)` (= `App.OnSessionEnding`).
- `EmergencyRestore()`: `vm.RestoreWindowsDefaultForExit()`; any thread, never throws (= the restore part of
  `App.OnDomainUnhandledException`).
- `Dispose()`: idempotent and never throws. Same order as `MainWindow.Closing` + `App.OnExit`: view detach,
  `vm.Shutdown()` (or the services), vault dispose, lock handle closed. Called from another thread it runs the
  clean-up on the UI thread (hotkeys and timers belong to it) while that dispatcher is alive. That wait is bounded
  (10 s for the UI thread to *start* the shutdown): if the UI thread is stuck, the calling thread does the clean-up
  itself, so the library is still saved, the default output restored and the lock released instead of hanging exit.
- Internal `AudioHostOptions` lets tests swap every service (vault, devices, engine, routers, decoder, Equalizer APO
  detection, mutex name) for fakes.

### `AudioController.Decode`

- `FfmpegLocator`: new `SetEmbeddedDirectoryProvider(Func<string?>)`. When registered (only by `AudioHost`), the
  folder it returns is tried **first**; the provider runs once (lazy, thread-safe), and null / relative path /
  exception simply skips it. All original locations follow unchanged (`<exe dir>\ffmpeg\`, `<exe dir>\`, DEBUG dev
  tree, absolute `PATH` entries). Without a registration the search order is the original one.
- New `FfmpegExtractor` (section 3): `FromAssembly` (names, hashes and bytes from one assembly), `FromManifest`
  (names and hashes from one assembly, bytes from another that is only asked for when a file must be written),
  `ComputeKey`, `TryExtract`, `DefaultRoot`.

## 3. FFmpeg inside the exe

- `AudioController.Ffmpeg.dll` carries the payload (section 2). `AudioController.dll` carries the same build-time
  SHA-256s. `AudioHost` uses `FfmpegExtractor.FromManifest(<AudioController.dll>, <load AudioController.Ffmpeg>)`:
  on a normal start the copy on disk is checked against the hashes alone and the payload assembly is **never loaded**.
  This matters because RigControl is published as a compressed single file, where loading an assembly means
  decompressing all of it into memory (about 200 MB that would stay for the life of the process). It is loaded only
  on the first run, after an FFmpeg update, or to replace a damaged copy.
- First need (`AudioHost.Start`, where the original looked FFmpeg up): files go to
  `%LOCALAPPDATA%\RigControl\ffmpeg\<key>\`, `<key>` = first 16 hex digits of the SHA-256 of the payload manifest
  (one `name:sha256` line per file, sorted), so a new FFmpeg build gets a new folder.
- Each file: reused if present and its SHA-256 matches; otherwise written to a unique `.tmp` in the same folder,
  flushed to disk, hashed again from disk, then moved into place (atomic replace). Mismatch or any error -> null, no
  temp file left, and the locator continues with the original locations, then "import disabled".
- After a successful extraction, older 16-hex payload folders and `.tmp` files older than 10 minutes are removed
  (best effort).
- Cost: first run writes ~200 MB (a second or two, and in the compressed single-file exe the payload assembly stays
  in memory until RigControl is closed that one time); later starts re-hash both tools (~0.25 s warm cache) on the UI
  thread, at the same point where the standalone app did its file lookup.

## 4. How RigControl.App uses it

1. `OnStartup`: `Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AudioHost.ResourcesUri })`
   before any view exists.
2. `AudioHost.Start(Dispatcher)` on the UI thread; it never throws. `State`/`Message` feed the `app` RPC status.
3. `View` goes in the ContentControl next to the navigation rail (it may be collapsed while other sections show,
   also while the window is hidden in the tray).
4. `SourceInitialized` -> `AttachToWindow(mainWindow)`. When starting hidden, the host calls `EnsureHandle()` so
   soundboard hotkeys work from the tray.
5. Windows logoff -> `OnSessionEnding()` (not final). `AppDomain.UnhandledException` -> `EmergencyRestore()`.
6. Exit -> `Dispose()` on the UI thread, before the window is force-closed.

## 5. Tests

- Baseline: the **original** suite, run from a temporary copy outside `Audio sources`
  (`%TEMP%\rigcontrol-audio-baseline`: the five projects, the tests, `tools/ffmpeg`, `Directory.Build.props`,
  `NuGet.Config`, `AudioController.sln`): 797 passed, 0 failed (re-run on 2026-11-15).
- Ported suite: the same 797 original tests (unchanged files) plus 45 new ones, 842 passed, 0 failed, Debug and
  Release:
  - `AudioHostTests` (13): pack URI, mutex name, standalone holds the lock -> `AlreadyRunningElsewhere` with nothing
    opened, second host refused until the first is disposed, undecryptable vault -> `VaultUnreadable` with nothing
    saved and the lock released, service failure -> `Failed`, off-UI-thread start, Dispose order (save, engine, vault,
    lock) and idempotence, Dispose from another thread runs on the UI thread, Dispose from another thread while the
    UI thread is stuck still saves and releases the lock without throwing, session-ending/emergency restore,
    AttachToWindow once and not after Dispose. All with fakes and unique mutex names; the real vault is never opened.
  - `AudioViewSmokeTests` (10): STA `Application` with only `AudioResources.xaml` merged; `AudioView` laid out,
    `SettingsWindow` and `ReportWindow` created with handles but never shown, view model built like the
    `MainViewModel` tests; every XAML resource key resolves; converter keys present; a collapsed view in a
    never-shown window; no FFmpeg -> import disabled; hotkey capture events hooked on attach, unhooked on dispose.
  - `FfmpegExtractorTests` (22): small fake payloads in temp folders: extract, key, reuse without rewrite, damaged
    copy replaced, bad hash refused with nothing left, unreadable payload, path-like names refused, relative root,
    old folders cleaned, concurrent first use, real embedded metadata matches the shipped binaries, the host
    assembly carries the same names and hashes as the payload assembly, an intact copy is reused without asking for
    the payload assembly, a damaged copy asks for it and an unavailable payload falls back, locator order. One test is
    opt-in (`RIGCONTROL_TEST_REAL_FFMPEG_EXTRACT=1`; without it the test returns at once): it extracts the real
    payload into a temp folder, verifies it and checks the second pass reads nothing from the exe; it passed here.
    Nothing ever writes to `%LOCALAPPDATA%\RigControl`.
- Run: `$env:RIGCONTROL_ARTIFACTS = "$env:TEMP\rigcontrol-build-audio"; dotnet test tests\Audio\AudioController.Tests\AudioController.Tests.csproj`.

## 6. Known limitations

- **Implicit styles are app-wide.** `DarkTheme.xaml` has implicit styles (Button, ToggleButton, CheckBox, TextBox,
  Slider, ComboBox, TabControl, ScrollBar, ToolTip, ContextMenu, MenuItem, GridSplitter, Expander, ...). Merged into
  `Application.Resources` (as the contract requires) they also style any plain WPF control RigControl.App creates
  itself. Today that is only the fallback panels (they set their colors explicitly) and the tray menu is WinForms, so
  nothing visible changes.
- **Unhandled-exception UI moved to the host.** The standalone app's `DispatcherUnhandledException` handler (message
  box, close after 8 errors) and its fatal message box on `AppDomain.UnhandledException` are not reproduced: RigControl
  logs and keeps running. `EmergencyRestore` keeps the important part (never leave Windows on the virtual cable).
- **No message boxes at start.** "Already running", "undecryptable" and "could not start" are shown in the Audio
  section (`View`) instead of a modal box, and RigControl keeps running.
- **Lock held for RigControl's lifetime.** If the standalone app is running when RigControl starts, the Audio
  section stays off until RigControl is restarted after closing it. While RigControl runs, starting the standalone app
  shows its own "already running" box.
- **Window size.** The old window minimum was 1000 x 660 for the audio content alone. RigControl's window currently
  has `MinWidth="1000"` including its 232 px navigation rail, so the Audio section can be squeezed to about 768 px,
  narrower than the standalone app ever allowed; the right-hand panels get cramped there. The host should use a
  minimum of about 1232 x 660 (client area) while the Audio section is shown. Window placement is RigControl's.
- **Dialogs while hidden.** A report or message raised while RigControl is hidden in the tray (for example a hotkey
  conflict) opens without an owner, centered on screen, as the original `ShowReport` already did when its window was
  hidden.
- **FFmpeg extraction** needs ~200 MB free in `%LOCALAPPDATA%`; when it fails, the original locations
  (`<exe dir>\ffmpeg\`, `<exe dir>\`, `PATH`) are tried and then import is disabled. Startup re-hashes the tools each
  time.
- **Not run here:** the manual device checklist (`tests/Audio/DeviceManualTests.md`) and anything that needs real
  audio hardware, VB-Cable or Equalizer APO; the tests use fakes only. The view was laid out off-screen but not shown
  inside the real RigControl window.

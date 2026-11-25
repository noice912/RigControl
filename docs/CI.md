# The automatic checks (GitHub Actions)

Every push to `main` and every pull request starts the checks in `.github/workflows/ci.yml`. They run on GitHub's
own machines: one Linux, one macOS and one Windows machine, all fresh each time.

This page says what each check proves, and what it does not.

## The short version

- The checks build the code and run its tests on Linux, macOS and Windows.
- One check opens the Linux window on a machine with no screen and waits for the interface to talk to the app.
- **Nothing is tried on real hardware.** No OpenRGB server, no Discord, no lights, no fans and no sensors are
  contacted. The tests use stand-ins and only ever connect to the machine itself (127.0.0.1).
- The Windows app itself (`RigControl.exe`) is **not** built by these checks. The reason is below.
- So the honest label for Linux and macOS is: *builds and passes its tests on GitHub's Linux and macOS machines;
  not tried on real hardware.*

## The five jobs

### 1. Interface

Runs on Linux. Installs the interface's packages exactly as `src/ui/package-lock.json` lists them (`npm ci`), checks
the types (`npm run typecheck`) and builds it (`npm run build`).

Proves: the interface compiles from a fresh copy of the source.
Does not prove: that any page looks right or works. Nobody looks at it.

### 2. Shared code (Linux, macOS, Windows)

Builds and tests only the parts that are meant for every system:

| Built | Tests run |
|---|---|
| `src/RigControl.Core` | `tests/RigControl.Core.Tests` |
| `src/RigControl.Hosting` (this also builds the interface) | `tests/RigControl.Hosting.Tests` |
| `src/RigControl.Lighting`, neutral flavour | `tests/RigControl.Lighting.Tests`, neutral flavour |
| `src/RigControl.Presence` (Discord status), neutral flavour | `tests/RigControl.Presence.Tests`, neutral flavour |
| `src/RigControl.Desktop` (the Linux and macOS app) | `tests/RigControl.Desktop.Tests` |

Every warning is an error here. That is how a call that only exists on Windows is caught: the compiler warns about
it (CA1416) and the job fails. Two kinds of warning stay warnings, because they depend on the date and not on the
change being checked: a security notice for a package (NU1901 to NU1904) and the note that .NET 8 is past its
support date (NETSDK1138). They still show in the log.

`RigControl.slnx` is not built on Linux or macOS, because it also lists the Windows-only projects.

Proves: the shared code compiles on all three systems with no Windows-only call in it, and its tests pass there.
This is the first place the Linux- and macOS-only tests really run: the key file that only its owner may read, the
folder checks before Discord's connection is trusted, and the build step that seals the Discord ids.
Does not prove: that Discord accepts a status card, that OpenRGB answers, or that a network light changes colour.
The tests talk to stand-ins written for the tests.

### 3. Windows projects

Runs on Windows. Builds every Windows project that can be built from a fresh copy of the source, and runs their
tests.

Built and tested: Bios, Capture, Cooling, Cpu, Drivers, Health, Nvidia, Sensors, and the Windows flavour of Lighting
and of Discord status.
Built only: `RigControl.Hosting` (its tests run in job 2), `RigControl.Bundled`, `AudioController.Core`,
`AudioController.Storage`, `AudioController.Decode` and `AudioController.Audio` (the tests for these five are in
the two test projects that are left out, below).

**Left out, and why:**

| Left out | Why |
|---|---|
| `src/Audio/AudioController.Ffmpeg` | It carries `ffmpeg.exe` and `ffprobe.exe` inside it. Those two files are not in the repository (each is over GitHub's 100 MB limit for one file; see `third_party/ffmpeg/README.md`), and the build stops when they are missing. |
| `src/Audio/AudioController.App` | Records the two files' checksums at build time, so it stops the same way. |
| `tests/Audio/AudioController.Tests` | Needs the two projects above. |
| `src/RigControl.App` (the Windows app) | Contains the Audio section, so it needs `AudioController.App`. |
| `tests/RigControl.App.Tests` | Needs the Windows app. |

There is **no switch today** that builds these without FFmpeg. Two ways to close the gap, neither is in place:

1. A step in this job that downloads the FFmpeg build named in `third_party/ffmpeg/README.md`, checks its SHA-256
   against the value written there, and unpacks the two files before building. This adds a download from a third
   party to every run and would let the job build `RigControl.App` and run the last two test projects.
2. A build property (for example `-p:FfmpegPayload=none`) that makes the two audio projects build with nothing
   inside, for checking only. This needs a change to `src/Audio/AudioController.Ffmpeg/FfmpegPayload.targets` and a
   look at the tests that expect the files to be there.

Until one of them is done, the Windows app is built and tested on the developer's PC only.

Two more things are missing from a fresh copy, and the build copes with both:

- **The helper programs** (OpenRGB, PawnIO, Equalizer APO) under `third_party/` are not in the repository.
  `RigControl.Bundled` builds without them and simply carries nothing (`third_party/BUNDLED.md`).
- **The Discord application ids** (`private/discord-ids.json`) are not in the repository. Discord status builds
  without them, and such a build says "Discord status isn't set up in this build". The tests use made-up ids.

Proves: these Windows projects compile from the public source and their tests pass on a Windows machine with none
of the hardware.
Does not prove: anything about `RigControl.exe`, the Audio section, real fans, sensors, graphics cards or lights.

### 4. Linux window starts (headless)

Runs on Linux after job 2. It installs the system web view and the notification library the window library also
needs (`libwebkit2gtk-4.1-0 libnotify4`) and a virtual screen (`xvfb`), publishes `RigControl.Desktop` for
`linux-x64` and starts it with `--smoke`.

What `--smoke` does: the app uses a throwaway folder instead of your settings, opens its window with stand-ins
instead of the real modules (no lights and no Discord are touched), loads the interface, and waits until the page
itself has asked `app.getInfo` over the bridge, the answer was handed to the window, and the page has then sent a
short note saying that answer arrived. Only that note shows that messages from the app reach the page, so the check
does not pass without it. Then it closes and exits with 0. If that has not happened after 60 seconds it writes how
far it got and exits with 1 (or 3 if the window would not close). It prints its log before it exits. The job adds a
hard limit of 2 minutes around it in case it hangs.

The app has two ways of loading its page (`--ui=scheme`, `classic`), both from memory, and normally tries them in
that order until one connects. Neither opens a network port. The log of the required run says which one worked. A
second step then asks for each way by itself and writes down the two exit codes. That step is for information only
and never fails the job.

When the required run fails, what the app printed (its log is part of that) is kept for 7 days as the artifact
`smoke-logs`, together with the log files it left behind if it had to be stopped from outside. Nothing else from
its folder is uploaded.

Proves: on GitHub's Linux machine the published app starts, the window library loads, the interface is served from
inside the app and loads in the system web view, and messages go both ways between the page and the app (the
page's own note that the answer arrived is the proof of the way back).
Does not prove: that the window looks right (nobody sees it), that it works on a real desktop with a graphics card
(the check draws in software), or anything about macOS. No macOS window is ever opened by these checks.

This is the only real evidence that the Linux window works, so it should stay a required check for `main`.
(Marking a check as required is done in the repository's branch settings on GitHub, not in the workflow file.)

### 5. Package

Runs after job 2. Publishes `RigControl.Desktop` with its own .NET runtime for `linux-x64` (on Linux) and for
`osx-arm64` and `osx-x64` (on macOS), and packs each into a `.tar.gz` so the program keeps its "can be run" mark.
The three files are kept for 7 days with the run.

These are try-out builds, **not releases**: no version tag, no GitHub release, no signature.

- **macOS: the build is not signed and not notarised.** Gatekeeper will block it the first time. To run it you have
  to allow it yourself (System Settings > Privacy & Security > "Open Anyway", or take the quarantine mark off the
  unpacked folder). It is a plain folder with a program in it, not a `.app` bundle.
- **Linux:** the system web view and the notification library must be installed (Debian and Ubuntu:
  `sudo apt install libwebkit2gtk-4.1-0 libnotify4`; Fedora: `sudo dnf install webkit2gtk4.1 libnotify`). The window
  library needs both; the web view package does not bring in the second one.
- To download an artifact you must be signed in to GitHub.

Proves: the app can be published for the three targets.
Does not prove: that the packed app starts. Only the Linux one is started, in job 4. The Intel Mac build
(`osx-x64`) is made on an Apple-silicon machine and never run.

## How the workflow is kept safe

- It uses **no secrets**. Its token may only read the source (`permissions: contents: read`).
- It runs on `push` to `main` and on `pull_request`. It never uses `pull_request_target`, so code from a fork never
  runs with more rights than "read".
- Every action is pinned to one exact commit, with the version written beside it:
  `actions/checkout` v7.0.1, `actions/setup-dotnet` v6.1.0, `actions/setup-node` v7.1.0,
  `actions/upload-artifact` v7.0.2. To update one, change the commit and the comment together.
- Nothing is cached between runs.
- A newer run on the same branch or pull request cancels the older one. Every job has a time limit.
- No text from a pull request (title, branch name, commit message) is put into a command.

## Versions

- **.NET:** the projects target .NET 8. The checks build with the .NET 11 SDK (the one the code is written with)
  and install the .NET 8 runtime to run the tests. Each job writes a small `global.json` on the build machine so
  the same SDK line is used every time; that file is not part of the repository. If the repository gets its own
  `global.json`, the jobs use it and the `dotnet-version` lines in the workflow should be changed to match.
- **.NET 8 is past its support date** (November 2026). The packed Linux and macOS apps carry that runtime. Moving
  the projects to a supported .NET is a separate piece of work.
- **Node.js:** 24.

## What has and has not been checked about the workflow itself

The workflow was written on a Windows PC and could not be run there. It was checked by reading: the four actions
exist at the pinned commits and take the inputs used, and the paths exist in the source. The build and test
commands for `RigControl.Core` were tried on Windows with the same switches. Everything else, above all the
headless start, is first tried when the workflow runs on GitHub. Expect the first run to need small fixes.

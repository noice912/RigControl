# RigControl

One Windows app for a gaming PC. It shows temperatures, runs fans and lights, changes graphics settings, records the
screen, shows what you play on Discord and carries a soundboard, so you need fewer maker apps running at once.

It is free software (GPL-3.0), made by one person and tested on one PC. Read "Will it work on my PC?" before you rely
on it.

- [Will it work on my PC?](#will-it-work-on-my-pc)
- [What it installs and why it runs as administrator](#what-it-installs-and-why-it-runs-as-administrator)
- [Installing](#installing)
- [What each page does](#what-each-page-does)
- [Safety](#safety)
- [Building from source](#building-from-source)
- [Privacy](PRIVACY.md), [Security](SECURITY.md), [Third-party notices](THIRD-PARTY-NOTICES.md)

## Will it work on my PC?

RigControl needs 64-bit (x64) Windows 10 or Windows 11 and Microsoft's WebView2 runtime, which Windows 11 already
has. It was developed and tested on Windows 11 only.

The first time it starts it looks at your PC and only shows the pages and features that fit it. Nothing is changed by
that look.

### Developed and tested on

One desktop PC with these parts. Everything in this list is used daily.

- Intel Core i9-13900KF on an ASUS ROG STRIX Z790-A GAMING WIFI II motherboard
- NVIDIA GeForce RTX 4070 Ti (a Gigabyte card)
- NZXT Kraken Elite (2023) liquid cooler and NZXT RGB & Fan Controller
- DDR5 memory with lights, a Razer keyboard and mouse, Nanoleaf Lines
- A USB capture card

### Should work, but not tested by the author

| Feature | Expected to work with |
|---|---|
| Temperatures, load, memory | Any PC |
| Motherboard fans | Boards that [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) can control |
| Graphics page, graphics card fans | Other NVIDIA GeForce cards with a current driver |
| Recording and instant replay | NVIDIA cards with NVENC (GTX 10-series or newer) |
| Lighting | Devices that [OpenRGB](https://openrgb.org) 1.1 supports |
| Nanoleaf | Nanoleaf panels and Lines with the local API, on the same network as the PC |
| Drivers page | ASUS motherboards (ASUS's driver list); Windows Update drivers on any PC |
| Audio | Any PC. The virtual microphone needs [VB-Audio Virtual Cable](https://vb-audio.com/Cable), which you install yourself |
| Discord status, game library | Any PC with the Discord desktop app |
| CPU page: readings and Windows power-plan tuning | Intel and AMD processors |

**AMD processors: built, but not tested on real hardware.** The AMD side of the CPU page was written without an AMD
PC to try it on. It only reads values and changes Windows power-plan settings, which Windows can put back. It never
writes voltages, Curve Optimizer or PBO values.

### Not supported

- AMD and Intel graphics cards. The Graphics page says so and the Recording page has no encoder to use.
- NZXT devices other than the Kraken Elite 2023 (USB 1E71:300C) and the RGB & Fan Controller (USB 1E71:2011). Other
  NZXT coolers are left alone.
- BIOS settings on any board or BIOS version other than the ASUS ROG STRIX Z790-A GAMING WIFI II with BIOS 2301. On
  every other PC that part of the CPU page is hidden. Even on that board the BIOS refuses changes from Windows, so
  the BIOS part only shows the settings.
- Laptops. Nothing stops it from starting on one, but fan and light control on laptops is not something it knows.
- Windows on ARM, and 32-bit Windows.
- **Linux and macOS: not available yet.** Planned for a first version there: the app window with settings, Discord
  status, and lighting through OpenRGB. Fans, sensors, recording, audio and the in-game panel will stay
  Windows-only for now.

The hardware names RigControl shows come from the PC itself and from a built-in list (graphics cards from the public
PCI ID database, processor generations from a table in the app). "Known" means known to the list in that version of
RigControl. A newer part still works; it is shown with the name Windows or the processor reports.

Reports from other hardware are welcome as GitHub issues.

## What it installs and why it runs as administrator

RigControl needs three open-source helper programs. The release build carries all three inside `RigControl.exe`.
The first time you start it, one screen lists them, says what each is and what licence it has, and has one
**Install and continue** button. Nothing is installed before you have seen that screen and pressed the button. If
you leave the setup instead, nothing is installed and the hardware features do not start.

| Helper | What it is | Why RigControl needs it |
|---|---|---|
| [OpenRGB](https://openrgb.org) 1.1 | A program that sets the colours of RGB lights | All lighting. RigControl keeps its own copy in `C:\Program Files\RigControl\OpenRGB` and runs it hidden. If an OpenRGB service already exists on the PC, it is pointed at this copy (its settings are copied; the old files stay). |
| [PawnIO](https://pawnio.eu) | A signed Windows **kernel driver** | Reading CPU temperatures, controlling motherboard fans, and OpenRGB's access to memory and motherboard lights |
| [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) 1.4.2 | A system-wide audio equaliser | The equaliser on the Audio page. Windows needs one restart after it is installed. |

A helper that is already on the PC is left as it is. Each file is checked against a SHA-256 recorded when RigControl
was built before it is written or run. Details: [third_party/BUNDLED.md](third_party/BUNDLED.md).

RigControl runs **as administrator**. It needs that to read sensors through the PawnIO driver, control motherboard
fans, change the graphics card's power limit and clock offsets, install the helpers and drivers, and stop a maker
app's background service when you press "Take over". Without administrator rights it still starts, but shows
partial readings and switches those controls off.

"Start with Windows" registers a Windows scheduled task for your user that starts RigControl as administrator when
you sign in, without a permission prompt each time. Because of that, keep the `RigControl` folder somewhere only
administrators can change, for example inside `C:\Program Files`. RigControl warns you on the Health page when its
folder is open to ordinary programs. See [SECURITY.md](SECURITY.md).

## Installing

1. Download `RigControl-<version>-win-x64.zip` from [Releases](../../releases) and unzip it straight into a folder
   only administrators can change, for example `C:\Program Files\RigControl`. Unzip it there directly: a folder that
   was unzipped somewhere else and then moved in still belongs to your own account, and RigControl then refuses to
   put its helper programs next to it. If the Releases page is empty, no release has been published yet: build
   RigControl from source as described below.
2. Run `RigControl.exe`. **The exe is not code-signed**, so Windows SmartScreen shows "Windows protected your PC" the
   first time: choose "More info", then "Run anyway". Some antivirus programs also warn about an unsigned program
   that asks for administrator rights. Only run a copy you downloaded from this repository's Releases page.
3. Follow the first-time setup: it shows what was found on your PC, the helper programs it will install, anything
   that gets in its way, and offers "Start with Windows".
4. At the end it offers to restart the PC (**Restart now** or **Later**). A restart lets it start as administrator
   with Windows and is needed once for the equaliser.
5. If you use NZXT CAM, Armoury Crate or GIGABYTE Control Center, the Health page explains what clashes. "Take over"
   buttons are there for you to press; RigControl never closes another program by itself.

There is no installer and no automatic update yet. To update, replace the exe with the new one. To remove RigControl,
see "Removing it" in [PRIVACY.md](PRIVACY.md).

## What each page does

| Page | What it does |
|---|---|
| **Overview** | CPU and graphics card temperature, load, power and clocks, memory use, liquid temperature, and every fan and pump in one table. |
| **Cooling** | NZXT Kraken Elite pump and radiator fan curves (stored on the cooler, so they keep running when RigControl is closed), NZXT controller case fans (fixed speed or a temperature curve), motherboard fan headers and graphics card fans. Cooler screen: liquid temperature, infographics with one to three readings, a picture or GIF, clock, audio visualiser or a web page. "Take over" from NZXT CAM and Armoury Crate. |
| **Graphics** | NVIDIA only. 3D settings for all games or per game, including DLSS overrides, Smooth Motion and RTX HDR where the driver has them. Power limit, core and memory clock offsets (voltage is never changed). Display resolution, refresh rate and digital vibrance. Checking for, downloading and starting NVIDIA's driver installer. |
| **Recording** | NVIDIA only. Screen recording and instant replay (the last 60 seconds by default) encoded on the graphics card, with system sound. Hotkeys (Alt+F9 record, Alt+F10 save replay, Alt+R overlay) and a small on-screen overlay with temperatures. Files go to `Videos\RigControl` unless you pick another folder. |
| **Games** | Your game library (Steam, Epic, GOG, other launcher folders and folders you add) with Play buttons. **Discord status**: the game you play, your Apple Music or Spotify song with album art and the current lyric line, what you watch (with the browser extension), the app in front (off unless you turn it on), and custom statuses. Settings for the in-game panel. |
| **In-game panel** | Ctrl+Alt+G or the controller's logo button opens a panel over the game: music controls, volume per app, games, Steam, Xbox and PlayStation shortcuts, Discord's own web app, Phone Link, full controller navigation with an on-screen keyboard, and an optional pause of PS Remote Play while the panel is open. |
| **Lighting** | Every light OpenRGB finds: one colour for everything or per device and zone, device modes, saved scenes. **Effects**: solid, breathing, colour cycle, rainbow wave and spiral, gradient sweep, starfield, colour rain, and three that follow sound (visualiser, bass ripple, sound colour). **Screen sync**: lights follow the screen or a capture card, with optional brightness from the bass and flashes from the treble. **Layout**: place devices, zones or single LEDs on a picture of your setup so effects and screen colours land in the right place. **Nanoleaf**: pair over the local network and light it panel by panel. "Take over" from GIGABYTE Control Center where it is installed. |
| **CPU** | Processor name, cores and threads, generation, live temperature, clock, load and power. Tuning through Windows power-plan settings (boost mode, minimum and maximum processor state, frequency cap, core parking) with "Put back Windows' defaults". On the one supported ASUS board it also shows the BIOS's CPU settings. AMD: built, not tested on real hardware. |
| **Drivers** | On ASUS boards: ASUS's driver list compared with what is installed, with download and install. On any PC: driver updates from Windows Update. |
| **Audio** | Soundboard with hotkeys, virtual microphone (through VB-Audio Virtual Cable), equaliser and effects (through Equalizer APO), and "also play on" a second output. The sound library is stored encrypted on your PC. |
| **Health** | A list of other programs and Windows settings that get in RigControl's way (other RGB, fan, overlay and recording apps, Windows Fast Startup, Game Bar recording and more), what each breaks and what you can do. It only looks; it never closes, changes or installs anything. A few findings have a button that does the same as a "Take over" button elsewhere in the app, after you confirm. |
| **Settings** | Start with Windows as administrator, keep running in the tray, start hidden, °C or °F, the helper programs and their state, restart as administrator, the recent log. |

### Discord status

The status card goes to the Discord desktop app of the Windows user who runs RigControl. You do not link an account
and RigControl never reads Discord's stored login. Another Windows user's Discord, another session and another
browser profile never feed your card. It is off until you turn it on.

The browser extension in [`extension/`](extension/README.md) (Chrome and Edge) tells RigControl what you are
watching. Use the **Get the browser extension** button on the Games page: that copy only talks to your own
RigControl on this PC (`127.0.0.1`, ports 47610 to 47619).

## Safety

RigControl writes to fan controllers, the cooler and the graphics driver. It is written to stay on the safe side:

- Only known commands are sent to a device.
- Kraken pump at least 20 %; every Kraken curve ends at 100 % at 59 °C liquid temperature. NZXT controller fans at
  least 25 %, motherboard fans at least 30 %. A fan curve whose temperature can no longer be read runs at 100 %.
- On ASUS Z790 boards the AIO pump header is never controlled. On other boards RigControl cannot tell which header
  is a pump: leave a pump's header on Automatic.
- Nothing is written to NZXT devices while NZXT CAM runs. Taking over is a button you press.
- Motherboard and graphics card fans go back to automatic when RigControl closes. A display mode change goes back
  after 15 seconds unless you confirm it.
- Graphics card lights get at most one colour a second, because faster writes can lock up some cards' light chip
  until the PC is fully powered off.
- Driver installers only run when they come from NVIDIA's or ASUS's download servers over https and are signed by a
  trusted maker. ASUS packages must also match the checksum ASUS publishes.
- Security settings (Memory Integrity, Defender, Controlled Folder Access) are never changed. VPNs are never closed.
  The old WinRing0 driver is never used.

Still: **use it at your own risk.** There is no warranty. Cooler screen uploads in particular are known to leave
some Kraken screens blank until a full power-off.

## Building from source

You need Windows 10 or 11 (x64), the .NET 8 SDK or newer, and Node.js with npm for the web interface.

1. **FFmpeg.** Put `ffmpeg.exe` and `ffprobe.exe` in `third_party/ffmpeg` as described in
   [its README](third_party/ffmpeg/README.md) (a pinned build with a SHA-256; the files are too big for git). The
   audio projects do not build without them.
2. **Helper programs (optional for a test build, needed for a release).** Download the three official installers
   named in [third_party/BUNDLED.md](third_party/BUNDLED.md), then run `third_party\prepare-bundled.ps1`. It checks
   each against a pinned SHA-256 and fills `third_party/openrgb`, `pawnio` and `equalizerapo`. Without this step the
   build still works but carries no helpers, so it can only use helpers that are already installed.
3. `npm install --prefix src/ui` (run this and the next steps from the top of the repository).
4. `dotnet build RigControl.slnx` and `dotnet test RigControl.slnx`.
5. `tools\build.ps1` makes the single self-contained `RigControl.exe` in `artifacts\app` (or in the folder you give
   with `-Output`). It only builds; it does not install or start anything.

A build you make yourself is not signed either, and it uses the same data folder and settings as a released copy.
Close a running RigControl before you start your own build.

**Discord status in a source build.** The Discord application ids that the released exe uses are not in this
repository. A build from source has none and says "Discord status isn't set up in this build" on the Games page;
everything else works. To use Discord status in your own build, create your own applications in
[Discord's developer portal](https://discord.com/developers/applications) and give their ids to RigControl in one
of two ways:

- **In the settings file, no rebuild needed.** In `%LOCALAPPDATA%\RigControl\settings.json`, add an `appIds` entry to
  the `presence` section and start RigControl again:

  ```json
  "presence": { "appIds": { "game": "<your id>", "music": "<your id>", "app": "<your id>" } }
  ```

- **Built into your exe.** Before building, make a file `private\discord-ids.json` at the top of the repository with
  the same flat list of `"name": "id"` pairs, or point the build at a file elsewhere with
  `-p:DiscordIdsFile=<path>`. The build stores the ids encrypted inside the exe. git ignores the `private` folder
  (see `.gitignore`), so the file stays out of your commits; do not add it by force.

Names you can give an id for: `game`, `music` and `app` (the three main cards); `netflix`, `hulu`, `disney`,
`primeVideo`, `max`, `crunchyroll`, `paramount`, `peacock` and `appleTv` (one card per streaming service); and
`custom1` to `custom9` (cards that custom statuses borrow). An id is 17 to 20 digits. Each card needs its own id;
an entry that is not valid is left out and the log says why. An id in the settings file wins over one built into
the exe.

Code layout:

- `src/RigControl.App`: the Windows host (WPF window, tray, in-game panel, WebView2)
- `src/RigControl.*`: one project per area (Core, Sensors, Cooling, Nvidia, Lighting, Capture, Presence, Drivers,
  Bios, Cpu, Health, Bundled)
- `src/ui`: the interface (React and TypeScript)
- `src/Audio`: the Audio section
- `extension`: the browser extension
- `tools`: the build script and the script that makes the graphics card list
- `docs/API.md`: every call between the interface and the app. `docs/protocols`: research notes for each device
  protocol.

## Credits

RigControl stands on other people's work. The full list with versions and licences is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

- Lighting: [OpenRGB](https://openrgb.org). Sensors and motherboard fans:
  [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and the
  [PawnIO](https://pawnio.eu) driver. Equaliser: [Equalizer APO](https://sourceforge.net/projects/equalizerapo/).
- NVIDIA settings: [NvAPIWrapper](https://github.com/falahati/NvAPIWrapper). Setting ids for DLSS and RTX HDR come
  from [NVIDIA Profile Inspector](https://github.com/Orbmu2k/nvidiaProfileInspector).
- The NZXT protocol work follows [liquidctl](https://github.com/liquidctl/liquidctl); the Kraken Elite screen
  streaming follows [kraken-elite-screen-manager](https://github.com/KhalloufHassan/kraken-elite-screen-manager).
- Recording and audio decoding use an unmodified [FFmpeg](https://ffmpeg.org) build. Audio uses
  [NAudio](https://github.com/naudio/NAudio).
- Graphics card names come from the [PCI ID database](https://pci-ids.ucw.cz/).
- The Games page, Discord status, in-game panel, screen sync and browser extension are a rebuild of
  [RichPresence](https://github.com/noice912/Discord-status) (MIT).
- Song lyrics come from [LRCLIB](https://lrclib.net), album art from Apple's iTunes Search, show posters from
  [TVmaze](https://www.tvmaze.com) and Cinemeta, Genshin Impact profiles from [Enka.Network](https://enka.network).

RigControl is not made, endorsed or supported by NVIDIA, NZXT, ASUS, GIGABYTE, Razer, Nanoleaf, Discord, Intel, AMD,
Microsoft or any other company named here. Their names are used only to say what the app works with.

## Licence

GPL-3.0. See [LICENSE](LICENSE). The bundled helper programs keep their own licences, listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) with the texts in [LICENSES/](LICENSES/).

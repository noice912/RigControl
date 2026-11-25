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
- [Linux and macOS](#linux-and-macos)
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
| Lighting | Devices that [OpenRGB](https://openrgb.org) 1.1 supports, and Wi-Fi lights of the brands listed under [Which lights work](#which-lights-work) |
| Drivers page | ASUS motherboards (ASUS's driver list); Windows Update drivers on any PC |
| Audio | Any PC. The virtual microphone needs [VB-Audio Virtual Cable](https://vb-audio.com/Cable), which you install yourself |
| Discord status, game library | Any PC with the Discord desktop app |
| CPU page: readings and Windows power-plan tuning | Intel and AMD processors |

**AMD processors: built, but not tested on real hardware.** The AMD side of the CPU page was written without an AMD
PC to try it on. It only reads values and changes Windows power-plan settings, which Windows can put back. It never
writes voltages, Curve Optimizer or PBO values.

### Which lights work

Only three kinds of light have been tried on real hardware: NZXT and Razer devices through OpenRGB, and Nanoleaf.
Everything else in these two tables was built from published protocol descriptions and has **not been tried on a
real device yet**. The Lighting page says the same next to each brand. If you own one, a report is very welcome.

**Lights on your network (Wi-Fi).** RigControl talks to each light directly over your home network. It never signs
in to a cloud account for lights. You pair a light on the Lighting page under "Lights on your network", which shows
the steps for its brand. These lights do not need OpenRGB: they play the effects even while OpenRGB is not running.
Each paired light has its own switch, brightness slider, colour and a "Give back" button on that page. The first time
you change a light there, RigControl remembers what it was doing; "Give back" puts that back (its own effect or scene,
as far as the brand allows). "Everything at once" and "Turn all lights off" reach these lights too, except a Hue
Bridge, which you change from its own controls. An effect drives a light only while it plays; changing a light by
hand stops the effect. Scenes hold the PC's own lights only.

| Brand | Tried on real hardware | How to pair | What it does, and its limits |
|---|---|---|---|
| Nanoleaf (panels, Shapes, Lines) | Yes (Nanoleaf Lines) | Search, hold the Nanoleaf's power button 5 to 7 seconds until it flashes, then press Pair within 30 seconds. | Each panel takes its own part of the picture, about 10 times a second. Not placed on the layout, the whole Nanoleaf shows the one overall colour while an effect plays. On/off, brightness, colour, read back and give back (its effect comes back). |
| WLED | **No.** Built from WLED's published protocol. | Search or type its address, press Pair. Nothing to press on the light. "Receive UDP realtime" must be ticked in WLED (it is by default). | Every LED of a strip or matrix takes its own part of the picture. A strip over 489 LEDs is sent in several parts. On/off, brightness, one colour, read back and give back. |
| Philips Hue (through a Hue Bridge) | **No.** Built from open-source descriptions of the bridge's local API. | Search (or type the bridge's address), press the round button on the bridge, then press Pair within 30 seconds. Then place the bridge on the layout: until you do, no effect touches a Hue light. | The colour lights in the bridge's entertainment area follow (make one in the Hue app to choose which lights). **A bridge with no entertainment area follows with every colour light it has, in every room.** Lights that follow are switched on, and go back to how they were, on or off, when the effect stops. Each light shows one colour, a few times a second. Hue's fast Entertainment stream is not built yet, so gradient strips are not lit piece by piece. Its own on/off, brightness and colour change the same lights; left out of "Everything at once" and "Turn all lights off". |
| Govee | **No.** Built from Govee's published LAN protocol and open-source projects. | In the Govee Home app switch on "LAN Control" for each light first. Then search (or type its address) and press Pair. | Only lights that show a "LAN Control" switch. One colour for the whole light, or a colour per piece on lights that accept Govee's unofficial Razer stream (you choose the number of pieces). A running scene or music mode is not put back afterwards. On/off, brightness and colour by hand, but no read back and no "Give back": a Govee light can't be asked what it shows without RigControl waiting on its answer port. |
| Yeelight | **No.** Built from Yeelight's published specification. | In the Yeelight app switch on "LAN Control" for each light first. Then search (or type its address) and press Pair. | One colour for the whole light (plus the background light on models that have one). Lights without that switch, and ones set up through Mi Home, cannot be driven locally. On/off, brightness, colour, read back and give back. |
| Cololight (Hexagon, Strip, MIX) | **No.** The maker publishes nothing; built from the open-source pycololight project. | Type the light's address (your router shows it) and choose which Cololight it is. It cannot be searched for. | One colour for the whole light. On/off, brightness, colour, read back and give back. |
| LeetDesk Aura | **No.** The maker publishes nothing; built from a third-party add-on last changed in 2024, so current desks may behave differently. | Search or type the desk's address, press Pair. | The desk's light strip, as one strip. Use "Run backwards" on the layout if it runs the wrong way. On/off and brightness by hand (no one colour: nothing published says how), read back and give back. |

Not supported: lights that only work through an account (Tuya / Smart Life, Wiz cloud scenes, TP-Link cloud), Hue's
Entertainment stream, Govee and Yeelight lights without a LAN Control switch, Yeelight strips LED by LED, and a
colour per Cololight panel. The Lighting page lists these with the reason.

If a search finds nothing: the light and the PC must be on the same network; Windows Firewall blocks most answers
when your home network is set to Public in Windows (the Health page names the network if it is, and says how to
change it if it is your own home network); a VPN can hide the home network; and Govee and Yeelight lights need LAN
Control switched on in their own app. Only networks with a router are searched, which leaves out most VPNs and a virtual
machine's network. The first search may make Windows ask whether to allow RigControl on private networks.

**Lights through OpenRGB (USB and motherboard).**

| Device | Tried on real hardware |
|---|---|
| NZXT RGB & Fan Controller, Kraken Elite ring | Yes |
| Razer keyboard and mouse | Yes |
| Razer Chroma light strip controller | No |
| Motherboard RGB headers, addressable and plain | No |
| Corsair Commander and Lighting Node | No |
| Thermaltake Riing controllers | No |
| Lian Li Uni fan hubs | No |
| Strips and fans chained on one header, with the number of lights set per header | No |

For a header that cannot count its own lights, the Lighting page has "Lights on this header" under the device's
**More** button: you type how many lights are plugged in and press Set. OpenRGB remembers the number. Lian Li hubs
count 16 lights for each fan, so they take multiples of 16. If the maker's own program (iCUE, TT RGB Plus,
L-Connect, Synapse, NZXT CAM) runs at the same time, it may fight over the same device. RigControl tells you and
never closes it.

### Not supported

- AMD and Intel graphics cards. The Graphics page says so and the Recording page has no encoder to use.
- NZXT devices other than the Kraken Elite 2023 (USB 1E71:300C) and the RGB & Fan Controller (USB 1E71:2011). Other
  NZXT coolers are left alone.
- BIOS settings on any board or BIOS version other than the ASUS ROG STRIX Z790-A GAMING WIFI II with BIOS 2301. On
  every other PC that part of the CPU page is hidden. Even on that board the BIOS refuses changes from Windows, so
  the BIOS part only shows the settings.
- Laptops. Nothing stops it from starting on one, but fan and light control on laptops is not something it knows.
- Windows on ARM, and 32-bit Windows.
- **Linux and macOS: a first version exists in the source, with fewer features, and nobody has tried it on a real
  Linux PC or Mac yet.** See [Linux and macOS](#linux-and-macos). Fans, sensors, graphics, recording, audio, drivers,
  the CPU page and the in-game panel are Windows-only.

The hardware names RigControl shows come from the PC itself and from a built-in list (graphics cards from the public
PCI ID database, processor generations from a table in the app). "Known" means known to the list in that version of
RigControl. A newer part still works; it is shown with the name Windows or the processor reports.

Reports from other hardware are welcome as GitHub issues.

## What it installs and why it runs as administrator

RigControl needs three open-source helper programs. The release build carries all three inside `RigControl.exe`.
The first time you start it, one screen lists them, says what each is and what licence it has, and has one
**Install and continue** button. Nothing is installed before you have seen that screen and pressed the button. If
you leave the setup instead, nothing is installed: lights, motherboard sensors and fans, and the equaliser do not
work until you press **Install and continue**, and the setup screen comes back each time. Closing the window leaves
RigControl in the tray; quit it from the tray icon.

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
   put its helper programs next to it. To check the download, see "Checking your download" in the release notes.
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
| **Lighting** | Every light OpenRGB finds: one colour for everything or per device and zone, device modes, saved scenes. **Effects**: solid, breathing, colour cycle, rainbow wave and spiral, gradient sweep, starfield, colour rain, and three that follow sound (visualiser, bass ripple, sound colour). **Screen sync**: lights follow the screen or a capture card, with optional brightness from the bass and flashes from the treble. **Layout**: place devices, zones or single LEDs on a picture of your setup so effects and screen colours land in the right place. **Lights on your network**: search for and pair Wi-Fi lights (Nanoleaf, WLED, LeetDesk Aura, Philips Hue, Govee, Yeelight, Cololight) and place them on the layout; see [Which lights work](#which-lights-work). **Header sizes**: set how many lights hang on a header. "Take over" from GIGABYTE Control Center where it is installed. |
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
- Lights on your network are only searched for when you press Search for lights, are only controlled on your
  home network, and a pairing key is only ever sent to the light it came from. The number of lights on a header
  only changes when you type it and press Set.
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
- `src/RigControl.Desktop`: the Linux and macOS host (one window through Photino.NET); `src/RigControl.Hosting`:
  what both hosts share
- `src/RigControl.*`: one project per area (Core, Sensors, Cooling, Nvidia, Lighting, Capture, Presence, Drivers,
  Bios, Cpu, Health, Bundled)
- `src/ui`: the interface (React and TypeScript)
- `src/Audio`: the Audio section
- `extension`: the browser extension
- `tools`: the build script and the script that makes the graphics card list
- `docs/API.md`: every call between the interface and the app. `docs/protocols`: research notes for each device
  protocol.

## Linux and macOS

**How far this is checked: it is built by CI, and its tests are run there, on GitHub's Linux and macOS machines;
it has not been tried on real hardware.** The Actions tab of this repository shows whether the
latest run passed, and [docs/CI.md](docs/CI.md) says what each check proves and what it does not: the window is
opened once on Linux with no screen attached, never on macOS, and no OpenRGB, Discord or light is ever involved.
It was written on a Windows PC with no Linux PC or Mac at hand. The window itself has never been opened by its
author. Treat it as an early test version and please report what you see.

What it has: the same window and interface, settings, Discord status, and lighting effects through OpenRGB and the
lights on your network. Every other page is left out, and Settings lists those features as "Not available on this
system".

What is different from Windows:

- It runs as your normal user. It refuses to start as root and never asks for your password.
- There is no tray icon. Closing the window closes RigControl and puts your lights back as they were.
- **OpenRGB is not started for you.** Install OpenRGB yourself (https://openrgb.org), open it and turn on its SDK
  server (port 6742). RigControl connects by itself within a few seconds. Screen colours, capture cards and
  sound-reactive lighting are Windows-only; the other effects, the layout, scenes and network lights are there.
- **Discord status** attaches to your own Discord through its local socket in your private runtime folder. It can
  show your custom statuses and the name of a well-known program you have open (from a short fixed list; never a
  window title). Games, music, "watching" and the browser extension are Windows-only. A Discord socket in a shared
  folder such as `/tmp` is never used; if your Discord only offers that one, RigControl says Discord isn't running
  and the log says why.
- Pairing keys for your lights are encrypted with a key kept in a file only you can read, not in the system keyring.
  [PRIVACY.md](PRIVACY.md) says what that means.

What you need:

- Linux (x64): the system packages **libwebkit2gtk-4.1** and **libnotify** (Debian and Ubuntu:
  `sudo apt install libwebkit2gtk-4.1-0 libnotify4`; Fedora: `sudo dnf install webkit2gtk4.1 libnotify`) and a
  desktop session. The window needs both; the web view package does not bring in the second one. macOS (Apple
  silicon or Intel): nothing extra, it uses the system's own web view.
- To build: the .NET 8 SDK or newer and Node.js with npm. FFmpeg and the helper programs are not needed.

Building and starting it (from the top of the repository, on the system you want to run it on):

```
npm install --prefix src/ui
dotnet publish src/RigControl.Desktop -c Release -r linux-x64      # or osx-arm64, osx-x64
```

The result is a folder (about 75 MB, with its own .NET runtime) under `artifacts/publish/RigControl.Desktop/`. Start
the file `RigControl` in it. It is a folder and not one file because the window library is a native file that has to
sit next to the program. Publish on the system you will run it on: a copy published on Windows has no "executable"
mark on its files, and on a Mac it is not signed.

`./RigControl --smoke` opens the window, waits until the page has connected to the app, and closes again (exit code
0 when it worked, 1 with the reason in its output when not). It uses a temporary folder and touches no lights and no
Discord. `--help` lists the other options.

How the page reaches the window: from memory, by an address of its own (`app://rigcontrol/`). RigControl opens no
network port for it, not even one only your own computer can reach. If the system's web view will not load the page
that way, RigControl tries a second way (the same address, the script handed over differently). If neither works,
RigControl closes and says so, instead of leaving an empty window open. The log says which way was used. Which of
the two a real Linux PC or Mac needs is not known yet.

The layout of the shared source is in [docs/PORTING.md](docs/PORTING.md).

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
- Lights on your network were written from the makers' published protocols and from open-source projects that
  describe them: [WLED](https://kno.wled.ge), the Yeelight Inter-Operation Specification, python-yeelight,
  govee-local-api, govee2mqtt, pycololight, Hyperion.ng, aiohue, Q42.HueApi and OpenHue. No code was copied.
- Graphics card names come from the [PCI ID database](https://pci-ids.ucw.cz/).
- The Games page, Discord status, in-game panel, screen sync and browser extension are a rebuild of
  [RichPresence](https://github.com/noice912/Discord-status) (MIT).
- Song lyrics come from [LRCLIB](https://lrclib.net), album art from Apple's iTunes Search, show posters from
  [TVmaze](https://www.tvmaze.com) and Cinemeta, Genshin Impact profiles from [Enka.Network](https://enka.network).

RigControl is not made, endorsed or supported by NVIDIA, NZXT, ASUS, GIGABYTE, Razer, Nanoleaf, Signify (Philips
Hue), Govee, Yeelight, the WLED project, LifeSmart (Cololight), LeetDesk, Corsair, Thermaltake, Lian Li, Discord,
Intel, AMD, Microsoft or any other company named here. Their names are used only to say what the app works with.

## Licence

GPL-3.0. See [LICENSE](LICENSE). The bundled helper programs keep their own licences, listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) with the texts in [LICENSES/](LICENSES/).

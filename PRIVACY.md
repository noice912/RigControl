# Privacy

This page says what RigControl reads, what it stores, and every place it contacts. It describes the app as its code
stands. If you find something the app does that is not written here, that is a bug in this page: please open an
issue.

## The short version

- **Nothing is sent to the author.** There is no account, no telemetry, no crash upload, no analytics and no update
  check. The author has no server.
- Everything RigControl stores stays on your PC.
- It contacts the internet only for the look-ups listed below, each tied to a feature you can leave off or a button
  you press.
- If you turn on Discord status, what you play, listen to and watch is shown on your Discord profile. That is what
  the feature is for.

## What it contacts on the internet

| Who | When | What is sent |
|---|---|---|
| **Discord** (`discord.com`) | Once a week when the game library is scanned (the scan runs when RigControl starts, even while Discord status is off) | A plain request for Discord's public list of games it can detect. Nothing about you or your games is sent; the matching is done on your PC. |
| **Discord** (`discord.com`) | When a "Watching" card is first shown for a streaming service | A request for that service's public icon |
| **Apple iTunes Search** (`itunes.apple.com`) | While Discord status is on and a song plays, if "album art" is on | The song's artist, album and title, to find the cover picture |
| **LRCLIB** (`lrclib.net`) | While Discord status is on and a song plays, if "lyrics" is on | The song's artist, title, album and length, to find timed lyrics |
| **TVmaze** (`api.tvmaze.com`) and **Cinemeta** (`v3-cinemeta.strem.io`) | While a "Watching" card is shown, if "poster" is on | The name of the show or film, to find a poster |
| **Enka.Network** (`enka.network`) | Only if you typed a Genshin Impact UID on the Games page and the game runs | That UID, to read the public profile (name and level). No login is used. |
| **NVIDIA** (`www.nvidia.com`, `gfwsl.geforce.com`) | When you press "Check for updates" on the Graphics page | NVIDIA's public product list is fetched, then the ids of your graphics card model and Windows version are sent to find the newest driver |
| **NVIDIA** (`*.download.nvidia.com`) | When you press the button to install that driver | A download of NVIDIA's installer |
| **ASUS** (`www.asus.com`) | When you press "Check" on the Drivers page, on an ASUS motherboard only | The motherboard's model name, to get ASUS's driver list |
| **ASUS** (`dlcdnets.asus.com`, `dlcdn.asus.com`) | When you press install on an ASUS driver | A download of that driver package |
| **Microsoft Windows Update** | When you press "Check" or install on the Drivers page | Windows' own update service does the search and download, the same as Windows Settings does |
| **A web page you chose** | Only while the cooler screen is in "web page" mode | A hidden browser opens the address you typed and shows it on the cooler's screen. That site sees a normal browser visit. |
| **Discord's web app** (`discord.com`, `discordapp.com`, `discord.gg`) | Only when you open Discord inside the in-game panel | See "The Discord window in the in-game panel" below |

RigControl does **not** contact GitHub. There is no update check; you look at the Releases page yourself. Links in
the app (to a download page, a store, a help page) open in your normal browser when you press them.

The requests RigControl makes itself carry no account, cookie or id of yours. Like any internet request, they show
your IP address to the site that answers.

### What Discord and your friends see

With Discord status on, RigControl hands a card to the Discord desktop app on your PC, and Discord shows it on your
profile. Depending on what you switched on, a card can hold: the game's name, a song's title, artist, album and
current lyric line, the show or video you are watching and how far in you are, a custom status you wrote, or the
name of the app in front with its window title. The last one is off unless you turn it on, because window titles
can be private.

Pictures on a card (album cover, poster, a site's or app's icon) are given to Discord as web addresses. Discord and
the people who look at your profile then load those pictures from where they are hosted: Apple, TVmaze, Cinemeta,
Wikimedia, Google's site-icon service, Discord's own image server, and for the browser extension YouTube's and
Twitch's thumbnail servers.

RigControl attaches only to the Discord app of the Windows user who runs RigControl. It never reads Discord's stored
login or token, and it never uses another Windows user's Discord.

## What it contacts on your own network and PC

| What | When | Details |
|---|---|---|
| **Nanoleaf** on your home network | When you press "Find" on the Lighting page | RigControl tries port 16021 on every address of the PC's own local network (small home networks only) to find a Nanoleaf. Other devices on the network see a short connection attempt. |
| **Nanoleaf** you paired | While it is paired | Colours go to that Nanoleaf's address over your network (its local API on port 16021, colour stream on UDP port 60222). The Nanoleaf's access token is only ever sent to the address you paired. Nothing goes through Nanoleaf's cloud. |
| **OBS** on this PC (`127.0.0.1`, OBS's WebSocket port, 4455 unless you changed it) | Only while lights follow a capture card **and** OBS, Streamlabs or XSplit is running | Many capture cards open in one program at a time, so RigControl asks OBS for small pictures of the capture source instead. **To do that it reads OBS's saved WebSocket settings, including the WebSocket password, from OBS's own settings file** (`%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json`). The password is used only to sign in to OBS on this PC. It is never logged, shown, stored by RigControl or sent anywhere else. Only read-only requests are made. |
| **OpenRGB** on this PC (`127.0.0.1:6742`) | Whenever lighting is in use | RigControl runs OpenRGB hidden as a local server and sends it colours. |
| **The browser extension link** (`127.0.0.1`, the first free port from 47610 to 47619) | While RigControl runs | RigControl listens here for the browser extension. Only this PC can reach it, and only programs of your own Windows user in your own session are answered. Websites cannot post to it. |
| **Discord desktop app** on this PC | While Discord status is on, and a short "who is signed in" hello once a minute while it is off | Discord's local link for status cards. Discord answers with the account's name and id, which RigControl shows on the Games page so you can see which account it attached to. |

### The browser extension

The extension (Chrome and Edge, in `extension/`) reads the title, episode and position from the player on the
streaming sites it lists, and sends them only to RigControl on this PC. It sends nothing to any other place. The
copy you get from **Get the browser extension** carries a secret made for your Windows user, so it only reports to
your own RigControl.

### The Discord window in the in-game panel

The in-game panel can show Discord's own web app. This is a real browser view (Microsoft WebView2) that runs
**inside RigControl**, which runs as administrator. You sign in to Discord there yourself, the normal way.
RigControl does not see or store your password.

- The view keeps its own browser profile (cookies, cache, the Discord sign-in) in
  `%LOCALAPPDATA%\RigControl\discord-web`.
- Only `discord.com`, `discordapp.com` and `discord.gg` load in it. Any other link opens in your normal browser.
- The page cannot reach this PC's own local addresses.
- "Sign out and forget" on the Games page deletes that profile.
- What you do inside that view is between you and Discord, under Discord's own privacy policy.

## What it reads on your PC

- Hardware sensors, fan speeds and device names, to show and control them.
- The list of running programs and Windows services, to notice games, maker apps and other things that clash
  (Health page). Only programs of your own Windows session count for your Discord card.
- Installed games: the library folders of Steam, Epic, GOG and other launchers, and folders you add.
- What Windows' media controls report as playing (song, artist, player), for the music card and the in-game panel.
- The title of the window in front, only if you turned "current app" on.
- A fixed list of Windows settings (for example Fast Startup, Game Bar recording, Memory Integrity), read-only, for
  the Health page.
- The screen or a capture card picture, shrunk to a tiny grid of colours, only while lights follow the screen. The
  picture is not kept.
- The sound your PC plays, as loudness of bass, mids and treble, only while a sound-driven light effect or the
  cooler screen's visualiser is on. Nothing is recorded by this.
- OBS's WebSocket settings, as described above.
- If the separate RichPresence app was used on this PC: its settings, copied once, and its Discord web sign-in for
  the in-game panel, copied once. Its own folder is never changed.

## What it stores, and where

Everything is on your PC. Nothing is synced.

| Where | What |
|---|---|
| `%LOCALAPPDATA%\RigControl\settings.json` | All settings: fan curves, lighting scenes and layout, hotkeys, Discord status choices and custom statuses, the paired Nanoleaf's address, name and serial number, and its access token **encrypted for your Windows user** (Windows DPAPI) |
| `%LOCALAPPDATA%\RigControl\logs` | Log files in plain text: what the app did and what went wrong. They can contain device names, program names and file paths. RigControl does not write passwords or tokens to them. Nothing uploads them; you choose whether to attach one to an issue. |
| `%LOCALAPPDATA%\RigControl\presence` | The game library that was found, Discord's public game list, and the secret for the browser extension link |
| `%LOCALAPPDATA%\RigControl\browser-extension-xxxxxxxx` | The copy of the browser extension made by "Get the browser extension" |
| `%LOCALAPPDATA%\RigControl\WebView2`, `WebView2-screen`, `discord-web` | Browser profiles: the app's own interface, the cooler screen's web page, and the Discord window |
| `%LOCALAPPDATA%\RigControl\drivers` | Driver packages you downloaded |
| `%LOCALAPPDATA%\RigControl\replay-buffer` | The last seconds of screen recording while instant replay is on. Deleted when it is switched off and when RigControl closes. |
| `%LOCALAPPDATA%\RigControl\ffmpeg` | The FFmpeg tools, unpacked from the exe |
| `%LOCALAPPDATA%\RigControl\bios-backups`, `nvidia-settings-before-rigcontrol.bin` | Copies of settings as they were before RigControl changed anything |
| `%LOCALAPPDATA%\AudioController` | The Audio page's sound library, **encrypted** with a key protected for your Windows user (Windows DPAPI) |
| `Videos\RigControl` (or the folder you chose) | Your recordings and saved replays |
| `C:\Program Files\RigControl\OpenRGB` | RigControl's copy of OpenRGB and OpenRGB's own settings |
| Registry `HKLM\SOFTWARE\RigControl` | That you agreed to the helper programs, and (on the one supported board) the original BIOS settings. Only administrators can write here. |
| Registry `HKCU\Software\RigControl` | Notes of what "Take over" changed (for example another app's start-up entry), so it can be put back |
| A Windows scheduled task named `RigControl-<your user id>` | Only if "Start with Windows" is on |

The first-time setup also installs PawnIO and Equalizer APO as normal Windows programs, and may point an existing
OpenRGB service at RigControl's copy. See the README.

## Removing it

1. In Settings, switch off "Start with Windows" (this removes the scheduled task). If you used a "Take over"
   button, give control back first on the page where you pressed it.
2. Quit RigControl from the tray and delete its exe.
3. Delete `%LOCALAPPDATA%\RigControl` and, if you do not want the sound library, `%LOCALAPPDATA%\AudioController`.
4. Uninstall PawnIO and Equalizer APO from Windows' "Installed apps" if nothing else on your PC uses them
   (other hardware monitors and RGB programs often use PawnIO).
5. Delete `C:\Program Files\RigControl`. If an OpenRGB service existed before RigControl and was pointed at this
   copy, reinstall OpenRGB first so the service runs its own files again.
6. Optional: delete the registry keys `HKLM\SOFTWARE\RigControl` and `HKCU\Software\RigControl`.

There is no uninstaller yet.

## Children and sensitive data

RigControl is a hardware tool. It asks for no personal details and has no accounts. The only personal information
it handles is what the sections above list, and it stays on your PC unless you turn on Discord status.

## Changes

This page changes when the app does. The history of this file in the repository shows every change.

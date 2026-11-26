# Privacy

This page says what RigControl reads, what it stores, and every place it contacts. It describes the app as its code
stands. If you find something the app does that is not written here, that is a bug in this page: please open an
issue.

## The short version

- **Nothing is sent to the author.** There is no RigControl account, no telemetry, no crash upload and no analytics.
  The author has no server. The update check asks GitHub, not the author, and sends nothing about you.
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
| **GitHub** (`api.github.com`, and GitHub's release download hosts) | The update check: 1 minute after RigControl starts and every 6 hours, while "Check for updates automatically" is on (Settings > Updates; on by default), and when you press "Check now" | A plain request for RigControl's latest release page (with "Get development builds" on, which only people who may publish RigControl can turn on: the list of RigControl's newest releases instead). When you press "Update", the new version's setup file is downloaded. No account or id of yours is sent. |
| **GitHub** (`github.com`, `api.github.com`) | Only if you sign in with GitHub (Settings > GitHub account), and while you stay signed in | Signing in uses GitHub's device sign-in: you confirm a short code on github.com in your normal browser. RigControl then holds a GitHub sign-in token for your account, **encrypted for your Windows user** (Windows DPAPI) in `settings.json`. It is used only to talk to GitHub: to post a review you write, and to check whether your account may change the maintainers' private development repository (GitHub answers "not found" to anyone else). For the repository's maintainers it is also used to publish releases and to list reviews. Signing out deletes it. |
| **GitHub issues** (`api.github.com`) | Only when you press "Send" on a review | Your star rating, the text you wrote and RigControl's version become a **public** issue on github.com/noice912/RigControl under your GitHub name. Nothing about your PC is added. |

Apart from the GitHub rows above, RigControl contacts no other site. Every update is checked before it is used: its
details carry a signature that only the author's update key can make, and the file must match them exactly. Links in
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
| **A search for lights** on your home network | Only when you press "Search for lights" on the Lighting page. Never by itself, never on a timer. | Only networks that have a router are searched, which leaves out most VPN adapters and a virtual machine's network. Any network with private addresses and a router counts, so do not press Search on a network that is not yours. Every brand is asked at once, each in its own way: a name question to the whole home network (mDNS) for WLED, LeetDesk and the Hue Bridge; one question to a group address for Yeelight (239.255.255.250, UDP port 1982) and one for Govee (239.255.255.250, UDP port 4001); and for Nanoleaf a try of port 16021 on every address of the PC's own local network (small home networks only). Other devices on the network see these short questions and connection attempts. A device that answers the name question is then asked what it is, at the address its answer came from and nowhere else. Cololight is not searched for: you type its address. The search stops by itself after about half a minute, or when you press Stop. |
| **A light you are pairing** | When you press "Pair" | RigControl asks the light at that address what it is and, for Nanoleaf and Philips Hue, asks it for a key after you pressed the button on the light. Only addresses on a home network are accepted. |
| **A light you paired** | While it is paired | Once in a while RigControl reads what the light is made of (how many panels or LEDs). While the Lighting page is open, it asks each light that can say what it shows (on or off, brightness, colour) at most every 15 seconds. While an effect or the screen sync runs, colours go to that light's address and nowhere else. When the effect stops, the light is put back as it was. When you switch, dim or colour a light by hand, press "Everything at once" or "Turn all lights off", or press "Give back", the command goes to that light's address only; the first change by hand reads what the light was doing so it can be given back later (that is kept in the settings file, see below). A key the light gave at pairing (Nanoleaf's access token, Hue's application key) is only ever sent to the address it was paired with. |
| **Yeelight fast mode** | While a Yeelight follows an effect | RigControl asks the light to connect back to this PC, so colours can be sent quickly. For that RigControl listens on one port, on your home network's address only, until the light has connected. Windows Firewall may ask once whether to allow RigControl on private networks. |
| **Govee answers** | During a search, while pairing, and for a moment when a paired Govee light is read or starts following | Govee lights send their answers to UDP port 4002 on this PC, so RigControl listens there (on every local network address of the PC, so a light you paired by typing its address can answer too) for as long as it waits for an answer, then stops. Windows Firewall may ask once whether to allow RigControl on private networks. |

All of this stays inside your home network. RigControl refuses to send light traffic to any address that is not a
home-network address, and **no cloud account is used for lights**: not Nanoleaf's, Philips Hue's, Govee's,
Yeelight's or anyone else's. Every brand other than Nanoleaf was built from the published protocol and has not been
tried on a real device yet (see the README).

The rest of what RigControl contacts on this PC:

| What | When | Details |
|---|---|---|
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
- The capture card's sound (Recording page, "Capture card sound"), only while that is switched on, and only to play
  it straight away on the output you chose. It is held in memory for a few milliseconds on the way; nothing is
  recorded, saved or sent anywhere.
- OBS's WebSocket settings, as described above.
- If the separate RichPresence app was used on this PC: its settings, copied once, and its Discord web sign-in for
  the in-game panel, copied once. Its own folder is never changed.

## Developer mode and the Windows Hello check

This part only concerns the people who maintain RigControl. For everyone else the Developer section stays hidden and
none of this happens: it appears only when the GitHub account signed in to RigControl may change the maintainers' private
development repository on GitHub.

**What the Windows Hello check is for.** Developer mode can publish an update that every RigControl user receives,
so it needs more than a GitHub sign-in (sign-in tokens can be stolen and stay valid for hours). Every time the
Developer page is opened, and again right before "Publish", RigControl asks Windows Hello (your Windows PIN,
fingerprint or face) to sign a random challenge with a key kept in this PC's TPM security chip. That proves two
things: the request comes from the one PC that was registered, because the key cannot be copied out of the chip, and
the person at the keyboard knows the PIN or has the finger, not a program running in the background or someone using
an unlocked PC. The unlocked state lasts for that session only and locks again after 15 minutes without use or when
RigControl closes.

**What RigControl sees and keeps.**

- Your PIN, fingerprint and face never reach RigControl. Windows checks them itself and only tells RigControl whether
  the key was allowed to sign.
- "Register this PC" makes one Windows Hello key named `RigControl.Developer` for your Windows account. Windows keeps
  it, in the TPM. RigControl only ever reads its public half, which is what the registration text (`RCREQ2...`)
  carries.
- The developer code you paste in (`RCDEV2...`) is kept in `settings.json`. It holds the maintainer's GitHub name,
  the date, an id and the public half of this PC's key. It is not secret: it works only together with that key on
  this PC.
- Nothing about this check is sent anywhere. The registration text goes only where you paste it yourself, to the
  maintainer who issues the code.

**Publishing.** "Publish an update" reads the update signing key file you point it at (by default
`%USERPROFILE%\.rigcontrol-keys\update-signing.pem`), signs the new version's details on this PC, and uploads the
setup file and those details to RigControl's GitHub releases with your GitHub sign-in. The signing key itself is
never uploaded, logged or shown.

**Removing it.** Settings > Developer > "Remove from this PC" deletes both the code and the Windows Hello key.
Uninstalling RigControl does not delete the Windows Hello key, so press that button first if you want it gone.

## What it stores, and where

Everything is on your PC. Nothing is synced.

| Where | What |
|---|---|
| `%LOCALAPPDATA%\RigControl\settings.json` | All settings: fan curves, lighting scenes and layout, hotkeys, Discord status choices and custom statuses, and for each light you paired on your network: its brand, name, model, id (serial number or hardware address) and network address, plus the key it gave at pairing (Nanoleaf's access token, Hue's application key and client key) **in protected form, encrypted for your Windows user** (Windows DPAPI), and, once you changed a light by hand, what it was doing before (its effect or scene name, colour, brightness, on or off) so "Give back" can put it back. Forgetting a light removes its entry and its key. |
| `%LOCALAPPDATA%\RigControl\logs` | Log files in plain text: what the app did and what went wrong. They can contain device names, program names and file paths. RigControl does not write passwords or tokens to them. Nothing uploads them; you choose whether to attach one to an issue. |
| `%LOCALAPPDATA%\RigControl\presence` | The game library that was found, Discord's public game list, and the secret for the browser extension link |
| `%LOCALAPPDATA%\RigControl\browser-extension-xxxxxxxx` | The copy of the browser extension made by "Get the browser extension" |
| `%LOCALAPPDATA%\RigControl\WebView2`, `WebView2-screen`, `discord-web` | Browser profiles: the app's own interface, the cooler screen's web page, and the Discord window |
| `%LOCALAPPDATA%\RigControl\drivers` | Driver packages you downloaded |
| `%LOCALAPPDATA%\RigControl\replay-buffer` | The last seconds of screen recording while instant replay is on. Deleted when it is switched off and when RigControl closes. |
| `%LOCALAPPDATA%\RigControl\ffmpeg` | The FFmpeg tools, unpacked from the exe (an installed RigControl unpacks them into `<install folder>\ffmpeg` instead) |
| `%LOCALAPPDATA%\RigControl\bios-backups`, `nvidia-settings-before-rigcontrol.bin` | Copies of settings as they were before RigControl changed anything |
| `%LOCALAPPDATA%\AudioController` | The Audio page's sound library, **encrypted** with a key protected for your Windows user (Windows DPAPI) |
| `Videos\RigControl` (or the folder you chose) | Your recordings and saved replays |
| `C:\Program Files\RigControl\OpenRGB` | RigControl's copy of OpenRGB and OpenRGB's own settings |
| Registry `HKLM\SOFTWARE\RigControl` | That you agreed to the helper programs, and (on the one supported board) the original BIOS settings. Only administrators can write here. |
| Registry `HKCU\Software\RigControl` | Notes of what "Take over" changed (for example another app's start-up entry), so it can be put back |
| A Windows scheduled task named `RigControl-<your user id>` | Only if "Start with Windows" is on |
| A Windows Hello key named `RigControl.Developer`, in the TPM | Only on a maintainer's PC after "Register this PC" (see "Developer mode and the Windows Hello check") |

The first-time setup (or the installer) also installs PawnIO and Equalizer APO as normal Windows programs, and may
point an existing OpenRGB service at RigControl's copy. See the README and [TERMS-OF-USE.md](TERMS-OF-USE.md).

### On Linux and macOS

This is for the Linux and macOS version. It has fewer features (see the README) and has not been tried on real
hardware yet, so this part describes what the code does, not what was watched happening.

| Where | What |
|---|---|
| Linux: `~/.config/RigControl` (or `$XDG_CONFIG_HOME/RigControl`). macOS: `~/Library/Application Support/RigControl` | `settings.json` with the same kind of settings as on Windows, `secret.key`, a `presence` folder (what the Discord status part keeps, such as Discord's public game list), and `running.lock`, an empty file that is held open while RigControl runs so a second copy can tell |
| Linux: `~/.local/state/RigControl/logs` (or `$XDG_STATE_HOME/RigControl/logs`). macOS: `~/Library/Logs/RigControl` | Log files in plain text, as on Windows |
| Linux: `~/.config/autostart/rigcontrol.desktop`. macOS: `~/Library/LaunchAgents/io.github.rigcontrol.plist` | Only if you turn on starting at sign-in |
| Where the system's web view keeps its own data. This is the system's choice, not RigControl's: normally `~/.local/share/RigControl` and `~/.cache/RigControl` on Linux, `~/Library/WebKit` and `~/Library/Caches` on macOS | The window's own small notes, such as which page you had open last. The interface itself is carried inside the program and is not stored there by RigControl. |

The keys your lights gave at pairing are protected differently there. Windows encrypts them for your Windows user.
On Linux and macOS RigControl encrypts them with a random key that it keeps in `secret.key`, a file that only your
user account can read (file mode 0600). The system keyring is not used. What that means: a copy of `settings.json`
alone does not give the keys away, but any program running as you can read both files. The folders are made so
that only you can open them. RigControl never runs as root there and never asks for your password.

**What it reads there.** For Discord status it looks at the names of the programs you are running, to show the
name of a well-known one from a short fixed list. It does not read window titles, and it has no way to know which
window is in front. It does not read your music player, your browser or your games on these systems.

**What it connects to on your own computer there.**

- Discord, through Discord's own local socket in your private runtime folder (`$XDG_RUNTIME_DIR` on Linux, your
  own temporary folder on macOS). The folder must be yours alone; a socket in a shared folder such as `/tmp` is
  never used. No stored Discord login is read.
- OpenRGB, on `127.0.0.1` port 6742, when you have started it and turned on its SDK server. RigControl does not
  start OpenRGB on these systems.
- The lights you paired on your network, as on Windows.

**How the interface is shown.** The interface is carried inside the program and handed to the window from memory.
The page is not allowed to load anything from the internet or to connect anywhere; everything it does goes through
the window's own message channel to RigControl. Links open in your normal browser.

Nothing listens on any network port for this, not even on `127.0.0.1`. If the system's web view will not load the
page the usual way, RigControl tries a second way, also from memory; if that one was needed, it is noted
(`desktop.uiMode` in `settings.json`) so it is tried first next time. If neither works, RigControl closes and says
so instead of leaving an empty window open.

Only RigControl's own page can give RigControl orders. The window passes on messages from whatever page it shows,
so each time RigControl starts it makes a random value, writes it into its own page, and reads only messages that
carry it. If another page ended up in the window (for example a link or file dropped on it), RigControl ignores
everything that page sends. The value is never written to the log.

**Removing it there.** Switch off "Start when I sign in" in Settings, close RigControl, delete the folder you
started it from, and delete the folders in the table above.

## Removing it

An installed RigControl is removed from Windows' "Installed apps" (or with `RigControl.exe --uninstall`). The
uninstaller closes RigControl and removes its program files, shortcuts, its entry in Installed apps and your
"Start with Windows" task; ticking "Also delete my settings, logs and Discord sign-in" also deletes
`%LOCALAPPDATA%\RigControl`. It leaves what steps 3 to 6 below cover apart from that folder: the sound library, the
helper programs, RigControl's OpenRGB copy and the registry keys.

By hand, or for a copy that was never installed:

1. In Settings, switch off "Start with Windows" (this removes the scheduled task). If you used a "Take over"
   button, give control back first on the page where you pressed it.
2. Quit RigControl from the tray and delete its exe.
3. Delete `%LOCALAPPDATA%\RigControl` and, if you do not want the sound library, `%LOCALAPPDATA%\AudioController`.
4. Uninstall PawnIO and Equalizer APO from Windows' "Installed apps" if nothing else on your PC uses them
   (other hardware monitors and RGB programs often use PawnIO).
5. Delete `C:\Program Files\RigControl`. If an OpenRGB service existed before RigControl and was pointed at this
   copy, reinstall OpenRGB first so the service runs its own files again.
6. Optional: delete the registry keys `HKLM\SOFTWARE\RigControl` and `HKCU\Software\RigControl`.

## Children and sensitive data

RigControl is a hardware tool. It asks for no personal details and has no accounts. The only personal information
it handles is what the sections above list, and it stays on your PC unless you turn on Discord status.

## Changes

This page changes when the app does. The history of this file in the repository shows every change.

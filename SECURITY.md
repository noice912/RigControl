# Security

RigControl runs as administrator, installs a kernel driver and talks to hardware. This page says how to report a
problem, what the app does to stay safe, and what you should know before you run it.

## Reporting a security problem

Please report it privately through **GitHub security advisories**: open this repository on GitHub, go to the
**Security** tab and choose **Report a vulnerability** ([direct link](../../security/advisories/new)).

- Do not open a public issue for a security problem.
- There is no e-mail address for reports.
- Say what you found, how to make it happen, and which version or commit you used.
- This is a one-person project with no fixed response time. Reports are read and answered through the advisory.

Only the newest release gets fixes.

For bugs that are not security problems, open a normal issue.

## What you should know before running it

- **The exe is not code-signed.** Windows SmartScreen will warn you, and nothing proves the file came from this
  project except where you downloaded it. Download it only from this repository's Releases page, or build it
  yourself.
- **It runs as administrator.** A program with administrator rights can do anything on the PC. With "Start with
  Windows" on, a scheduled task starts it with those rights at every sign-in, without a prompt.
- **Keep its folder closed to ordinary programs.** If RigControl starts as administrator from a folder that any
  program can change (Downloads, Desktop, a folder on another drive), such a program could swap the exe and get
  administrator rights at your next sign-in. Put the exe somewhere only administrators can change, for example
  inside `C:\Program Files`. RigControl checks this and warns in the first-time setup, on the Health page and in
  the log. There is no installer yet that does this for you.
- **It installs a kernel driver.** PawnIO is a signed driver that lets programs read and write low-level hardware
  ports. RigControl needs it for temperatures and fans. Other programs running as administrator can use the
  installed driver too.
- **A browser view of Discord runs inside the app.** The in-game panel can show Discord's web app in a Microsoft
  WebView2 view. That view lives inside the RigControl process, which runs as administrator. It is limited to
  Discord's own sites (see below), but a web page running inside an elevated program is a larger risk than the
  same page in your normal browser. If you do not want that, do not open Discord in the panel; nothing loads until
  you do.
- **Use at your own risk.** It changes fan speeds, pump speeds, graphics card limits and lights. There is no
  warranty.

## What the app does to stay safe

### Helper programs and drivers

- Nothing is installed before the first-time setup has shown you the list of helper programs and you pressed
  Install. That agreement is stored where only administrators can write (`HKLM\SOFTWARE\RigControl`), not in the
  settings file, so another program cannot fake a "yes".
- Every carried file has a SHA-256 recorded at build time. A file is only written or run if it matches.
- RigControl's OpenRGB copy lives in `C:\Program Files\RigControl\OpenRGB`. Before it is started, RigControl checks
  that the folder cannot be changed by programs without administrator rights and that every file is unchanged.
- NVIDIA drivers are downloaded only from `*.download.nvidia.com` over https and must carry a valid Windows
  signature from NVIDIA Corporation, or the file is deleted and never run.
- ASUS drivers are downloaded only from ASUS's two download servers over https, must match the checksum ASUS
  publishes, and the installer inside must be signed by ASUS, Intel or Realtek.
- The old WinRing0 driver, which Microsoft blocks as vulnerable, is never used.

### Other programs and Windows settings

- The Health page only looks. It never closes, changes or installs anything by itself.
- "Take over" (from NZXT CAM, Armoury Crate, GIGABYTE Control Center) only runs when you press it and confirm.
- Windows security settings are never changed: not Memory Integrity, not the vulnerable driver blocklist, not
  Defender, not Controlled Folder Access. VPN programs are never closed.
- A PC restart is only ever asked for by you, in a dialog, with a 30-second countdown you can cancel. It is refused
  while another Windows user is signed in unless you were told and agreed.

### The interface and the browser views

- The app's own interface is local files shown in WebView2. It cannot navigate to other sites; links open in your
  normal browser, and only `https` links.
- A released build always shows its own built-in pages. The developer option that loads the interface from a local
  dev server exists only in debug builds, and never when RigControl runs as administrator.
- WebView2's own settings from environment variables (`WEBVIEW2_...`: another browser folder, extra switches, a
  remote debugging port) are removed before any browser view is made, because an ordinary program can set such
  variables for your account. The browser profiles are still in your own `%LOCALAPPDATA%\RigControl` folder.
- The Discord view only loads `https://discord.com`, `discordapp.com` and `discord.gg`. Any other link opens in
  your normal browser. The page is blocked from this PC's own local addresses. It has its own browser profile, and
  RigControl does not take commands from that page.
- The cooler screen's "web page" mode uses a separate hidden browser with its own profile. That page cannot open
  windows, download files or ask for permissions, and has no connection to RigControl itself.

### Local connections

- The browser extension link listens on `127.0.0.1` only (ports 47610 to 47619). It answers only programs that run
  as your Windows user in your session, and only browser-extension requests; a website cannot post to it.
- The extension copy handed out by the Games page carries a random secret for your Windows user and only reports to
  a listener that proves it knows that secret. A copy loaded straight from the source folder has no secret and
  cannot check who it talks to; on a PC shared with other people, use the handed-out copy.
- RigControl talks to OpenRGB's server on `127.0.0.1` (port 6742). When RigControl starts OpenRGB itself, it tells
  it to listen on `127.0.0.1` only. An OpenRGB that runs as a Windows service, or that you started yourself, uses
  its own settings. OpenRGB's server has no password, so check those settings if the PC is on a network you do not
  trust.
- OBS's WebSocket password is read from OBS's own settings to sign in to OBS on this PC, for read-only requests. It
  is never logged, shown or stored by RigControl.

### Lights on your network

- **Pairing keys.** Nanoleaf gives an access token at pairing; a Philips Hue Bridge gives an application key and a
  client key. They are protected with Windows data protection (DPAPI) for the signed-in Windows user before they
  are written to the settings file, and they are never written to the log or shown in the interface.
- **A key only goes to the light it came from.** One central check in the code fills a key into a request only when
  the request goes to the address that key was paired with. A request that would carry a key anywhere else is
  refused before anything is sent. A paired light with a key is never moved to a new address by a search: you pair
  it again.
- **Home network only.** Light traffic is refused for any address that is not a home-network address (or this PC
  itself). Requests to lights do not follow redirects, do not go through a proxy and carry no cookies.
- **Philips Hue.** The bridge is spoken to over https. Its certificate must carry the bridge's own id, and the
  certificate seen at pairing is remembered (pinned): later requests are refused if the bridge shows a different
  one. Pair the bridge again if it was replaced or reset.
- **Lights without a key.** Govee, Yeelight, WLED, LeetDesk and Cololight lights have no key at all. Anyone on the
  same network can control such a light, with or without RigControl. That is how those lights work; keep them on a
  network you trust. The colour stream to a Nanoleaf is not encrypted either.
- **Searching** only happens when you press Search for lights, and only on networks that have a router (which leaves out most VPN adapters and virtual
  switches; a VPN that gives the PC a router address is searched too). Answers from the network are treated as untrusted:
  - A device speaks for itself only. A light that announces itself is asked what it is at the address its answer
    really came from, on that brand's usual port, and only when that address is on a searched network. An
    announcement cannot point RigControl at another address or port.
  - A found light with an address outside the home network is dropped. At most 64 lights of one brand are listed.
  - Names, models and ids from a device are cut to a short length and stripped of control characters before they
    are shown, saved or written to the log.
  - An answer longer than 1 MB is dropped unread.
  - A light without a key that answers from a new address is noted there only when it no longer answers at its old
    address and only one device claims to be it.
- **Listening.** While it waits for an answer from a Govee light (a search, pairing, reading a paired light)
  RigControl listens on UDP port 4002 and takes an answer only from the address it asked. Yeelight's fast mode makes
  it listen on one port on your home network's address until the light has connected; only a connection from that
  light's own address is taken.
- No cloud account, sign-in or OAuth is used for lights.
- Every brand other than Nanoleaf was built from published protocols and has not been tried on a real device yet.

### Discord

- RigControl never reads Discord's stored login or token.
- It only attaches to a Discord app that runs as the same Windows user in the same session. Another user's Discord
  on a shared PC is never used, and their programs never feed your card.
- The Discord application ids are not in the public source. In the released exe they are stored encrypted. This
  keeps them out of the source and out of a simple search of the file. It does not hide them from someone who
  inspects the running app or Discord itself; they are not secrets in the cryptographic sense.

### Hardware limits

Fan and pump minimums, the pump header rule, the graphics card clock limits and the other hardware rules are listed
under "Safety" in the [README](README.md).

## Known limits

- No code signing, no installer, no automatic updates. You have to check for new releases yourself.
- Settings in `%LOCALAPPDATA%\RigControl\settings.json` can be changed by any program you run. RigControl treats
  that file as untrusted for anything that installs software or changes a service, but a program on your PC could
  still change your fan curves or lighting through it.
- The settings file also holds each paired light's address and its key in protected form. Another program running
  as your Windows user can ask Windows to unprotect that key, as with any data protected this way.
- Hardware control was tested on one PC. On other hardware a wrong assumption is possible; the README says which
  parts are untested.
- The log folder is plain text. Look through a log before you attach it to a public issue.
- What RigControl cannot defend against: code that already runs as you can set environment variables a .NET
  program reads at start (a profiler DLL through `CORECLR_PROFILER_PATH`, `DOTNET_STARTUP_HOOKS`, another garbage
  collector or JIT, a diagnostics connection). RigControl refuses to keep running elevated with them and restarts
  without them, and startup hooks are switched off in the build, but a profiler DLL named that way has already been
  loaded into the first process by .NET before any of RigControl's own code runs.
- Setup run as administrator from a folder any program can change (such as Downloads) copies itself into a new
  folder only administrators can change (`<install>\updates\setup-<random>`), checks the copy and carries on from
  there. The same limit applies: whatever Windows itself loaded from that folder before RigControl's code ran (a DLL
  placed next to the exe) is out of its reach. Run setup from a folder you trust.

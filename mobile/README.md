# RigControl Mobile

The phone app for RigControl (one .NET MAUI code base for iPhone and Android; Android is not released, only a test
build signed with a throwaway key exists). It does two things:

1. **Your PCs**: every PC running RigControl 1.2.2 or later can be linked by scanning the QR code it shows
   (Settings > Phone > Link a phone) or typing its 20-character code. The phone then shows the PC's temperatures,
   fan speeds, CPU and GPU load and recording state, and can switch the lights on or off, pick a lighting effect,
   start or stop a recording and save an instant replay. Nothing riskier (fan curves, overclocking, BIOS, drivers)
   can be done from the phone.
2. **Music on your Discord status**: "Connect Spotify" lets your PCs read what your Spotify account plays on any
   device, and "Share my iPhone's Music app song" sends the Music app's song to the PCs while this app is open. A PC
   then shows it on your Discord card. For now Spotify only lets in accounts the developer added by hand (Spotify's
   limit for apps in development mode, at most 25).

Phone and PC talk through a small relay that both connect out to, end-to-end encrypted
([docs/protocols/link.md](../docs/protocols/link.md)). The PC opens no incoming ports.

> **State of this build:** the app uses the real link (`RelayLinkService` in `RigControl.Mobile.Core`, on top of
> `src/RigControl.Link`). Typed codes go to the relay built into RigControl.Link (`LinkDefaults.RelayUrl`,
> `wss://rigcontrol-relay.rigcontrol.workers.dev`, the same one the PC uses); a scanned QR code carries its own relay
> host. Only `wss://` relays are used. A build whose `RelayUrl` is a `.invalid` address has no relay, and a typed code
> then says so. Tests never reach the real relay: they use an in-memory one. To try the screens without a PC, build with `-p:UseFakeLink=true`: the app then runs on a
> stand-in with two sample PCs (`FakeLinkService`) and Settings says so.

How the real link works on the phone: pairing claims the PC's offer on `/v1/pair`; the phone makes a new key pair
for every PC it links (so the relay can't tell two PCs belong to the same phone) and keeps the private keys in
SecureStorage (Keychain / Keystore), the rest of the list (ids, names, the PCs' public keys, epochs, relay, counters,
last seen) in Preferences. Each PC gets one relay connection while a screen needs it; it reconnects by itself with
growing pauses (1 s doubling to a minute). The relay tells only the PC who is online, so the phone learns that a PC is
there by asking it for a snapshot: the PCs list asks every 20 s, the PC screen every 2 s.

## What is where

| Folder | What |
|---|---|
| `RigControl.Mobile/` | The app: screens (`Views/`), their logic (`ViewModels/`, MVVM), platform code (`Platforms/Android`, `Platforms/iOS`) |
| `RigControl.Mobile.Core/` | Plain .NET, no phone needed: `ILinkService` (what the app needs from the link), `FakeLinkService`, the pairing code, relay address and `rigcontrol://` link checks |
| `RigControl.Mobile.Tests/` | Tests for the Core project (run on any PC) |
| `global.json` | Pins the .NET SDK to 10.0.404 (a PC may also have a newer preview SDK) |

`rigcontrol://pair?relay=<host>&code=<code>` (the PC's QR code, also when opened from the phone's own camera app)
and `rigcontrol://callback` (the end of the Spotify sign-in in the browser) open the app. A pairing link only fills
in the Link a PC screen: linking always waits for a tap on **Link**.

## Building on Windows (Android)

You need the .NET SDK 10.0.404 with the `maui` workload and JDK 17. Then, once, the Android SDK:

```powershell
cd mobile\RigControl.Mobile
dotnet build -t:InstallAndroidDependencies -f net10.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.21.102-hotspot" `
  -p:AcceptAndroidSDKLicenses=True
```

(That accepts the Android SDK licence for you; read it at https://developer.android.com/studio/terms first.)

Tests and the .apk:

```powershell
cd mobile
dotnet test RigControl.Mobile.Tests
dotnet publish RigControl.Mobile -f net10.0-android -c Release `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-17.0.21.102-hotspot"
```

The installable file is `RigControl.Mobile\bin\Release\net10.0-android\publish\io.github.rigcontrol.mobile-Signed.apk`
(about 30 MB, for 64-bit phones). It is signed with the build's throwaway debug key, which is enough to install it:
copy it to the phone and open it (Android asks to allow installs from that app once), or `adb install` it. A proper
release key comes before it is handed to other people; an update signed with a different key needs an uninstall first.

The iPhone part compiles on Windows too (`dotnet build -f net10.0-ios`), which catches code mistakes, but an
iPhone app can only be packaged on a Mac.

## The iPhone build (in the cloud)

`.github/workflows/mobile.yml` builds both phones on GitHub's machines. It runs only when started by hand
(**Actions > Mobile > Run workflow**) or for a `v*` tag, so it doesn't use Actions minutes on every push
(the macOS machine counts 10x).

- **android** (Ubuntu): tests, then `RigControl-Mobile.apk`.
- **ios** (macOS): builds for real iPhones (`ios-arm64`) **without signing** and packs `Payload/RigControl.app` into
  `RigControl-Mobile.ipa`.

Both files appear under the run's **Artifacts** (kept 14 days). No secrets or certificates are used: the .ipa is
signed later, on your own PC, with your own certificate.

## Installing on an iPhone (sideloading)

The app is not on the App Store. **It needs a paid Apple developer certificate.** The easy way is RigControl's own
installer on the PC: Settings > Phone > "Install on iPhone", with the iPhone plugged in by USB. There, "Import
certificate" takes a `.p12` and a `.mobileprovision` profile from your paid Apple Developer account (the profile must
list your iPhone), or "Install pre-signed .ipa" installs an `.ipa` that is already signed.

Free Apple IDs: since October 2026 Apple blocks free Apple ID sign-in from Windows clients (it fails at the "app
token" step). RigControl's installer still has the sign-in and says so when Apple refuses it. Where a free Apple ID
still works (for example in a tool on a Mac), apps signed with it stop opening after **7 days** and must be signed
again, and at most 3 such apps can be active at once. The tools below may hit the same block from Windows.

**Sideloadly** (Windows or Mac):
1. Install Apple's USB driver: "Apple Devices" from the Microsoft Store (or iTunes from apple.com).
2. Install Sideloadly from sideloadly.io, plug in the iPhone and tap "Trust this computer".
3. Drag `RigControl-Mobile.ipa` into Sideloadly, type your Apple ID, Start. Your password goes to Apple only.
4. On the iPhone: Settings > General > VPN & Device Management > your Apple ID > Trust. iOS 16 and later also need
   Settings > Privacy & Security > Developer Mode on (the phone restarts).

**AltStore** (altstore.io): install AltServer on the PC and AltStore on the iPhone as its guide says, then in
AltStore tap **+** and pick the .ipa. AltStore re-signs it in the background while the iPhone and PC share Wi-Fi.

RigControl's own installer is described in [src/RigControl.iPhone/README.md](../src/RigControl.iPhone/README.md).

## What is left

- Custom Discord statuses are read from the snapshot and `presence.setCustom` works, but no screen shows them yet.
- Spotify for everyone: Spotify's extended access, so accounts the developer has not added can connect.
- Android: a release signing key, and a release.
- An AltStore source file.

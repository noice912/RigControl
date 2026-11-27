# RigControl.iPhone

The iPhone installer behind Settings > Phone > "Install on iPhone". It puts RigControl Mobile on an iPhone that is
plugged into the PC with a USB cable, all from Windows, without a Mac.

**What you need:** a paid Apple developer certificate. That is a `.p12` file and a `.mobileprovision` profile from
your Apple developer account, and the profile must list your iPhone. You can also install an `.ipa` that is already
signed. Since October 2026 Apple blocks free Apple ID sign-in from Windows clients (it fails at the "app token"
step). The free sign-in code is still here, and the app shows a clear message when Apple refuses it.

It runs in a separate helper process **without administrator rights**, only when you open the installer window (or
when an iPhone it installed the app on is plugged in and the signature is about to run out).

## What is where

| Folder | What |
|---|---|
| `Anisette/` | The device-identification headers Apple wants on every sign-in request. They come from Apple's own CoreADI library, loaded from an Apple app already installed on your PC (Apple Devices, Apple Music, iTunes or iCloud). |
| `Apple/` | Apple ID sign-in (`gsa.apple.com`: SRP proof, two-factor code) and Apple's developer services (`developerservices2.apple.com`: register the iPhone, make a certificate, get a profile). |
| `Signing/` | Re-signs the `.ipa` for your team: new bundle ids, each bundle's profile, entitlements, Mach-O code signature and CMS signature. |
| `Device/` | Talks to the iPhone through Apple's USB service on the PC (usbmuxd on `127.0.0.1:27015`): lockdown with the pairing you already made by tapping "Trust", AFC to copy the app, installation_proxy to install it. |
| `Helper/` | The whole flow (`IphoneInstaller`), and the certificate and profile you import (`ImportedIdentity`). |
| `Plist/` | Apple's property lists: reads XML and binary, writes XML. |

What it stores and who it talks to: [PRIVACY.md](../../PRIVACY.md), section "Installing the phone app on an iPhone".

## Protocol references (read, not copied)

This code was written from the descriptions, source and issue reports of the projects below. **No code was copied
from any of them.** Only projects that the code itself names are listed.

| Project | What it was used for | Licence |
|---|---|---|
| Dadoum's apple-crates (https://github.com/Dadoum/apple-crates) | How CoreADI is called (function codes and message layouts, `adi/src/calls.rs`) and how it is set up on Windows (`library-coreadi/src/windows.rs`) | No licence stated in the repository (read 2026-11-27) |
| AltStore (https://github.com/altstoreio/AltStore) and AltSign (https://github.com/rileytestut/AltSign) | How sign-in and the developer services are used to sign an app; which client description Apple accepts (AltSign issue 61) | AltStore: AGPL-3.0. AltSign: no licence file in the repository (read 2026-11-27) |
| iloader (https://github.com/nab138/iloader) | Fixes for Apple's sign-in changes of October 2026 (iloader issue 1304) | MIT |
| zagstore | An issue report (number 48) on what Apple's own iTunes for Windows sends at sign-in | Not checked; only the issue was read |
| apple-codesign, part of apple-platform-rs (https://github.com/indygreg/apple-platform-rs) | A cross-check of Apple's public CA certificates that every code signature carries (they were downloaded from apple.com/certificateauthority) | MPL-2.0 |

usbmuxd, lockdown, AFC and installation_proxy are Apple's own services on the PC and the iPhone; the code names
them by Apple's names.

**Apple's CoreADI is not shipped.** RigControl loads `CoreADI64.dll` from your own Apple install, checks that Apple
signed it, and keeps a private copy for your Windows user only. Nothing of Apple's is in RigControl's files or
releases.

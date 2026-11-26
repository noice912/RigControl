# Third-party notices

RigControl is licensed under GPL-3.0 ([LICENSE](LICENSE)). It carries, links to or builds on the work listed here.
Each entry gives the name, the version this source tree refers to, the licence and where the source is. The licence
texts are in [LICENSES/](LICENSES/).

### How the licences were checked

The carried programs come as official installers or packages without a licence file of their own, so their terms
were read at the sources below on 2026-11-24. This was done by the author, who is not a lawyer.

- **PawnIO 2.2.0:** the licence notice in the README of its repository: GPL-2.0-or-later with a special exception.
  The notice is copied in full to [LICENSES/PawnIO-LICENSE.txt](LICENSES/PawnIO-LICENSE.txt). The setup file is the
  official one (it carries its author's valid code signature); it holds no licence file to compare with.
- **HIDAPI 0.15.0:** `LICENSE-bsd.txt` at the `hidapi-0.15.0` tag, copied to
  [LICENSES/HIDAPI-LICENSE-bsd.txt](LICENSES/HIDAPI-LICENSE-bsd.txt).
- **DirectX Shader Compiler (`dxcompiler.dll`, `dxil.dll`, version 10.0.19041):** both files carry Microsoft's
  valid code signature and are part of OpenRGB's official package. `dxcompiler.dll` is built from the open-source
  DirectX Shader Compiler; its licence is copied to
  [LICENSES/DirectXShaderCompiler-LICENSE.txt](LICENSES/DirectXShaderCompiler-LICENSE.txt). `dxil.dll` has no
  open-source licence. Microsoft ships both files in the Windows SDK's `Redist\D3D` folder, and the Windows SDK
  licence allows the files in its redistributable folders to be passed on as part of a program ("Distributable
  Code"). RigControl passes them on only inside OpenRGB's unchanged package, on that basis.
- **Still marked "to be confirmed" in the tables:** the licence shown is what the project's own page or package
  says; it was not compared with a licence file for exactly that version.

## 1. Separate programs carried inside `RigControl.exe`

These are unmodified official files. RigControl starts or installs them as separate programs; it does not link to
them. How they get into a build: [third_party/BUNDLED.md](third_party/BUNDLED.md).

| Program | Version | Licence | Source |
|---|---|---|---|
| OpenRGB | 1.1 (build `8e16de2`, official Windows 64-bit MSI) | GPL-2.0-only ([text](LICENSES/GPL-2.0.txt)) | https://codeberg.org/OpenRGB/OpenRGB (downloads: https://openrgb.org/releases.html) |
| PawnIO (driver setup, `PawnIO_setup.exe`) | 2.2.0 | GPL-2.0-or-later with a special exception for combining it with other free software ([notice with the exception](LICENSES/PawnIO-LICENSE.txt), [GPL text](LICENSES/GPL-2.0.txt)). RigControl's first-run screen says the same. | https://github.com/namazso/PawnIO (downloads: https://pawnio.eu) |
| Equalizer APO | 1.4.2 (official 64-bit installer) | GPL-2.0, as stated on its project page ([text](LICENSES/GPL-2.0.txt)) | https://sourceforge.net/projects/equalizerapo/ |
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`) | 9.0.2, "release essentials" build from gyan.dev | GPL-3.0 for this build ([text](LICENSES/FFmpeg-COPYING.GPLv3.txt)). The build includes other libraries under their own licences, listed by the build's maker. | https://ffmpeg.org (build and its source links: https://www.gyan.dev/ffmpeg/builds/) |

### Files that come inside OpenRGB's official package

RigControl copies OpenRGB's program folder as it is. These files are part of that folder:

| Component | Version in the package | Licence | Source |
|---|---|---|---|
| Qt 6 (`Qt6Core`, `Qt6Core5Compat`, `Qt6Gui`, `Qt6OpenGL`, `Qt6Widgets`, and the `platforms`, `styles` and `imageformats` plug-ins) | 6.8.3 | LGPL-3.0 ([text](LICENSES/LGPL-3.0.txt)), as used by OpenRGB | https://www.qt.io / https://code.qt.io |
| libusb (`libusb-1.0.dll`) | 1.0.27 | LGPL-2.1-or-later ([text](LICENSES/LGPL-2.1.txt)) | https://github.com/libusb/libusb |
| HIDAPI, hotplug build (`hidapi-hotplug.dll`) | 0.15.0 | HIDAPI is offered under GPL-3.0, BSD-3-Clause or its original licence, at the user's choice (its `LICENSE.txt`). RigControl passes it on as part of OpenRGB's package under the BSD-3-Clause choice ([its text with HIDAPI's copyright line](LICENSES/HIDAPI-LICENSE-bsd.txt)). | https://github.com/libusb/hidapi |
| PawnIO library and modules (`PawnIOLib.dll`, `LpcIO.bin`, `SmbusI801.bin`, `SmbusPIIX4.bin`, `SmbusNCT6793.bin`, `SmbusIntelSkylakeIMC.bin`, `LedsValve.bin`) | as shipped with OpenRGB 1.1 | The modules: LGPL-2.1, as the PawnIO.Modules repository states ([text](LICENSES/LGPL-2.1.txt)). `PawnIOLib.dll` is part of the PawnIO repository (see PawnIO above). To be confirmed against the versions OpenRGB 1.1 ships. | https://github.com/namazso/PawnIO, https://github.com/namazso/PawnIO.Modules |
| DirectX Shader Compiler (`dxcompiler.dll`, `dxil.dll`) | 10.0.19041 | Microsoft files that Qt's Windows deployment adds, both signed by Microsoft. `dxcompiler.dll`: the DirectX Shader Compiler's licence ([text](LICENSES/DirectXShaderCompiler-LICENSE.txt)). `dxil.dll`: no open-source licence; passed on as a redistributable Windows SDK file inside OpenRGB's unchanged package (see "How the licences were checked"). | https://github.com/microsoft/DirectXShaderCompiler |

### Where the source code is

The source of the four carried programs, for the versions carried, is on RigControl's "Helper source code" release
page on GitHub (https://github.com/noice912/RigControl/releases/tag/helper-sources). Each RigControl release's notes
link to it:

| File on that page | What it is | Taken from |
|---|---|---|
| `OpenRGB-1.1-8e16de2-source.tar.gz` | OpenRGB at commit `8e16de27858cf2be6af54a33ce003df74cb9fd3e`, the build carried | https://codeberg.org/OpenRGB/OpenRGB |
| `EqualizerAPO-src-1.4.2.zip` | Equalizer APO 1.4.2, the project's own source archive | https://sourceforge.net/projects/equalizerapo/files/1.4.2/ |
| `PawnIO-2.2.0-source.tar.gz` | PawnIO at its `2.2.0` tag (commit `5cdf470831fdfff3f7f1d06363ca6b230f3bf35a`): the driver, its library and its utility, together with the PawnPP interpreter the driver is built with (commit `e64e4c37b2d8ba0d8ee57205faf8183aee12c438`, MPL-2.0, [text](LICENSES/MPL-2.0.txt)) | https://github.com/namazso/PawnIO and https://github.com/namazso/PawnPP |
| `ffmpeg-9.0.2.tar.xz` | FFmpeg 9.0.2, the project's own release archive | https://ffmpeg.org/releases/ |

Not attached, and where to get them:

- **Inside OpenRGB's package:** Qt 6.8.3, libusb 1.0.27, HIDAPI 0.15.0 and the PawnIO modules, at the addresses in
  the table above.
- **Inside Equalizer APO's installer:** FFTW (`fftw3f.dll`, GPL-2.0-or-later, https://www.fftw.org), libsndfile
  (`sndfile.dll`, LGPL-2.1-or-later, https://github.com/libsndfile/libsndfile), Qt 6 (LGPL-3.0,
  https://code.qt.io) and Microsoft's Visual C++ runtime files. The attached Equalizer APO archive holds these as
  ready-built files in its `Setup` folder, as the project publishes them, not as source.
- **Inside the FFmpeg build:** the other libraries the gyan.dev build links in (for example x264 and x265), which
  that build's maker lists with their sources at https://www.gyan.dev/ffmpeg/builds/.
- **PawnIO's installer:** the project publishes the finished setup file only (https://github.com/namazso/PawnIO.Setup);
  the scripts that pack it are not published. What it installs is built from the attached PawnIO source.

If one of those addresses stops working, open an issue in this repository.

## 2. Libraries linked into `RigControl.exe` (NuGet)

Versions are the ones the project files and the package restore name.

| Library | Version | Licence | Source |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 ([text](LICENSES/MPL-2.0.txt)) | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| DiskInfoToolkit (used by LibreHardwareMonitorLib) | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD (used by LibreHardwareMonitorLib) | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core (used by the two above) | 1.0.7 | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp | 2.6.4 | Apache-2.0 ([text with its copyright line](LICENSES/HidSharp-LICENSE.txt)) | https://software.seekye.com/hidsharp |
| NAudio, NAudio.Core, NAudio.Wasapi, NAudio.WinMM, NAudio.WinForms, NAudio.Asio, NAudio.Midi | 2.2.1 | MIT, Copyright 2020 Mark Heath ([text](LICENSES/NAudio-LICENSE.txt)) | https://github.com/naudio/NAudio |
| NvAPIWrapper.Net | 0.8.1.101 | LGPL-3.0 per its repository ([text](LICENSES/LGPL-3.0.txt)). To be confirmed against the package. | https://github.com/falahati/NvAPIWrapper |
| Microsoft.Web.WebView2 (SDK) | 1.0.4314.35 | Microsoft's WebView2 SDK licence, BSD-3-Clause wording, Copyright (C) Microsoft Corporation ([text](LICENSES/WebView2-LICENSE.txt)) | https://aka.ms/webview |
| Mono.Posix.NETStandard (used by LibreHardwareMonitorLib) | 1.0.0 | MIT. To be confirmed: the package only links to its licence. | https://github.com/mono/mono |
| System.Management, System.CodeDom, System.IO.Ports (with `runtime.native.System.IO.Ports`), System.Threading.AccessControl | 10.0.x | MIT, Copyright (c) .NET Foundation and Contributors ([text](LICENSES/MIT.txt)) | https://github.com/dotnet/runtime |
| System.ServiceProcess.ServiceController, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, Microsoft.Win32.SystemEvents | 8.0.x | MIT, Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| System.Drawing.Common | 8.0.x | MIT, Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/winforms |
| Microsoft.Win32.Registry, System.Security.AccessControl, System.Security.Principal.Windows, Microsoft.NETCore.Platforms | 4.7.0 / 3.1.0 | MIT, Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| .NET 8 runtime, WPF and Windows Forms (the published exe is self-contained and carries them) | 8.0 | MIT, Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms |

The WebView2 **runtime** itself is Microsoft's and is not carried by RigControl. Windows 11 includes it.

LibreHardwareMonitorLib carries compiled PawnIO modules inside its own file (for example `IntelMSR`, `LpcIO`,
`SmbusI801`, `AMDFamily17`, `RyzenSMU`). They are PawnIO modules (LGPL-2.1 per that repository, see above); to be
confirmed with them.

### Only in the Linux and macOS app (`src/RigControl.Desktop`)

The Windows exe does not carry these. The Linux and macOS app carries them next to its program file, together with
the .NET 8 runtime (MIT, as above) and the same Lighting and Discord status code as the Windows app, without the
Windows-only libraries (no NAudio, no WebView2, no WPF).

| Library | Version | Licence | Source |
|---|---|---|---|
| Photino.NET | 4.0.16 | Apache-2.0 per the package ([text](LICENSES/Apache-2.0.txt)), by TryPhotino, "Copyright (C) 2025" in the package. To be confirmed: the package holds no licence or NOTICE file of its own, so the repository's must be read at this version before a Linux or macOS release. | https://github.com/tryphotino/photino.NET |
| Photino.Native (the native window library: `Photino.Native.so` on Linux, `Photino.Native.dylib` on macOS) | 4.0.22 | Apache-2.0 per the package ([text](LICENSES/Apache-2.0.txt)), by TryPhotino. To be confirmed the same way. It includes the JSON library "JSON for Modern C++" by Niels Lohmann (MIT per that project); to be confirmed. | https://github.com/tryphotino/photino.Native |

The web view itself is the system's and is not carried: WebKitGTK (`libwebkit2gtk-4.1`, LGPL-2.1 and BSD) on Linux,
which you install as a system package, and Apple's WKWebView on macOS.

## 3. Interface (npm)

Carried in the exe as part of the built interface:

| Package | Version | Licence | Source |
|---|---|---|---|
| react, react-dom | 19.3.1 (`^19.3.1` in `package.json`) | MIT, Copyright (c) Meta Platforms, Inc. and affiliates ([text](LICENSES/React-LICENSE.txt)) | https://github.com/facebook/react |
| scheduler (used by react-dom) | 0.28.0 | MIT, Copyright (c) Meta Platforms, Inc. and affiliates | https://github.com/facebook/react |
| @fontsource/barlow: the Barlow font | 5.3.0 | SIL Open Font License 1.1, Copyright 2017 The Barlow Project Authors ([text](LICENSES/Barlow-OFL-1.1.txt)) | https://github.com/jpt/barlow, https://github.com/fontsource/font-files |
| @fontsource/barlow-condensed: the Barlow Condensed font | 5.3.0 | SIL Open Font License 1.1, Copyright 2017 The Barlow Project Authors ([text](LICENSES/Barlow-Condensed-OFL-1.1.txt)) | https://github.com/jpt/barlow, https://github.com/fontsource/font-files |

Used only to build the interface, not carried in the exe: vite 8.3.8 (MIT), @vitejs/plugin-react 6.3.0 (MIT),
typescript 7.0.2 (Apache-2.0), @types/react and @types/react-dom 19.3.0 (MIT), and what they pull in (for example
rolldown, MIT, and lightningcss, MPL-2.0).

## 4. Data

| Data | Version | Licence | Source |
|---|---|---|---|
| The PCI ID database (`pci.ids`): names of NVIDIA, AMD and Intel graphics devices, built into the hardware list | Snapshot 2026.11.22, SHA-256 `f1c7173d83d868e5578408edeb3cd8cdfa33f9df543880d4f66456cfaaa12e79` | GPL-2.0-or-later or BSD-3-Clause, at the user's choice ([GPL-2.0](LICENSES/GPL-2.0.txt), [BSD-3-Clause](LICENSES/BSD-3-Clause.txt)) | https://pci-ids.ucw.cz/ |

Notice from the file: "This file can be distributed under either the GNU General Public License (version 2 or
higher) or the 3-clause BSD License. The database is a compilation of factual data, and as such the copyright only
covers the aggregation and formatting. The copyright is held by Martin Mares and Albert Pool." It is maintained by
Albert Pool, Martin Mares and other volunteers of the PCI ID Project.

## 5. Code and knowledge RigControl builds on

| Project | How it is used | Licence | Source |
|---|---|---|---|
| RichPresence | The browser extension in `extension/` was brought over from it; the Games page, Discord status, in-game panel and screen sync are a rebuild of it in C#. | MIT, Copyright (c) 2026 RichPresence contributors ([notice](extension/LICENSE-RichPresence.txt)) | https://github.com/noice912/Discord-status |
| Audio Controller | The Audio section (`src/Audio`) is this app's code, merged in. | GPL-3.0 | Same author; see `src/Audio/MERGE-NOTES.md` |
| liquidctl | The NZXT protocol code follows its documentation and driver behaviour. | GPL-3.0 | https://github.com/liquidctl/liquidctl |
| kraken-elite-screen-manager | The Kraken Elite screen streaming follows its findings. | MIT | https://github.com/KhalloufHassan/kraken-elite-screen-manager |
| NVIDIA Profile Inspector | Setting ids for DLSS and RTX HDR come from its `CustomSettingNames.xml`. | MIT | https://github.com/Orbmu2k/nvidiaProfileInspector |
| OpenRGB SDK protocol | RigControl has its own client for OpenRGB's network protocol, written from OpenRGB's source and documentation. | GPL-2.0-only (OpenRGB) | https://codeberg.org/OpenRGB/OpenRGB |

### Lights on your network: protocol descriptions

RigControl's code for Wi-Fi lights and for setting the number of lights on a header was written from these projects'
documentation and observed behaviour. **No code was copied from any of them**, and none of them is carried in the
exe. They are named here to say thank you and to say where the knowledge comes from. Apart from Nanoleaf, none of
this has been tried on a real device yet.

| Project | What it was used for | Licence | Source |
|---|---|---|---|
| WLED and its documentation | How a WLED light is found, read and sent colours | EUPL-1.2 | https://github.com/wled/WLED, https://kno.wled.ge |
| Yeelight WiFi Light Inter-Operation Specification | Yeelight's local protocol (search, commands, fast mode) | The maker's published specification | https://www.yeelight.com |
| python-yeelight | How Yeelight lights behave in practice | BSD-2-Clause | https://gitlab.com/stavros/python-yeelight |
| govee-local-api | Govee's LAN protocol | Apache-2.0 | https://github.com/Galorhallen/govee-local-api |
| govee2mqtt | Govee's LAN protocol and its limits | MIT | https://github.com/wez/govee2mqtt |
| OpenRGB | Govee's per-piece colour stream, and how a header's number of lights is set (zone resize) | GPL-2.0-only | https://codeberg.org/OpenRGB/OpenRGB |
| pycololight | The Cololight messages (LifeSmart publishes none) | MIT | https://github.com/BazaJayGee66/pycololight |
| Hyperion.ng | How a Hue Bridge is paired and driven | MIT | https://github.com/hyperion-project/hyperion.ng |
| aiohue and Home Assistant | The Hue Bridge's local API in practice | Apache-2.0 | https://github.com/home-assistant-libs/aiohue |
| Q42.HueApi | The Hue Bridge's local API in practice | MIT | https://github.com/michielpost/Q42.HueApi |
| OpenHue | A written description of the Hue Bridge's local API | Apache-2.0 | https://github.com/openhue/openhue-api |
| SignalRGB's public LeetDesk add-on | Behaviour only: which pages a LeetDesk Aura answers on. LeetDesk publishes no protocol. | Not checked; nothing was copied | SignalRGB's public add-on collection |

## 6. Used by tests only (not carried in the exe)

xunit 2.9.x (Apache-2.0), xunit.runner.visualstudio (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

## 7. Not distributed with RigControl

RigControl works with these but does not carry them, and none of their code is in it:

- **VB-Audio Virtual Cable** (donationware, proprietary). You install it yourself from https://vb-audio.com/Cable.
- **Discord**, **OBS Studio**, **NZXT CAM**, **Armoury Crate**, **GIGABYTE Control Center**, NVIDIA's and ASUS's
  drivers and installers. RigControl talks to or works beside whatever is installed.
- **WebKitGTK** on Linux and **WKWebView** on macOS: the system's own web view that the Linux and macOS app shows
  its interface in. On Linux, **OpenRGB** is also yours to install and start; RigControl only connects to it there.
- **Online services** it can ask for information (LRCLIB, Apple's iTunes Search, TVmaze, Cinemeta, Enka.Network).
  See [PRIVACY.md](PRIVACY.md).

## Trademarks

NVIDIA, GeForce, NZXT, Kraken, ASUS, ROG, GIGABYTE, Razer, Nanoleaf, Philips Hue (Signify), Govee, Yeelight, WLED,
LifeSmart Cololight, LeetDesk, SignalRGB (WhirlwindFX), Corsair, Thermaltake, Lian Li, Discord, Intel, AMD, Ryzen, Microsoft, Windows and
the other product names in this repository belong to their owners. RigControl is not made, endorsed or supported
by any of them, and it is not a competitor of their products.

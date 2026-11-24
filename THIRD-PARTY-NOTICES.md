# Third-party notices

RigControl is licensed under GPL-3.0 ([LICENSE](LICENSE)). It carries, links to or builds on the work listed here.
Each entry gives the name, the version this source tree refers to, the licence and where the source is. The licence
texts are in [LICENSES/](LICENSES/).

"To be confirmed" means the licence was not checked against the component's own licence file for exactly this
version. Those entries name what the project's own pages say and must be checked before a release.

### Still to do before the first release

The carried files below come without a licence file of their own, so their terms were read on the projects' own
repository pages (2026-11-24), not in the pinned files. Before a release is published:

1. Compare each with the licence file at the exact version carried (PawnIO 2.2.0, the PawnIO modules and library as
   shipped with OpenRGB 1.1, HIDAPI 0.15.0, the DirectX Shader Compiler files in OpenRGB's package).
2. Add to [LICENSES/](LICENSES/): PawnIO's exception wording, HIDAPI's `LICENSE-bsd.txt` (it holds HIDAPI's
   copyright lines) and the DirectX Shader Compiler's `LICENSE.TXT`.
3. Find the terms `dxil.dll` may be passed on under. If none allow it, leave that file out.

## 1. Separate programs carried inside `RigControl.exe`

These are unmodified official files. RigControl starts or installs them as separate programs; it does not link to
them. How they get into a build: [third_party/BUNDLED.md](third_party/BUNDLED.md).

| Program | Version | Licence | Source |
|---|---|---|---|
| OpenRGB | 1.1 (build `8e16de2`, official Windows 64-bit MSI) | GPL-2.0-only ([text](LICENSES/GPL-2.0.txt)) | https://codeberg.org/OpenRGB/OpenRGB (downloads: https://openrgb.org/releases.html) |
| PawnIO (driver setup, `PawnIO_setup.exe`) | 2.2.0 | GPL-2.0-or-later with a special exception for combining it with other free software, as its repository states ([GPL text](LICENSES/GPL-2.0.txt); the exception is in the project's README). Not yet compared with the files of the 2.2.0 setup. RigControl's first-run screen says the same. | https://github.com/namazso/PawnIO (downloads: https://pawnio.eu) |
| Equalizer APO | 1.4.2 (official 64-bit installer) | GPL-2.0, as stated on its project page ([text](LICENSES/GPL-2.0.txt)) | https://sourceforge.net/projects/equalizerapo/ |
| FFmpeg (`ffmpeg.exe`, `ffprobe.exe`) | 9.0.2, "release essentials" build from gyan.dev | GPL-3.0 for this build ([text](third_party/ffmpeg/COPYING.GPLv3.txt)). The build includes other libraries under their own licences, listed by the build's maker. | https://ffmpeg.org (build and its source links: https://www.gyan.dev/ffmpeg/builds/) |

### Files that come inside OpenRGB's official package

RigControl copies OpenRGB's program folder as it is. These files are part of that folder:

| Component | Version in the package | Licence | Source |
|---|---|---|---|
| Qt 6 (`Qt6Core`, `Qt6Core5Compat`, `Qt6Gui`, `Qt6OpenGL`, `Qt6Widgets`, and the `platforms`, `styles` and `imageformats` plug-ins) | 6.8.3 | LGPL-3.0 ([text](LICENSES/LGPL-3.0.txt)), as used by OpenRGB | https://www.qt.io / https://code.qt.io |
| libusb (`libusb-1.0.dll`) | 1.0.27 | LGPL-2.1-or-later ([text](LICENSES/LGPL-2.1.txt)) | https://github.com/libusb/libusb |
| HIDAPI, hotplug build (`hidapi-hotplug.dll`) | 0.15.0 | HIDAPI is offered under GPL-3.0, BSD-3-Clause or its original licence, at the user's choice (its `LICENSE.txt`). RigControl passes it on as part of OpenRGB's package under the BSD-3-Clause choice ([text](LICENSES/BSD-3-Clause.txt)). To be confirmed: HIDAPI's own copyright lines (`LICENSE-bsd.txt` at this version) are not in this repository yet. | https://github.com/libusb/hidapi |
| PawnIO library and modules (`PawnIOLib.dll`, `LpcIO.bin`, `SmbusI801.bin`, `SmbusPIIX4.bin`, `SmbusNCT6793.bin`, `SmbusIntelSkylakeIMC.bin`, `LedsValve.bin`) | as shipped with OpenRGB 1.1 | The modules: LGPL-2.1, as the PawnIO.Modules repository states ([text](LICENSES/LGPL-2.1.txt)). `PawnIOLib.dll` is part of the PawnIO repository (see PawnIO above). To be confirmed against the versions OpenRGB 1.1 ships. | https://github.com/namazso/PawnIO, https://github.com/namazso/PawnIO.Modules |
| DirectX Shader Compiler (`dxcompiler.dll`, `dxil.dll`) | 10.0.19041 | Microsoft files that Qt's Windows deployment adds. The compiler's repository states the University of Illinois Open Source License; its text is not in this repository yet. To be confirmed, `dxil.dll` in particular: its terms were not found. | https://github.com/microsoft/DirectXShaderCompiler |

The source code for the GPL and LGPL programs above is published by their projects at the links given, for the
versions named.

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

## 6. Used by tests only (not carried in the exe)

xunit 2.9.x (Apache-2.0), xunit.runner.visualstudio (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

## 7. Not distributed with RigControl

RigControl works with these but does not carry them, and none of their code is in it:

- **VB-Audio Virtual Cable** (donationware, proprietary). You install it yourself from https://vb-audio.com/Cable.
- **Discord**, **OBS Studio**, **NZXT CAM**, **Armoury Crate**, **GIGABYTE Control Center**, NVIDIA's and ASUS's
  drivers and installers. RigControl talks to or works beside whatever is installed.
- **Online services** it can ask for information (LRCLIB, Apple's iTunes Search, TVmaze, Cinemeta, Enka.Network).
  See [PRIVACY.md](PRIVACY.md).

## Trademarks

NVIDIA, GeForce, NZXT, Kraken, ASUS, ROG, GIGABYTE, Razer, Nanoleaf, Discord, Intel, AMD, Ryzen, Microsoft, Windows
and the other product names in this repository belong to their owners. RigControl is not made, endorsed or supported
by any of them.

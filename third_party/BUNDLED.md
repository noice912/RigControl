# Helper programs RigControl carries inside its exe

RigControl's lighting, sensors, fans and audio equaliser rely on three open-source programs. So that nothing has to
be downloaded separately, the release build carries their official, unmodified files inside `RigControl.exe`.

| Program | Version | What RigControl uses it for | How it is set up | Licence | Source code |
|---|---|---|---|---|---|
| OpenRGB | 1.1 (`8e16de2`) | Every RGB light | Unpacked to `C:\Program Files\RigControl\OpenRGB` and run hidden as RigControl's own copy. If an OpenRGB service already exists on the PC, the service is pointed at this copy and its `service_config` folder is copied over; the old files stay where they are. | GPL-2.0-only (its Qt files: LGPL-3.0) | https://codeberg.org/OpenRGB/OpenRGB |
| PawnIO | setup 2.2.0 | The signed kernel driver that reads sensors and drives motherboard fans, and that OpenRGB uses for memory and motherboard lights | Its own installer is run silently if PawnIO is not installed | GPL-2.0-or-later with PawnIO's own exception, as its repository states (not yet compared with the 2.2.0 setup's files; see the notices) | https://github.com/namazso/PawnIO |
| Equalizer APO | 1.4.2 | The audio equaliser | Its own installer is run silently if it is not installed. Windows needs one restart afterwards. | GPL-2.0 | https://sourceforge.net/projects/equalizerapo/ |

They are separate programs that RigControl starts or installs. RigControl does not link to them. Licence texts and
the other files inside OpenRGB's package are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

FFmpeg is carried too, in a different way: see [ffmpeg/README.md](ffmpeg/README.md).

## When they are installed

The helpers are **required**. On a PC that has not used RigControl before, the first-time setup shows one step that
lists all three with what each is and its licence, and one **Install and continue** button. Nothing is installed
before the user has seen that step and pressed the button; in particular the kernel driver is never installed
unseen. They cannot be declined one by one. Leaving the setup installs nothing, and the hardware features do not
start.

- Installing needs administrator rights. Without them the setup says so and installs nothing.
- The yes is stored in `HKLM\SOFTWARE\RigControl\Bundled`, where only administrators can write. A "yes" in the
  settings file (which any program can change) never installs anything.
- A helper that is already on the PC counts as set up and is left alone. A PC that already has all of them sees
  nothing.
- Moving an existing OpenRGB service to RigControl's copy is part of the same yes.

## How the files are protected

- The build records every carried file's SHA-256 inside the exe (`RigControl.Bundled.csproj`). A file is only
  written or run if it matches.
- Before RigControl's OpenRGB is started, RigControl checks that it is the copy in its own folder, that the folder
  cannot be changed by programs without administrator rights, and that every file still matches.

## Building a release that carries them

The binaries are not kept in git (`.gitignore` lists the three folders). To fill them:

1. Download the three official installers into one folder (the script looks in your Downloads folder unless you
   pass `-From`):
   - `OpenRGB_1.1_Windows_64_8e16de2.msi` from https://openrgb.org/releases.html
   - `PawnIO_setup.exe` from https://pawnio.eu/
   - `EqualizerAPO-x64-1.4.2.exe` from https://sourceforge.net/projects/equalizerapo/
2. Run `third_party\prepare-bundled.ps1`. It checks each file against the SHA-256 pinned in the script and stops if
   one differs. It unpacks OpenRGB's MSI without installing it into `third_party\openrgb\files`, and copies the
   two installers into `third_party\pawnio` and `third_party\equalizerapo`.
3. Build or publish as usual.

A build without these files still works. It carries no helpers and uses whatever is already installed. The
first-time setup and Settings > Built in name the helpers that are missing and say that what depends on them will
not work until you install them yourself.

To move to a newer version of a helper: download the official file, check it, update its name and SHA-256 in
`prepare-bundled.ps1`, and update the version in this file, in `THIRD-PARTY-NOTICES.md` and in the README.

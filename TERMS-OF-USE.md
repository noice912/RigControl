# RigControl terms of use

By installing RigControl you agree to these terms. The installer shows them on its first page, and installing goes
ahead only once you accept them. Read them before you install.

These terms do not take away any right that RigControl's licence gives you. RigControl is free software under the
GNU General Public License version 3 ([LICENSE](LICENSE)). You may use, study, change and share it under that licence,
whether or not you install it.

## 1. Programs installed together with RigControl

RigControl needs three helper programs. **They are required, and the installer installs them as part of RigControl.**
It does not ask about each one separately: accepting these terms is your agreement to install them.

| Program | What it is | What gets installed |
|---|---|---|
| **OpenRGB** 1.1 | Controls RGB lighting on most brands of hardware | RigControl's own copy in `C:\Program Files\RigControl\OpenRGB`. If an OpenRGB service already exists on your PC, it is pointed at this copy. |
| **PawnIO** 2.2.0 | A signed Windows **kernel driver** that lets OpenRGB and the sensors reach the motherboard's lighting and sensor chips | Installed as a normal Windows program, the same way its own installer does it. It loads with Windows. |
| **Equalizer APO** 1.4.2 | An audio processing add-on that the sound page uses for its equaliser | Installed as a normal Windows program. It hooks into Windows' sound engine for the playback devices it is set up on. |

RigControl also carries **FFmpeg** 9.0.2 for recording and instant replay. Nothing is installed for it: it is
unpacked into RigControl's own folder.

If PawnIO or Equalizer APO is already installed, the installer leaves it as it is. Some changes, such as PawnIO's
driver and Equalizer APO's sound-engine hook, only take effect after a restart.

The helpers are made by other people and come under their own licences, which these terms do not change: OpenRGB
GPL-2.0-only, PawnIO GPL-2.0-or-later with a special exception, Equalizer APO GPL-2.0, FFmpeg GPL-3.0. Their
licence texts and the places to get their source code are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Each release also offers their source code for download.

## 2. What RigControl changes on your PC

RigControl is a hardware tool. When you use its features, it can change fan speeds, lighting, power plans, audio
settings, drivers and (on supported boards) firmware settings. It shows each change and asks you before changes that
are hard to undo. Settings you change yourself are your responsibility: wrong fan curves, power limits or voltages can
make a PC overheat, crash or become unstable.

What RigControl stores, and what it sends over the internet and when, is described in [PRIVACY.md](PRIVACY.md).

## 3. Removing it

Removing RigControl removes its own files. The helper programs stay installed, because other programs on your PC may
use them as well (PawnIO in particular is shared by several hardware tools). You can remove them yourself from
Windows' "Installed apps". [PRIVACY.md](PRIVACY.md) ("Removing it") lists every step.

## 4. No warranty

RigControl and the helper programs come **without any warranty**, as their licences say (sections 15 and 16 of the
GPL version 3, and the same sections in the other licences). To the extent the law allows, the people who wrote them
are not liable for any damage that comes from using them, including damage to hardware or loss of data.

## 5. Updates and reviews

RigControl checks GitHub for a new version (you can switch this off in Settings > Updates). It installs one only when
you press "Update", and only after checking that the update is signed by RigControl's author and matches that
signature exactly. Updates come with the terms of that version.

If you write a review in RigControl, you sign in with your own GitHub account and the review is published on GitHub,
publicly and under your GitHub name. GitHub's own terms apply to it.

## 6. Other services

Some features use other companies' services, such as Discord for the Discord status, GitHub for updates and reviews,
or the makers' own download servers for drivers. Their own terms apply when you use them. RigControl is not made or endorsed
by any of those companies, or by the makers of your hardware.

RigControl is an independent project and not a competitor of any official product, such as SignalRGB, NZXT CAM,
ASUS Armoury Crate, GIGABYTE Control Center or other makers' apps. Their names appear only to describe what RigControl
works with or resembles.

## 6a. The phone link relay

The phone link (Settings > Phone) uses a relay run by RigControl's author on Cloudflare. It is offered as it is, for
free, with no promise that it is available, and it may be changed or stopped. When it is down, a phone cannot reach the
PC; the PC itself works as before. The relay passes on sealed messages it cannot read; what it stores is listed in
[PRIVACY.md](PRIVACY.md). Do not use it to send anything other than what RigControl sends, and do not try to overload
it. Remove a phone on Settings > Phone to unlink it.

## 7. Changes to these terms

A new version of RigControl may come with new terms. Each version carries its terms: they are written to the
`licenses` folder inside the install folder and shown in Settings > Licences. The setup window shows them on a fresh
install.

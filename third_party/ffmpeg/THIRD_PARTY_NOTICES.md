# Third-party notices

| Component | Version | License | How it is used |
|---|---|---|---|
| NAudio / NAudio.Core | 2.2.1 | MIT | Audio device enumeration, WASAPI capture/render, mixing, WAV I/O (NuGet, linked) |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | DPAPI protection of the AES master key (NuGet, linked) |
| xunit (+ runner, Test SDK) | 2.9.2 / 17.11.1 | Apache-2.0 / MIT | Unit tests only, not shipped |
| FFmpeg ("release essentials" build from gyan.dev, linked from ffmpeg.org) | 9.0.2 | GPL-3.0 (this build) | **Separate, unmodified executable** invoked as a child process for decoding. Not linked into the app. Distributed alongside the app in release zips with this notice. Source: https://ffmpeg.org / build: https://www.gyan.dev/ffmpeg/builds/ |
| VB-Audio Virtual Cable | user-installed | Donationware (proprietary) | NOT distributed with this project. The user installs it from https://vb-audio.com/Cable; the app only detects its "CABLE Input"/"CABLE Output" endpoints (also after they are renamed in Windows). |
| Equalizer APO | 1.4.2 (official 64-bit installer) | GPL-2.0 | A separate program. RigControl carries its unmodified official installer and runs it at first-time setup after the user presses "Install and continue"; no code from it is used. Source: https://sourceforge.net/projects/equalizerapo/. The audio code only reads its registry entries (install/config folder, which endpoints it is attached to), writes its own plain-text config file `AudioController.txt` into its config folder, adds one `Include: AudioController.txt` line to `config.txt`, and can launch its `DeviceSelector.exe`. |

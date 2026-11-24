# Third-party notices

| Component | Version | License | How it is used |
|---|---|---|---|
| NAudio / NAudio.Core | 2.2.1 | MIT | Audio device enumeration, WASAPI capture/render, mixing, WAV I/O (NuGet, linked) |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | DPAPI protection of the AES master key (NuGet, linked) |
| xunit (+ runner, Test SDK) | 2.9.2 / 17.11.1 | Apache-2.0 / MIT | Unit tests only, not shipped |
| FFmpeg ("release essentials" build from gyan.dev, linked from ffmpeg.org) | 9.0.2 | GPL-3.0 (this build) | **Separate, unmodified executable** invoked as a child process for decoding. Not linked into the app. Distributed alongside the app in release zips with this notice. Source: https://ffmpeg.org / build: https://www.gyan.dev/ffmpeg/builds/ |
| VB-Audio Virtual Cable | user-installed | Donationware (proprietary) | NOT distributed with this project. The user installs it from https://vb-audio.com/Cable; the app only detects its "CABLE Input"/"CABLE Output" endpoints (also after they are renamed in Windows). |
| Equalizer APO | user-installed (tested with 1.4.2) | GPL-2.0 | NOT distributed with this project and no code from it is used. The user installs it from https://sourceforge.net/projects/equalizerapo/. Audio Controller only reads its registry entries (install/config folder, which endpoints it is attached to), writes its own plain-text config file `AudioController.txt` into Equalizer APO's config folder, adds one `Include: AudioController.txt` line to `config.txt`, and can launch the user's installed `DeviceSelector.exe`. |

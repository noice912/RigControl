# Bundled FFmpeg

`ffmpeg.exe` and `ffprobe.exe` go in this folder. They are **not** committed to git
(each is ~105 MB, over GitHub's 100 MB per-file limit). The released `RigControl.exe`
carries them inside itself.

To put them here before building, download the pinned official build
https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip
(gyan.dev is linked from ffmpeg.org), check that the archive's SHA-256 is
`60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`
(the value gyan.dev publishes as `.zip.sha256`), then unzip and copy
`bin\ffmpeg.exe` and `bin\ffprobe.exe` here. The build does not check this hash for you:
it records the SHA-256 of whatever two files are here, so check the archive yourself.

## In RigControl

This folder is the copy RigControl builds from (`ffmpeg.exe` and `ffprobe.exe` are byte-identical to the
standalone Audio Controller's `tools/ffmpeg/`). RigControl ships as one exe, so the build embeds the two
tools and the notice files in this folder into `AudioController.Ffmpeg.dll`
(`src/Audio/AudioController.Ffmpeg/FfmpegPayload.targets`) together with their SHA-256s. The first time the
Audio section starts they are written to `%LOCALAPPDATA%\RigControl\ffmpeg\<first 16 hex of the payload's
SHA-256>\` and verified; later starts verify and reuse that copy. FFmpeg stays a separate, unmodified
executable run as a child process (GPL-3.0 build; `COPYING.GPLv3.txt` and `THIRD_PARTY_NOTICES.md` travel
with it). Details: `src/Audio/MERGE-NOTES.md`, section 3.

If the two `.exe` files are missing here the audio projects do not build (the error names the missing file).

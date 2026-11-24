# Manual test checklist — device management & routing

Run these after building, on real hardware. Automated tests cannot cover them
(they need actual devices, VB-Cable, and ears).

## Devices panel
- [ ] Device list matches Windows Settings → Sound (same outputs, inputs, states).
- [ ] Bluetooth group shows the Crusher ANC 2 when connected, with the SKULLCANDY badge
      and (when Windows exposes it) a battery percentage.
- [ ] Moving a volume slider is reflected live in the Windows volume mixer, and vice versa.
- [ ] Mute toggle matches the Windows mute state both ways.
- [ ] "Default" button changes the Windows default device (check Settings → Sound).
- [ ] With the "also Communications" toggle on, the communications default changes too.
- [ ] Default-device changes persist after a reboot (Windows owns this — verify anyway).
- [ ] Enhancements toggle appears only on devices that support it; flipping it changes
      Windows' "Audio enhancements" state for that device.
- [ ] Unplugging/replugging a device refreshes the list within a second or two.

## Virtual microphone routing (needs VB-Cable installed)
- [ ] With VB-Cable missing: app starts, shows the setup banner, nothing crashes.
- [ ] With VB-Cable installed: Settings preselects "CABLE Input" as output.
- [ ] Discord/OBS lists "CABLE Output" as a microphone; selecting it carries your voice.
- [ ] With no sound playing, voice through the cable sounds identical to the raw mic
      (no gain change, no EQ, no artifacts).
- [ ] Playing a slot mixes the sound under your voice for the listener; stopping it
      returns to clean pass-through.
- [ ] Multiple sounds can play at once and all mix.
- [ ] "Hear soundboard locally" plays the sound on your own headphones too, and your
      mic is never audible in that local monitor.
- [ ] Peak meter moves with voice/sound; Stop All silences everything instantly.

## Hotkeys
- [ ] Slot hotkeys fire while another app has focus.
- [ ] A conflicting gesture (used twice, or taken by another app) is reported in a
      dialog and marked "(conflict)" in the slot editor.
- [ ] Stop-all hotkey works globally.
- [ ] Hold-to-play: mouse hold plays while pressed; hotkey hold stops ~0.5 s after the
      key is released (documented auto-repeat limitation).

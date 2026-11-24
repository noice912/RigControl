# NZXT Kraken Elite (USB 1E71:300C) - cooling control and status protocol

> **About this file.** These are research notes written while RigControl was being developed, before and during the
> first builds. They describe the protocol and what was observed on the one development PC at that time. Where they
> say how RigControl should behave, the code and [docs/API.md](../API.md) are what counts today.

Spec for RigControl's own Kraken driver (replaces NZXT CAM for cooling).
Research date: 2026-11-15 (second pass the same day: every source citation, PnP fact, web quote and test vector
below was re-checked; corrections are listed in section 17. A third, adversarial pass the same day re-derived every
byte from source and fixed the items listed in section 18 "Verification log". A fourth adversarial pass re-derived everything
again from source, packages and the web and recorded its corrections in section 19 "Verification log (fourth pass)". A fifth
adversarial pass re-derived everything once more, this time reading the web sources as **raw text** (kernel source and docs from
raw.githubusercontent.com, GitHub issues and PR comments through the GitHub REST API) instead of through a summarising tool; its
corrections and confirmations are in section 20 "Verification log (fifth pass)"). Nothing in this document was verified against the live
device: no HID/USB traffic was sent or captured. Everything comes from reading reference source,
read-only PnP enumeration of the development PC, and web cross-checks.

Conventions used below:

- All offsets are **0-based byte offsets into the 64-byte HID report, where offset 0 is the HID Report ID**.
- Hex is written `0x72` or, in packet dumps, as bare `72`.
- **UNVERIFIED** marks anything not confirmed in source; each one says what would confirm it. They are collected again in section 15.
- "Design rule" marks a RigControl policy recommended by this spec. Design rules are not protocol facts.

---

## 0. Sources

| Tag | Source | Revision | Role |
|---|---|---|---|
| LQ | `ref/liquidctl/liquidctl/driver/kraken3.py` (class `KrakenZ3`, which covers 2023 / Elite) | commit `292dc56` (2026-06-16) | Primary |
| LQ-usb | `ref/liquidctl/liquidctl/driver/usb.py` (`HidapiDevice`) | same | HID framing |
| LQ-util | `ref/liquidctl/liquidctl/util.py` (`normalize_profile`, `interpolate_profile`, `clamp`) | same | Curve maths |
| LQ-test | `ref/liquidctl/tests/test_kraken3.py`, `tests/test_krakenz3_response.py`, `tests/_testutils.py` | same | Test vectors |
| LQ-doc | `ref/liquidctl/docs/kraken-x3-z3-guide.md`, `docs/developer/protocol/nzxt_x3-z3-2023.md` | same | Behaviour, protocol notes |
| LHM | `ref/LibreHardwareMonitor/LibreHardwareMonitorLib/Hardware/Controller/Nzxt/KrakenV3.cs`, `NzxtGroup.cs`, `Hardware/Computer.cs`, `Hardware/Control.cs` | commit `9e2d677` (2026-11-11) | Independent C# implementation that names PID 0x300C |
| ORGB | `ref/OpenRGB/Controllers/NZXTHue2Controller/*`, `Controllers/NZXTKrakenController/*` | commit `d09a493` (2026-11-14) | Cross-check of framing and firmware query only (see 0.1) |
| PNP | Read-only `Get-PnpDevice` / `Get-PnpDeviceProperty` / `Get-Process` / `Win32_Service` on the development PC, 2026-11-15 | - | Interface layout, CAM presence |
| WEB-K | Linux kernel `drivers/hwmon/nzxt-kraken3.c` and `Documentation/hwmon/nzxt-kraken3.rst` (docs.kernel.org, raw.githubusercontent.com/torvalds/linux) | master, fetched 2026-11-15 | Third independent implementation |
| WEB-G | GitHub liquidctl issues #128, #627, #631 and PR #605 | fetched 2026-11-15 | Anecdotal device facts |

Web access **was available**. Passes 1-4 read the web pages through a summarising fetch tool. The fifth pass re-read all of
them as raw text: `drivers/hwmon/nzxt-kraken3.c` and `Documentation/hwmon/nzxt-kraken3.rst` from raw.githubusercontent.com
(torvalds/linux master), and liquidctl issues #128, #627, #631 and PR #605 (body and all 67 comments) as JSON from the GitHub
REST API. Every WEB-K constant and every WEB-G quote and byte string below was compared against that raw text (section 20).
They are still third-party statements about other people's units, not observations of the development PC's device.

### 0.1 What OpenRGB does and does not cover

- OpenRGB has **no entry for PID 0x300C**. `NZXTHue2Devices.h` lines 24-45 (the `#define` block under the "NZXT Hue 2 product IDs" banner at lines 21-23) list 0x3012 (Kraken 2024 Elite RGB) and 0x3014 (2024 Plus) but not 0x300C. (The same list names the other NZXT devices on the development PC: 0x2011 = `NZXT_RGB_FAN_CONTROLLER4_PID`, 0x2021 = `NZXT_RGB_CONTROLLER_2_PID`.)
- `NZXTKrakenControllerDetect.cpp` lines 17-19 only covers the old X2/M2 Krakens (PIDs 0x170E, 0x1715), which use a different protocol. Do not use it.
- OpenRGB never implements Kraken cooling. Its `SendFan` (`NZXTHue2Controller.cpp` lines 141-168) uses command `0x62 0x01`, which belongs to NZXT Smart Device / RGB & Fan Controller hardware. **It is not a Kraken command** (see NEVER SEND).
- Useful agreement: OpenRGB sends the same firmware query (`0x10 0x01`), expects reply `0x11 0x01` with the version at offsets 0x11..0x13, with the command byte at offset 0 (`SendFirmwareRequest`, lines 903-930).
- **Caution on report length:** OpenRGB does *not* use 64-byte packets for the whole family. Its device table (`NZXTHue2Devices.cpp`, entries for 0x3012 and 0x3014) uses `NZXT_HUE_2_PACKET_SIZE_64` for the 2024 Elite RGB (0x3012) but `NZXT_HUE_2_PACKET_SIZE_512` for the 2024 Plus (0x3014). So newer Krakens can have 512-byte reports. This is why the runtime report-length check in 1.2 (U1) is mandatory.

---

## 1. Device identification and HID interface selection on Windows

### 1.1 What the development PC actually exposes (PNP, read-only enumeration)

The Kraken Elite is a USB composite device with two interfaces. Device serial numbers are deliberately omitted here.

Provenance note (fourth pass): the PnP table below and the CAM snapshot in 12.1 were observed by earlier passes. The fourth pass was limited to reading source, packages and the web, so it did **not** re-observe them (**UNVERIFIED (U21)** as a current snapshot). The interface layout is independently consistent with the USB descriptors in WEB-G #627 (below). Nothing depends on the snapshot being current: the selection rule and the CAM detection in 12.4 re-check everything at runtime.

| Node | Class / service | Friendly name | Purpose |
|---|---|---|---|
| `USB\VID_1E71&PID_300C\<serial>` | USB / `usbccgp` | USB Composite Device | Parent. Hardware ID shows `REV_0201` (bcdDevice 2.01) |
| `USB\VID_1E71&PID_300C&MI_00\...` | USBDevice / `WINUSB` | NZXT Kraken Elite | **Non-HID interface. LCD image upload (bulk).** Out of scope, never open (section 9) |
| `USB\VID_1E71&PID_300C&MI_01\...` | HIDClass / `HidUsb` | USB Input Device | HID transport |
| `HID\VID_1E71&PID_300C&MI_01\...` | HIDClass | HID-compliant vendor-defined device | **The one to open.** Hardware IDs include `HID_DEVICE_UP:FF00_U:0001` |

Facts and consequences:

- There is exactly **one HID top-level collection** for this PID: usage page `0xFF00` (vendor-defined), usage `0x0001`. No keyboard/mouse/consumer collections exist, so there is no ambiguity.
- Selection rule for RigControl: enumerate HID devices with VID `0x1E71` and PID `0x300C`; expect exactly one; its device path contains `mi_01`. As a sanity check require usage page `0xFF00`, usage `0x0001`. If zero or more than one match, do not write anything.
- No kernel driver, no driver replacement and no elevation are needed for the HID interface (it is the stock `HidUsb` stack), so HVCI / memory integrity is not a factor. (General Windows knowledge; not stated in the references.)
- WEB-G issue #627 reports the USB descriptors of a 300C unit: interface 0 vendor-specific with one **bulk OUT endpoint 0x02**; interface 1 HID with **interrupt IN 0x81 and interrupt OUT 0x01, 64 bytes each** (`wMaxPacketSize 0x0040`). The bulk endpoint's `wMaxPacketSize` is 512. The HID class descriptor gives `wDescriptorLength 360` (a 360-byte report descriptor, which is large and consistent with many numbered reports), but the descriptor contents were not dumped ("UNAVAILABLE" in the issue). This matches the PNP layout above and LQ `_bulk_write` (line 777, endpoint `0x2`). Note: a 64-byte endpoint does not by itself prove 64-byte reports (a report can span several packets); see U1.
- The development PC also has other NZXT HID devices that share VID 0x1E71 and the same usage page/usage: one `1E71:2011` and **two** `1E71:2021`. They are different products. Always filter on PID `0x300C`.

### 1.2 Report framing

| Property | Value | Evidence |
|---|---|---|
| Report IDs used | **Yes.** Byte 0 of every report is the Report ID, and it doubles as the command / response code | LQ-test asserts `pump_report.number == 0x72` where `number` is the first written byte (`_testutils.py` line 59, `test_kraken3.py` line 421). WEB-K: kernel driver dispatches on `report->id` (0x75, 0x11). LHM and ORGB both write buffers whose first byte is the command to hidapi/HidSharp, which treat byte 0 as the Report ID. Strongest local evidence: the HidSharp 2.6.4 XML docs (`HidDevice.GetMaxInputReportLength`) say that for a device without Report IDs "the first byte will always be 0"; LHM, using HidSharp, only accepts a report whose byte 0 is `0x11` (firmware) or `0x75` (status) (KrakenV3.cs lines 83, 212) and ships 300C support, which can only work if the device uses numbered reports. Further (reasoning from Linux HID core behaviour, not stated in the references): the kernel driver lists PID 0x300C explicitly and compares `report->id` with `STATUS_REPORT_ID 0x75` / `FIRMWARE_REPORT_ID 0x11`; the HID core only sets a non-zero `report->id` when the report descriptor declares numbered reports. Note that liquidctl's Linux tests cannot show this either way, because Linux hidraw sends byte 0 on the wire whether or not report IDs exist |
| Host to device | **Output reports**, written with a normal HID write (interrupt OUT). Not feature reports, not SetOutputReport over the control pipe | LQ-usb `HidapiDevice.write` (lines 468-485) -> `hid_write`; LHM `HidStream.Write`; WEB-K `kraken3_write_expanded` copies the command into a buffer zero-padded to `MAX_REPORT_LENGTH` (64) with `memcpy_and_pad` and sends it with `hid_hw_output_report(hdev, buffer, 64)` under `buffer_lock` (that kernel function sends an output report on the interrupt OUT endpoint when one exists, general Linux HID knowledge) |
| Device to host | **Input reports**, read with a normal HID read (interrupt IN) | LQ-usb `HidapiDevice.read` (lines 443-466); LHM `HidStream.Read` |
| Feature reports | Not used by any command in this spec | LQ never calls `send_feature_report` in `kraken3.py` |
| Output report length | **64 bytes including the Report ID byte** | LQ `_WRITE_LENGTH = 64` (line 43) and `_write` pads to 64 (lines 475-477); LHM uses `new byte[64]` (KrakenV3.cs lines 12-13); WEB-K `MAX_REPORT_LENGTH 64` |
| Input report length | **64 bytes including the Report ID byte** | LQ `_READ_LENGTH = 64` (line 42); LHM `_rawData = new byte[64]` (line 21) |
| Padding | Unused trailing bytes are `0x00` | LQ `_write` |
| Checksum / CRC / sequence number | **None** | No source computes one |
| Endianness | Multi-byte integers are **little-endian** (low byte first) | LQ lines 743, 745; LHM lines 215, 235 |

**UNVERIFIED (U1):** the exact `MaxOutputReportLength` / `MaxInputReportLength` Windows reports for this unit (expected 64 and 64). The HID report descriptor was not read (that needs a device handle). *Confirm at runtime:* `HidDevice.GetMaxOutputReportLength()` and `GetMaxInputReportLength()` must both return 64. **Design rule:** if either is not 64, log it and refuse to send any write. This is a real possibility, not a formality: OpenRGB uses 512-byte reports for the sibling PID 0x3014 (section 0.1), and LHM's success on Windows does not prove 64 because LHM writes short buffers and may rely on HidSharp padding (U20). Optionally also log `HidDevice.GetReportDescriptor()` once at runtime (HidSharp exposes a descriptor parser per its XML docs) to confirm that report IDs 0x10/0x11/0x70/0x72/0x74/0x75 are declared. The parsed descriptor has a `ReportsUseID` property ("True if the device sends Report IDs", HidSharp.XML `P:HidSharp.Reports.ReportDescriptor.ReportsUseID`); **design rule:** if it is `false`, treat it as "protocol mismatch" and do not write. (On Windows HidSharp rebuilds the descriptor from the HID parser data rather than reading the raw 360 bytes; that is from memory of HidSharp's source, which is not in the NuGet package, so do not rely on the rebuilt descriptor for anything except this yes/no check and the two lengths.)

HidSharp notes for implementers:

- Always pass a full 64-byte buffer to `HidStream.Write`, byte 0 = Report ID (= command byte). LHM passes a 2-byte array for the firmware query (KrakenV3.cs line 75), which implies HidSharp pads short writes, but that behaviour was not confirmed in HidSharp source (only the DLL and XML docs are in the NuGet package). Do not rely on it.
- `HidStream.Read` returns the report with byte 0 = Report ID. Set `ReadTimeout` explicitly (LHM uses 5000 ms, line 73; the HidSharp 2.6.4 XML docs give the default as 3000 ms, `P:HidSharp.DeviceStream.ReadTimeout`). Size the read buffer from `GetMaxInputReportLength()`, not a hard-coded 64.
- Do not use HidSharp's `OpenOption.Exclusive`. The HidSharp 2.6.4 XML docs describe it only as "HIDSharp's exclusivity layer", which lets "one process using HIDSharp" lock other processes out, with interruption requests between processes. The docs do not say whether it blocks a non-HidSharp program such as NZXT CAM (**UNVERIFIED**; it most likely does not, because it is a library-level layer and not a Windows sharing mode). Either way it gives no reliable ownership, and if it did block CAM that would break CAM. Open with the default (shared) options.
- If `GetMaxInputReportLength()` ever returns 65 and byte 0 of every report is `0x00`, the device is not using Report IDs (contradicting this section). Treat that as "protocol mismatch": log it and stay monitor-only. Do not try to shift offsets by one and keep writing. Note that 65 alone does **not** prove "no Report IDs" (per the XML docs the length includes the Report ID byte, so numbered 64-byte payloads would also give 65); either way any value other than 64 means "do not write" (U1).

---

## 2. Common response layout

Every reply observed in the references has this shape:

| Offset | Length | Meaning | Evidence |
|---|---|---|---|
| 0 | 1 | Response code (Report ID). Request code + 1 for the commands below (0x10->0x11, 0x30->0x31, 0x72->0x73, 0x74->0x75) | LQ-doc protocol file; LQ `_read_until` prefixes |
| 1 | 1 | Sub-code. Usually echoes the request's byte 1 | LQ `_read_until` keys such as `11 01`, `31 01` |
| 2..13 | 12 | Opaque, constant per device in the samples (looks like a 12-byte device identifier). **Not interpreted by any reference. Do not parse.** | LQ-test samples (lines 24-37) |
| 14.. | - | Payload, command-specific | see each command |

Replies are matched by the first two bytes. Never assume "the next report read is the answer to my last request" (see sections 6, 10, 12).
Real-device evidence for this rule (WEB-G PR #605, Elite debug log, see section 4): after `36 03` was written, the next read returned an unrequested `75 02`; the `37 03` reply only arrived on the read after the following `30 04` write. In the same log the 12 bytes at offsets 2..13 (`bb 8c 90 82 0e 58 0a 31 00 00 00 00`) are identical in the `31 01`, `75 02` and `37 03` reports, which supports "constant per device".

---

## 3. Command allow-list

These are the only reports RigControl may send. Anything not in this table is forbidden (section 11).

| Name | Bytes 0.. (rest zero-padded to 64) | Reply header | Purpose | Needed |
|---|---|---|---|---|
| SET_INTERVAL | `70 02 01 B8 01` | UNVERIFIED (probably `71 ..`; no source reads it) | Init step 1: sets the status push interval to 0.5 s | Recommended, **not proven required** (U2; section 4) |
| FINISH_INIT | `70 01` | UNVERIFIED (as above) | Init step 2 | Recommended, **not proven required** (U2; section 4) |
| GET_FIRMWARE | `10 01` | `11 01` | Firmware version | Yes |
| GET_STATUS | `74 01` | `75 01` | Liquid temp, pump/fan rpm and duty | Yes |
| SET_SPEED_PROFILE | `72 <ch> <b2> <b3> <40 duty bytes>` | `73 ..` per LQ-doc; no source reads it | Pump / fan curve or fixed duty | Yes |
| GET_LCD_INFO | `30 01` | `31 01` | Read LCD brightness + orientation | Only for LCD feature |
| SET_LCD_CONFIG | `30 02 01 <brightness> 00 00 01 <orientation>` | `31 02` per LQ-doc; no source reads it | Set LCD brightness + orientation | Only for LCD feature |

---

## 4. Initialisation

Source: LQ `KrakenZ3.initialize` (lines 684-726). LQ docstring: initialise "every time the systems boots, resumes from a suspended state, or if the device has just been (re)connected". LQ-doc guide line 44: needed "after powering on from Mechanical Off, or if there has been hardware changes".

Sequence (single thread, in this order):

| Step | Action | Detail |
|---|---|---|
| 1 | Discard queued input reports | LQ `clear_enqueued_reports` (line 696). In RigControl: start the reader loop and drop everything received before step 2 |
| 2 | Write SET_INTERVAL | `70 02 01 B8 01`. Last byte is the status-report interval code. LQ computes `1 + round((0.5 - 0.5) / 0.25) = 1` (line 709) meaning 0.5 s. WEB-K `set_interval_cmd[] = { 0x70, 0x02, 0x01, 0xB8, 1 }`, sent in `kraken3_init_device` (comment "Set the polling interval") for every kind, KRAKEN2023 included. WEB-G #128 (Kraken X63, firmware 2.1.0, Wireshark) describes this byte as "the interval on which the device will send status updates" (0x0 or 0x1 = 0.5 s, 0x0B = 3 s). So on the X63 the device pushes status reports at that interval after init; whether the Elite does is U3 |
| 3 | Write FINISH_INIT | `70 01` |
| 4 | Write GET_FIRMWARE, read until a report starting `11 01` arrives | LQ `_get_fw_version` (lines 675-682). Skip non-matching reports; give up after 12 reports (`_MAX_READ_ATTEMPTS`, line 44) |
| 5 | (Only if LCD features are enabled) write GET_LCD_INFO, read until `31 01` | LQ lines 719-720. LQ sends this unconditionally in `initialize`; making it conditional is a RigControl choice |
| 6 | Apply the cooling profile (section 7) | Design rule, not part of LQ `initialize` |

Notes:

- LQ does **not** read replies to the two `0x70` writes; it just moves on. Any ack they generate is skipped by the "read until header matches" loop.
- The lighting query `20 03` is **not** sent for this device: `_COLOR_CHANNELS_KRAKEN2023` is empty (LQ line 85, condition at line 722). Do not send it.
- LHM does **not** send either `0x70` report. It only sends `10 01`, then goes straight to `74 01` / `72 ..` (KrakenV3.cs lines 71-125). So status and speed control evidently work without the `0x70` init on at least some firmware.
- The author of the liquidctl 2023/Elite support says the same (WEB-G PR #605, brokenmass, 1 Sep 2023, verbatim): "device initialisation is actually useless. Initialisation actually only instructs the device to send its stats every x millisecond but that's totally useless and unreliable and in fact when we want to get the latest stat we send the specific command (`0x74, 0x01`) and wait for the response to that command, discarding anything else". The kernel source agrees on the meaning of the last byte (comment on `set_interval_cmd`: "Last byte sets the report interval at 0.5s") and re-sends both `0x70` reports on `reset_resume`.
- So the best available reading is: the `0x70` pair is **not required** for status or speed control on this family, and its known effect is to make the device push status reports (`75 02`) every 0.5 s (U3). **UNVERIFIED (U2):** that this holds on this unit, and that init has no other side effect (for example resetting the active speed profile). One anecdote of a side effect exists: an Elite owner (WEB-G PR #605, guygastineau, 31 Aug 2023, pre-release driver) "couldn't change orientation after initialization" and could when he never ran the initialisation command; cause unknown (see U12). *Confirm:* with CAM not running, read status, send init, read status again and compare duties. **Design rule (unchanged):** send init as LQ and the kernel driver do, always re-apply the cooling profile after it, and never treat a missing `70`/`71` acknowledgement as an error. If init ever turns out to cause trouble on this unit it can be dropped without losing status or speed control (LHM's behaviour).
- WEB-G PR #605 contains a debug log from a **Kraken 2023 Elite** (user maxawake, 14 June 2023; the log's own header says "device: NZXT Kraken 2023 Elite") in which liquidctl wrote `36 03` (`_write_then_read`, expecting `37 03`) and instead read a report that begins
  `75 02 bb 8c 90 82 0e 58 0a 31 00 00 00 00 01 1e 02 12 05 1e 1e 01 02 fc 03 32 32 00 00 ...` (the raw comment text shows all 64 bytes; bytes 27..63 are all `00`).
  Nothing in that liquidctl run had sent `74 01` or any `70 ..` (the write sequence in the log is `30 01`, `36 03`, sixteen `30 04 nn`, `32 02 ..`, `32 01 ..`), so this was a queued report liquidctl did not ask for. The `37 03` reply to `36 03` arrived one read later. The same log also contains a real Elite `31 01` LCD-info reply (TV21). Decoded with section 6.2: 30.2 degC, pump 0x0512 = 1298 rpm at 30 %, **fan 0x03FC = 1020 rpm at 50 % (offsets 23..25)**. So in this one sample the pushed `75 02` form has the same layout as `75 01`, including the fan fields. (An earlier revision of this spec wrongly said the log line was truncated after the pump bytes.)
  Counter-evidence: the Linux driver (WEB-K, comment in `kraken3_read_x53`) says that unlike the X-series, "the Z series devices ... send a sensor report only when requested", and it requests status for the 2023 kinds too (`kraken3_read_z53` is annotated "Covers Z53 and KRAKEN2023 device kinds"). Whether CAM was also running during the PR #605 capture is unknown.
  Further evidence for pushing: the PR author's statement quoted above (init "instructs the device to send its stats every x millisecond"), and the kernel's own constant comment `STATUS_VALIDITY 2000 /* In ms, equivalent to period of four status reports */` (i.e. a 0.5 s report period). The capture is consistent with a device that had been initialised earlier (by liquidctl or CAM) and was pushing every 0.5 s: exactly one unrequested `75 02` appears among the 23 reads of one short CLI run (the log has no timestamps, so the rate cannot be derived from it).
  Conclusion: the Elite **probably** pushes `75 02` reports every 0.5 s once SET_INTERVAL has been sent (PR author's statement plus one capture; the kernel comment about Z-series disagrees); **UNVERIFIED (U3)** whether this unit does. The reader must tolerate them either way and must never depend on them.

When to run the whole sequence: app start, device arrival (hot-plug / re-enumeration), resume from sleep or hibernate, and after any I/O error that forced a reopen.

---

## 5. Firmware version query

| Direction | Offset | Value |
|---|---|---|
| Request | 0 | `0x10` |
| Request | 1 | `0x01` |
| Request | 2..63 | `0x00` |
| Reply | 0 | `0x11` |
| Reply | 1 | `0x01` |
| Reply | 17 (0x11) | Major |
| Reply | 18 (0x12) | Minor |
| Reply | 19 (0x13) | Patch |

Display as decimal `major.minor.patch`.

Evidence: LQ `parse_firm_info` (lines 255-256); LHM lines 83-84; ORGB `SendFirmwareRequest` line 929; WEB-K `FIRMWARE_VERSION_OFFSET 17`. Four independent sources agree.
The LQ-doc protocol file says "Byte 3/4/5"; that disagrees with all four code bases and is wrong or uses a different base. Use 17/18/19.

Firmware facts worth knowing:

- LHM labels any 300C firmware other than `1.2.4` as "untested" (KrakenV3.cs lines 42, 133). That is a UI warning only; LHM still drives the device.
- The development PC's USB descriptor revision is `REV_0201`. WEB-G issue #627 shows an older 300C unit with bcdDevice 1.02. **UNVERIFIED (U4):** that bcdDevice tracks firmware (which would make this unit firmware 2.0.x). *Confirm:* read the real version with GET_FIRMWARE.
- Elite (PID 0x300C) firmware versions reported in the wild (WEB-G #631): `2.0.0` (maxawake, 5 Apr 2024, "Kraken 2023 Elite 360") and `2.1.0` (vossiman, 5 May 2025). The second is the printed output of `liquidctl initialize` on a "NZXT Kraken 2023 Elite (broken)": `Firmware version 2.1.0`, `LCD Brightness 80 %`, `LCD Orientation 270 °`. liquidctl's `initialize` (at the cloned revision) sends exactly TV1, TV2, TV3 and TV11 (which liquidctl version that user ran is not stated; on Linux the kernel driver also sends TV1/TV2 at probe), so this is real-device evidence that on Elite firmware 2.1.0 the init pair is accepted, the `11 01` reply carries the version at offsets 17..19 and the `31 01` reply carries sane values at offsets 24 and 26. So firmware 2.x exists for this PID and LHM's "1.2.4" is simply old.
- The cooling commands are reported unaffected by the 1.x -> 2.0.1 update on the *standard* Kraken 2023 (WEB-G PR #605 / #631, sossw, 3 Oct 2023: image upload broke, "All other functions of liquidctl seem to be unaffected"). No equivalent statement exists for the Elite's cooling commands on 2.x.
- No reference changes the **cooling** commands by firmware version. Only LCD image upload is version-dependent (LQ `_is_2023_fw_version2`, and that check is for PID 0x300E only).

---

## 6. Status

### 6.1 Request / receive

- Status **must be requested**: write `74 01`, then read. LQ `KrakenZ3._get_status_directly` (lines 734-747); LHM `Update` (lines 206-212); WEB-K `z53_get_status_cmd {0x74, 0x01}` used for both the Z53 and the 2023 kinds.
- LQ drains queued reports, writes, and takes the very next report without checking its header. **Do not copy that.** LHM loops until the report starts `75 01` (lines 208-212). The kernel driver accepts any report with ID `0x75`.
- RigControl rule: after writing `74 01`, read until a report with byte 0 = `0x75` and length >= 26 arrives. Prefer `75 01`. Also accept `75 02` (the pushed form; liquidctl's X3 samples use `75 02` with the identical field layout at offsets 15..19, and the one Elite `75 02` capture in WEB-G PR #605, section 4 and TV19, has plausible fan values at 23..25). **UNVERIFIED (U3):** that this unit's `75 02` reports, if any, always use that layout. *Confirm:* capture one `75 02` report and compare with a `75 01` taken at the same moment. Until confirmed, use `75 02` reports for display only if no `75 01` arrives, and never use them for write verification (7.6).
- Timeout: WEB-K waits 2000 ms for a status reply. LQ and LHM use 5000 ms read timeouts. Recommended: 2000 ms per request, at most 12 reports inspected per request.

### 6.2 Parse (the reply is 64 bytes; offsets include the Report ID at 0)

| Offset | Length | Field | Decode | Unit |
|---|---|---|---|---|
| 0 | 1 | `0x75` | Report ID | - |
| 1 | 1 | `0x01` (reply to request) or `0x02` (pushed) | see 6.1 | - |
| 2..13 | 12 | Opaque | ignore | - |
| 14 | 1 | Unknown (`0x01` in all three liquidctl samples) | ignore | - |
| 15 | 1 | Liquid temperature, integer part | `t = b[15] + b[16] / 10.0` | degC |
| 16 | 1 | Liquid temperature, tenths (0..9) | | |
| 17..18 | 2 | Pump speed | `b[17] | (b[18] << 8)` (LE) | rpm |
| 19 | 1 | Pump duty | raw value 0..100 | % |
| 20..22 | 3 | Unknown (byte 20 equals byte 19 in all samples; possibly the commanded duty. Bytes 21-22 are `01 02` in the Z3 sample and in the Elite sample TV19, `00 00` in the X3 samples) | ignore | - |
| 23..24 | 2 | Fan speed | `b[23] | (b[24] << 8)` (LE) | rpm |
| 25 | 1 | Fan duty | raw value 0..100 | % |
| 26..63 | - | Unknown / zero. In the one Elite sample (TV19) byte 26 equals byte 25 (`32 32`), mirroring the pump pair at 19/20; in the Z3 sample both are 0 | ignore | - |

Evidence: LQ lines 741-747; LQ-doc protocol file ("Get cooling info"); LHM lines 214-238; WEB-K offsets 15, 16, 17, 19, 23, 25. All agree exactly.

Fault handling:

- If `b[15] == 0xFF && b[16] == 0xFF` the temperature is invalid. LQ logs "unexpected temperature reading, possible firmware fault" (lines 738-740). The kernel documentation adds that this also happens when USB is connected but the pump's SATA power is not. Treat as **sensor fault**: show an alarm, do not display 280.5 degC, and do not lower any speed on the basis of that reading.
- "Fan" is a single channel: the fan header on the pump/breakout cable. All radiator fans on it report one rpm and share one duty.
- WEB-G issue #627 shows a 300C status with fan duty 30 % and fan speed 0 rpm, so a zero fan rpm reading is not by itself proof of a stopped fan (fans may not be wired to the Kraken's header). Pump rpm 0 with the PC on is always an alarm.

Polling rate: design rule - one GET_STATUS per second at most. The kernel driver caches for 2 s; LHM polls once per `Update()`.

---

## 7. Setting pump and fan speed

There is one command. A "fixed duty" is just a flat curve. There is no separate fixed-speed opcode.

### 7.1 Packet layout (SET_SPEED_PROFILE)

| Offset | Length | Pump | Fan | Meaning |
|---|---|---|---|---|
| 0 | 1 | `0x72` | `0x72` | Report ID / command |
| 1 | 1 | `0x01` | `0x02` | Channel |
| 2 | 1 | `0x01` | `0x01` | Constant for the 2023 family (it is `0x00` on the older X3/Z3) |
| 3 | 1 | `0x00` | `0x01` | Constant for the 2023 family |
| 4..43 | 40 | duty[0..39] | duty[0..39] | One byte per point, **raw percent 0..100** (not 0..255) |
| 44..63 | 20 | `0x00` | `0x00` | Padding |

Channel id evidence (three code bases agree; note that all three trace back to the same author's reverse engineering, WEB-G PR #605, so they are not three independent captures):

- LQ `_SPEED_CHANNELS_KRAKEN2023` (lines 69-72): pump `[0x1, 0x1, 0x0]`, fan `[0x2, 0x1, 0x1]`, and the 0x300C match entry uses it (lines 573-584).
- LHM `case 0x300C` (KrakenV3.cs lines 39-45): pump `72 01 01 00`, fan `72 02 01 01`.
- WEB-K `kraken3_write_curve`: header `{ 0x72, 0x00, 0x00, 0x00 }`, byte 1 = channel + 1 (pump 1, fan 2); for the KRAKEN2023 kind (which is what PID 0x300C maps to in `kraken3_probe`) byte 2 = 1, and byte 3 = 1 only for the fan (comment "These require 1s in the next one or two slots after SET_DUTY_ID_OFFSET").
- WEB-G PR #605 (brokenmass, PR description, 29 May 2023): "byte 3 and 4" (1-based, i.e. offsets 2-3) must be `0x1 0x1` for the fan and `0x1 0x0` for the pump, adding that `0x0 0x0` also works for the pump. Use `01 00` for the pump regardless.
- Real-Elite confirmation (WEB-G PR #605, guygastineau, 1 Sep 2023, "Kraken 2023 Elite AIO", running that PR's branch): "I can confirm that fan and pump speed curves are working", and a day earlier "the pump and fan settings didn't change" while the LCD misbehaved. That is one user's report on 2023 firmware, not a capture.

Padding difference: LHM fills offsets 4..63 (all 60 bytes) with the duty (`FillTargetArray`, lines 135-139). LQ and the kernel send 40 duty bytes and zero the rest (WEB-K `SET_CURVE_DUTY_CMD_LENGTH 44`). **Use the LQ/kernel form (zeros at 44..63).**

Reply: LQ-doc lists a reply with header `0x73`. No implementation reads it. Do not block on it; let the reader loop discard it.

### 7.2 What the curve is keyed to

- The 40 points correspond to **liquid (coolant) temperature 20 degC .. 59 degC in 1 degC steps**, as measured by the cooler's own sensor (the same value as status offsets 15-16). Point `i` (packet offset `4 + i`) applies at `20 + i` degC.
  Evidence: LQ `stdtemps = range(20, 59 + 1)` (lines 380-382) and its log text "duty for liquid temperature >= N degC"; WEB-K documentation: 40 points, "fixed [20-59] range", coolant temperature.
- The LQ-doc protocol file says "Byte 5-44" and "temperature 20 to 60 degree". "Byte 5-44" is **1-based** numbering and is the same as offsets 4..43 (the file numbers request bytes from 1, e.g. "Byte 2-4: Channel" = offsets 1..3, and "Byte 3/4/7/8" in "Set lcd brightness/orientation" = offsets 2/3/6/7). Its response numbering is **not consistent**: "Byte 15" temperature = offset 15 (0-based), "Byte 18" LCD brightness = offset 0x18 (hex), and the bucket-info "Byte 18-19" / "Byte 20-21" = code offsets 17-18 / 19-20 (1-based; LQ lines 1082-1083). Never take an offset from that file alone. Only "20 to 60" is a real disagreement about the curve; code and kernel say 20..59 (40 points). Follow the code.
- LQ guide line 81: "the liquid temperature should never reach 60°C, as at that point the pump and tubes might fail or quickly degrade", and liquidctl forces the pump to 100 % "for liquid temperatures of 60°C and above". In code this is the 59 degC point (offset 43) = 100, applied to the fan channel as well (the same `normalize_profile` call). RigControl should alarm well before 60 degC liquid (design rule: warn at 50, critical at 55).
- It is **not** keyed to CPU or GPU temperature. The device knows nothing about those. Any CPU/GPU-based behaviour has to be computed by the host and pushed as a new curve.
- **UNVERIFIED (U5):** what the firmware does below 20 degC and at or above 60 degC (assumed: first and last point respectively), and whether it steps or interpolates between points. *Confirm:* not safely testable at the high end; rely on the "last point = 100" rule below.

### 7.3 Allowed duty ranges

| Channel | Min | Max | Evidence |
|---|---|---|---|
| Pump | **20** | 100 | LQ `_SPEED_CHANNELS_KRAKEN2023` (line 70) and guide table (lines 93-96); WEB-K `PUMP_DUTY_MIN 20`; LHM `new Control(_pump, settings, 20, 100)` (line 96) |
| Fan | 0 | 100 | LQ line 71. LHM limits its own fan control to 20..100 (line 111) |

- LQ carries the comment "TODO adjust min duty value to what the firmware enforces" (lines 56, 63). So 20 is the driver authors' floor, not a documented firmware limit. **UNVERIFIED (U6):** the firmware's real minimum pump duty and what it does with a lower value. *Do not test this.* Never send a pump byte below 20.
- Never send a duty byte above 100 (0x64) on either channel.
- **UNVERIFIED (U7):** whether fan duty 0 actually stops the fans on this unit. *Confirm:* with CAM closed, send a flat fan curve of 0 with the last point 100 and watch fan rpm.

### 7.4 Building the 40 bytes (reference algorithm)

Input: a list of `(temperatureC, dutyPercent)` integer pairs, and the channel's `[min, max]`.
Source: LQ `set_speed_profile` (lines 375-411), LQ-util `normalize_profile` (lines 185-218), `interpolate_profile` (lines 221-246), `clamp` (lines 132-137).

1. Append the failsafe point `(59, 100)` to the list (`_CRITICAL_TEMPERATURE = 59`, LQ line 74).
2. Sort by temperature ascending; for equal temperatures put the higher duty first.
3. Make it non-decreasing: walk the sorted list keeping the first point; skip a point whose temperature equals the previous sorted point's; raise a point's duty to the previous point's duty if it is lower; **stop after the first point whose duty is 100** (later points are dropped).
4. For each `t` in 20..59: take `lower` = last kept point with temperature <= t (or the first point), `upper` = first kept point with temperature >= t (or the last point). If they are the same temperature use that duty, else linearly interpolate and **round half to even** (Python `round`; C# `Math.Round(double)` default is the same).
   To get bit-identical results, evaluate in `double` in exactly this order: `lower.duty + (t - lower.temp) / (double)(upper.temp - lower.temp) * (upper.duty - lower.duty)` (divide first, then multiply; LQ-util line 246). A different order can move a value that should be exactly x.5 off the midpoint.
5. Clamp each of the 40 values to the channel's `[min, max]`.

Consequences that implementers must reproduce:

- The value for 59 degC (offset 43) is **always 100** when built this way, for both channels, including "fixed" duties.
- A fixed duty `d` is built from the profile `[(0, d), (58, d)]` (LQ `_set_fixed_speed_directly`, lines 413-414). Result: offsets 4..42 = clamp(d) (39 bytes), offset 43 = 100.
- Clamping happens after interpolation, so a requested pump duty of 10 is sent as 20.
- Rounding half to even is visible in the reference test vector: 57.5 -> 58 and 72.5 -> 72.

Reference quirk: in step 3 liquidctl compares each point with the previous point's *original* duty, not the already-raised one, so an input like `(30,40),(35,30),(40,35)` yields a non-monotonic result. **Design rule:** RigControl uses a running maximum instead. This gives identical output for every input that is already non-decreasing, including all test vectors in section 13.

### 7.5 Is the profile stored and run by the device? (safety-critical)

| Question | Answer | Confidence / evidence |
|---|---|---|
| Does the device firmware execute the curve by itself, without the host? | **Yes.** The host sends 40 points once; the device applies them against its own liquid sensor | LQ is a one-shot CLI that sends the profile and exits; LQ guide line 8: configuration "persists as long as the device still gets power, even if the system has gone to Soft Off (S5) state"; WEB-K documentation describes curve mode as applied from coolant temperature, and the kernel source (`kraken3_write_fixed_duty`) states "The devices can only control the duty through a curve", sending even a fixed duty as a 40-point curve with the last point forced to 100. Written for the generation-4 family that the 2023/Elite driver class extends. Not separately demonstrated for PID 0x300C: **UNVERIFIED (U8)** for this exact unit, low risk |
| Does cooling continue if RigControl exits or crashes? | **Yes, with the last profile that was sent**, for as long as the cooler has power (U8) | same |
| Is the profile kept in non-volatile storage? | **No; assume it is lost on any real power loss.** "Persists as long as the device still gets power" and "initialise after Mechanical Off" both imply volatile storage. The PR #605 author states it directly for the 2023 family: "the pump will lose all information if it loses power for more than a couple secons (either your usb is not always-on or your desktop lose power completly)" (brokenmass, 1 Sep 2023). For the older X63, WEB-G #128 reports that removing the power cord "makes the device to fully reset it's colors, pump speed, etc". Consequence: after the PSU is switched off, after a power cut, and possibly after S4/S5 when USB standby power is off, the cooler is back at its power-up default (U9) until software talks to it again | LQ guide lines 8, 44; WEB-G PR #605; WEB-G #128 |
| Can the stored curve or mode be read back? | **No command for it is known.** No reference reads a curve back; status only reports the momentary duty. After start-up RigControl therefore cannot know what curve is in the device (API.md `mode: "unknown"`) until it has written one itself | absence in LQ, LHM, WEB-K, LQ-doc |
| What does the device do at power-up before any software talks to it? | **UNVERIFIED (U9).** No reference documents it. One web comment (WEB-G PR #605, brokenmass, 1 Sep 2023) says the default is "silent mode (pump at 30% fixed speed)". Neither that reply nor the question it answers names a model (the asker said elsewhere in the thread that he owns a "2023 Kraken 240 black", i.e. a standard model); fan behaviour is not stated. A maintainer added "They do have a default state:" with a screenshot that could not be read here. Weak supporting data point: WEB-G issue #627 shows a liquidctl status from a 300C unit reading pump duty 30 % and fan duty 30 % (whether CAM had run before is unknown) | *Confirm:* cold boot with CAM autostart disabled (user action), read status once per second for a minute before sending anything except GET_STATUS, and record pump duty, fan duty and whether they move with liquid temperature |
| What is left behind when another program (CAM) exits? | Whatever curve it sent last. LQ-doc protocol file notes the curve "is almost always set as a flat curve with all identical values" in observed traffic, so CAM appears to run its curves in software and push flat duties. A flat duty left behind does **not** react to temperature | Inference from LQ-doc; **UNVERIFIED (U10)** |

Design rules that follow (these are the reason the app can be trusted to replace CAM):

1. **Every packet RigControl sends must be safe to leave in the device forever.** Never send a flat low duty as a "software control" step. If RigControl computes a duty `d` from CPU/GPU temperature, send `max(d, baseline[i])` for each point, where `baseline` is a liquid-temperature failsafe curve that rises to 100. Then a crash leaves a curve that still protects the loop.
2. Offset 43 (59 degC) is always 100 on both channels. The curve is non-decreasing.
3. Pump bytes are never below 20. Recommended app default when nothing is configured: pump 60, fan 40 (the values LHM calls "a safe setting", KrakenV3.cs lines 157-158, 184-185; the PR #605 author also calls a fixed 60 % pump "a good default"), shaped per rule 1.
4. Re-apply the profile after: app start, init, device re-arrival, resume from sleep/hibernate, and when another controller releases the device (section 12).
5. On normal exit send **nothing**. Leave the current curve in place. There is no "release" or "return to default" command in any reference.
6. Start early at logon/boot so the unknown power-up default (U9) is replaced quickly.

### 7.6 Verifying a write

- LHM contains the comment "This pump sometimes sets itself to 0% when instructed to a value" and re-sends the packet on each update until the reported duty equals the commanded one (KrakenV3.cs lines 217-250).
- Design rule: after each SET_SPEED_PROFILE, compare the next `75 01` status reply's duty with the expected value `curve[clamp(floor(liquidTemp), 20, 59) - 20]`. How the firmware maps a temperature to a point (step at `floor`, round, or interpolate) is **UNVERIFIED (U5)**; liquidctl's log text "duty to N% for liquid temperature >= T" only suggests a step. So accept any reported duty between the minimum and the maximum of the three points for `floor(t) - 1`, `floor(t)` and `floor(t) + 1` (clamped to 20..59). For a flat curve below 59 degC this is an exact match, which is the only case LHM's loop exercises. If it does not match, re-send at most 3 times, at least 1 s apart, then raise an alarm. If the pump duty reads 0 or the pump rpm reads 0, alarm immediately.
- **UNVERIFIED (U22):** that status bytes 19 and 25 report the duty the firmware is currently applying from the stored curve (as opposed to, say, the last flat value or a measured value). LHM's resend-until-equal loop only makes sense if they do, and the samples (30 %, 50 %, 20 %) look like commanded values, but no reference states it. *Confirm:* write a two-level curve whose step is 2 degC above the current liquid temperature and watch the reported duty as the liquid warms.
- Do **not** run this re-send loop while another controller is active (section 12), or the two will fight.

---

## 8. Ordering and timing constraints

| Constraint | Value | Basis |
|---|---|---|
| I/O concurrency | One owner. Serialise all writes; one reader loop; at most one status request outstanding | WEB-K serialises writes with a buffer mutex (`kraken3_write_expanded`, `buffer_lock`) and serialises status requests with a second mutex (`z53_status_request_lock` in `kraken3_read_z53`, which also skips the request when the cached data is younger than `STATUS_VALIDITY`) |
| Curve write rate | Send only when the 40 bytes change, and no more than one SET_SPEED_PROFILE per channel per second | WEB-K documentation (`nzxt-kraken3.rst`, not the .c file): "If in curve mode, setting point values should be done in moderation"; "The devices require complete curves to be sent for each change; they can lock up or discard the changes if they are too numerous at once". The 1 s figure is a design rule; the true safe rate is **UNVERIFIED (U11)** |
| Gap between pump and fan writes | >= 200 ms (design rule only, no source requires it) | Correction: LQ's `time.sleep(0.2)` (line 370) is in `_set_speed_profile_hwmon`, the Linux kernel-driver path, between per-point sysfs writes and enabling curve mode. In the direct (Windows) path liquidctl, and its tests, send pump then fan back to back with no delay. 200 ms is a conservative choice, not a protocol fact |
| Status poll | <= 1 Hz | design rule; kernel caches 2 s |
| Reply wait | 2000 ms, max 12 reports inspected | WEB-K `STATUS_VALIDITY 2000`; LQ `_MAX_READ_ATTEMPTS = 12` |
| Stale input | Before a request, drop already-queued reports or (better) match replies by header in a reader loop | LQ `clear_enqueued_reports` (usb.py lines 424-441) |
| Init before control | Init, then firmware query, then profile | LQ `initialize` |
| After resume / re-plug | Full init + re-apply | LQ `KrakenZ3.initialize` docstring lines 687-690 (lines 221-224 are the identical `KrakenX3` copy); WEB-K `kraken3_reset_resume` re-runs `kraken3_init_device` (the two `0x70` reports). The kernel does not re-send curves on resume; RigControl must (the profile may have been lost, 7.5) |
| LCD config | Read-modify-write: GET_LCD_INFO must precede SET_LCD_CONFIG | LQ lines 802-828 |

Recommended reader design: a single background loop reads input reports and dispatches by `(byte0, byte1)`: `11 01` firmware, `31 01` LCD info, `75 xx` status, everything else logged at debug level and dropped. Requests complete when their header arrives or time out. Unknown headers are never an error.

---

## 9. LCD: brightness, orientation, and the non-HID interface

### 9.1 Read LCD info

| Direction | Offset | Value |
|---|---|---|
| Request | 0, 1 | `30 01` |
| Reply | 0, 1 | `31 01` |
| Reply | 24 (0x18) | Brightness, 0..100 % |
| Reply | 26 (0x1A) | Orientation index 0..3 (0 = 0 deg, 1 = 90, 2 = 180, 3 = 270) |

Evidence: LQ `parse_lcd_info` (lines 728-732) and line 656 comment; LQ-test `MockKraken` (lines 130-133). The LQ-doc protocol file says "Byte 18" for brightness; that is the hex offset 0x18 = 24 written without the prefix. Use 24 and 26.
Real Elite data point (WEB-G PR #605, TV21): a Kraken 2023 Elite answered `30 01` with `31 01`, offset 24 = `0x64` (100 %) and offset 26 = `0x03` (270 deg), both in range. Offsets 20-21 and 22-23 read `80 02` / `80 02` = 640 / 640 (LE), which matches the Elite's 640x640 LCD; that is an inference, not documented anywhere, so do not parse them.

### 9.2 Set brightness and orientation (one command sets both)

| Offset | Value | Meaning |
|---|---|---|
| 0 | `0x30` | Command |
| 1 | `0x02` | Sub-command |
| 2 | `0x01` | Constant |
| 3 | brightness | 0..100 |
| 4 | `0x00` | Constant |
| 5 | `0x00` | Constant |
| 6 | `0x01` | Constant |
| 7 | orientation index | 0..3 |
| 8..63 | `0x00` | Padding |

Evidence: LQ `set_screen` lines 817-828; LQ-test `test_krakenz3_response.py` lines 4-5; LQ-doc protocol file ("Set lcd brightness/orientation").

Rules:

- Because both values travel together, always GET_LCD_INFO first and re-send the value you are not changing (this is what LQ does).
- Validate brightness 0..100 and orientation in {0, 90, 180, 270} before building the packet. Orientation byte = degrees / 90.
- Reply `31 02` per LQ-doc; no implementation reads it.
- **UNVERIFIED (U12):** that these two LCD commands behave the same on the Elite with firmware 2.x. The test fixtures are for the older Z3. GET_LCD_INFO is shown working on one Elite in June 2023 (TV21, firmware unknown) and on one Elite with firmware 2.1.0 in May 2025 (section 5: brightness 80 %, orientation 270 deg). SET_LCD_CONFIG has no Elite capture, only two user reports from August 2023 on the pre-release driver (WEB-G PR #605, guygastineau, Kraken 2023 Elite): brightness changes worked "reliably"; orientation changes failed after `liquidctl initialize` had been run and worked when it had not. *Confirm:* GET_LCD_INFO (read-only) should return plausible values (brightness 0..100, orientation 0..3); if not, disable the LCD feature.
- **Design rule:** after every SET_LCD_CONFIG, send GET_LCD_INFO again and compare. If the value did not change, report that to the UI as a failed operation; do not retry in a loop and do not send any other LCD command to "fix" it.
- **UNVERIFIED (U13):** whether brightness/orientation survive a power cycle (one user in WEB-G PR #605 reported orientation did).
- While CAM is running it owns the display. Do not send SET_LCD_CONFIG then.

### 9.3 The non-HID interface (MI_00) - out of scope

- Interface 0 is a vendor-specific interface bound to WinUSB (PNP: service `WINUSB`, name "NZXT Kraken Elite"). It has one bulk OUT endpoint, `0x02`. It carries **image / GIF data for the 640x640 LCD** (LQ lines 581-582, 773-777, 945-1037).
- An upload is not trivial. It needs HID commands `36 03`, sixteen `30 04` bucket queries, `32 02` delete, `32 01` create with memory offsets and sizes in kilobytes, `36 01` start, a bulk header (`12 FA 01 E8 AB CD EF 98 76 54 32 10` + type + length), the pixel data in chunks, `36 02` end and `38 01 04 <bucket>` to switch. The format depends on firmware version, liquidctl marks GIFs unsupported on firmware 2.x for the 2023 model, and liquidctl tags this exact PID "NZXT Kraken 2023 Elite (broken)" (line 576) without saying why (**UNVERIFIED (U14)**; the README legend only defines B as "broken in at least one significant way"). In WEB-G #627 (closed 22 July 2024) a user running liquidctl from git reported status reads on a 300C and said setting GIFs and static images worked; no maintainer explains the label. Everything that is reported as broken for the Elite concerns the LCD, never cooling: liquidctl's README lists "NZXT Kraken 2023 Standard, Elite" with note `p` ("Only partially supported"), the changelog entries for this driver are about setting the screen with firmware v2.x (issue #631), and in #631 an Elite owner on firmware 2.1.0 writes (5 May 2025) "still shows up as broken, although it seems most functionality is restored. GIF does not work, however setting an image does - altho it reverts back to default screen every X seconds".
- Real-Elite reports of what LCD writes can do (WEB-G PR #605, guygastineau, 31 Aug and 1 Sep 2023): after an image upload "the lcd went black, and trying to set it back to liquid did nothing", it stayed blank across a reboot and only recovered after a full power-off; later "I cannot revert to liquid mode ... I have to poweroff my computer". This is the practical reason the upload path and `38 01` are on the NEVER SEND list.
- For PID 0x300C liquidctl always uses the older upload path (`_send_data`, `36 01 <bucket>`); its firmware-2 path (`_send_2023_data_fw2`, `36 01 00 01 06`) is selected only for PID 0x300E (`_is_2023_fw_version2`, lines 808-813).
- **Decision: LCD image upload, GIFs, bucket management and LCD mode switching are out of scope.** RigControl must not open the MI_00 interface at all. WinUSB interfaces accept a single opener (general Windows behaviour, not from the references), so opening it could also lock CAM out of the display.

---

## 10. Things the reference implementations do that RigControl must not copy

| Reference behaviour | Why not |
|---|---|
| LQ takes the first report after `74 01` as the status without checking the header | A pushed `75 02`, a `73` ack or a reply to another program's request may arrive first |
| LHM fills bytes 44..63 with duty | Not what LQ or the kernel send |
| LHM sends truly flat curves (no 100 at 59 degC) | Leaves no thermal failsafe in the device |
| LHM resends until the duty matches, forever | Fights with any other controller |
| LQ `normalize_profile` monotonic quirk | See 7.4 |
| LQ `assert` on unexpected replies | Unknown reports must be ignored, not fatal |

---

## 11. NEVER SEND list

RigControl uses a strict allow-list (section 3). Everything else is forbidden. Specifically:

| Never send | Reason |
|---|---|
| Any Report ID / command byte not in section 3 | **No reference documents the firmware-update or bootloader commands for this device, so they cannot be named and excluded individually.** The only safe policy is default-deny. Never probe, scan or fuzz command bytes. NZXT CAM performs firmware updates (WEB-G #631); the opcodes it uses are unknown |
| `36 xx` (start / end / cancel data transfer) | LCD storage write path |
| `32 01`, `32 02` (create / delete memory bucket) | Destructive: deletes images stored on the device |
| `30 04` (bucket query) | Read-only, but only useful for uploads; not needed |
| `38 01 ..` (switch LCD mode / bucket) | Changes what the display shows and overrides the user's CAM screen setup. Out of scope. (Known form, for reference only: `38 01 02 00` = show liquid temperature, LQ line 857 and test fixture line 3) |
| Anything on the bulk endpoint / the WinUSB interface MI_00 | LCD data path; do not even open it |
| `20 xx`, `22 xx`, `26 xx`, `28 xx`, `2A xx` (lighting info / effects / direct LED) | Lighting is not part of cooling. liquidctl says ring control on the Elite "is not yet supported" (guide line 29) and defines no colour channels for 2023 models. OpenRGB's variants are for other PIDs |
| `62 01 ..` (OpenRGB `SendFan`) | Fan command of NZXT Smart Device / RGB & Fan Controller hardware, **not** a Kraken command |
| `72 ..` with byte 1 other than `01` or `02`, or with bytes 2-3 other than `01 00` (pump) / `01 01` (fan) | Only these channel ids are documented for the 2023 family |
| `72 01 ..` with any duty byte below 20 (0x14) | Pump minimum |
| `72 ..` with any duty byte above 100 (0x64) | Out of range |
| `72 ..` whose last point (offset 43) is below 100, or whose points decrease | Design rule: no failsafe |
| `70 02 01 B8 <n>` with `<n>` other than `01` | Other interval codes are untested here (WEB-G #128 describes them for the Kraken X63 only, firmware 2.1.0) |
| Any other `70 ..` report (anything except exactly TV1 and TV2) | Init/config family; only these two forms are documented |
| `10`, `30`, `74` with a byte 1 other than the allow-listed values (`10 01`, `30 01`, `30 02`, `74 01`), or with non-zero bytes after the documented ones | Unknown sub-commands. `30 04` is a known read but out of scope; other `30 xx` and `10 xx` values are undocumented |
| `30 02 ..` with brightness > 100 or orientation > 3 | Out of range |
| Feature reports, SetOutputReport / control-pipe writes, writes whose length is not exactly 64 | Not part of the protocol |
| Bursts of `72` writes | Kernel documentation: the device can lock up or discard changes |
| Any write at all while not sure the opened device is VID 1E71 / PID 300C / usage page FF00 | The other NZXT devices on the development PC share the usage page and would misinterpret these packets |

---

## 12. Coexistence with a running NZXT CAM

### 12.1 What is on the development PC now (PNP, 2026-11-15)

| Item | Value |
|---|---|
| Installed | NZXT CAM 4.76.9, `C:\Program Files\NZXT CAM\` |
| Processes | `NZXT CAM` (5 instances), `cam_helper` (2 instances) |
| Service | `CAMService`, start mode Auto, Running, image `...\NZXT CAM\resources\app.asar.unpacked\node_modules\@nzxt\cam-core\dist\service.exe` |
| Not NZXT | A service named `camsvc` (`svchost.exe -k osprivacy`) is a Windows component. **Do not treat it as CAM.** |

### 12.2 What the references say

- LQ guide line 10: "Monitoring and/or configuring the coolers is not possible with CAM running, otherwise you'll get errors such as `OSError('read error')`."
- LQ-doc protocol file: the speed curve is "almost always set as a flat curve" in observed traffic, i.e. CAM re-sends flat duties as its own software curve moves (inference, U10).
- WEB-G PR #605 (brokenmass, about the **standard** Kraken 2023): the screen "is constantly rendered and sent by cam every x ms". It is likely, but **UNVERIFIED**, that CAM does the same for the Elite, i.e. talks to the device constantly while it runs.

### 12.3 Expected behaviour if both talk to the device

Not tested. Derived from the protocol and from general Windows HID behaviour (each open handle gets its own copy of every input report; writes from different processes interleave). **UNVERIFIED (U15).**

| Effect | Consequence |
|---|---|
| Opening the HID path may fail (sharing violation / access denied) if CAM opened it exclusively | Must be handled as a normal condition, not an error |
| Replies to CAM's requests appear in RigControl's input queue, and the reverse | Match replies by header; never by order. RigControl's extra `74 01` polls put extra `75 01` reports in CAM's queue (probably harmless; unverified) |
| Both write `72` curves | Last writer wins. CAM will overwrite RigControl's curve whenever CAM updates its own; RigControl's verify-and-resend would then overwrite CAM. The result is oscillating speeds |
| Both write `30 02` / LCD commands | CAM's display and RigControl's settings fight |
| Init (`70 ..`) sent under CAM | Unknown side effects (U2) |

### 12.4 Required policy

RigControl must never stop, kill, suspend or reconfigure CAM or its service **on its own initiative**, and never during development or tests. The user decides whether CAM runs. The UI contract (`docs/API.md`) defines one explicit, user-triggered method, `cooling.closeCam` ("closes the NZXT CAM app the user is running, so RigControl can take over"); how that method closes the app is outside this protocol spec. It does not cover the `CAMService` Windows service, which the non-elevated user session cannot stop (see the open point below).

Contract mapping: API.md says of `CoolingStatus.blockedBy` "while non-empty RigControl does not talk to the devices". So in the "CAM active" state `blockedBy` is non-empty (for example `["NZXT CAM"]`) and RigControl sends **nothing** to the Kraken, not even `74 01`.

| State | Detection (read-only) | RigControl behaviour for the Kraken |
|---|---|---|
| **CAM active** | A process named `NZXT CAM` or `cam_helper` exists, or service `CAMService` is Running | **Monitor-only, zero writes.** Do not send anything (no `70`, `72`, `30`, `10`, and no `74 01`: an earlier revision allowed an optional `74 01` poll here, which contradicts API.md's `blockedBy` rule and is withdrawn). If the lead wants live values while CAM runs, the only option compatible with the contract is to open the HID device read-only in the sense of "never write" and parse any `75` report that happens to arrive (CAM's own polling and pushed reports); whether opening at all counts as "talking to the device" is a contract question for the lead (section 20.4). If the device cannot be opened, show "in use by NZXT CAM" and retry with back-off (for example every 10 s). Never force access |
| **CAM not active** | None of the above | Full control: init, firmware query, profile, 1 Hz status, verification |
| **CAM starts while RigControl controls** | Process/service check once per poll cycle | Stop writing immediately, switch to monitor-only, tell the user |
| **CAM stops** | same | Wait ~2 s, then run full init and apply RigControl's profile. This matters: the curve CAM leaves behind is probably flat (U10) |

Extra safeguard (heuristic, **UNVERIFIED (U16)**): in control mode, count `73` acks and `75 01` replies that RigControl did not request. A steady stream of them means some other program is driving the device; drop to monitor-only and tell the user.

Open point for the user: `CAMService` is set to start automatically. **UNVERIFIED (U17):** whether the service alone (CAM window closed) keeps talking to the Kraken. *Confirm (passive, safe):* with the CAM window closed and the service running, open the HID device, send nothing, and log incoming report headers for 30 s. If `75 01` or `73` reports arrive, the service is polling/controlling and RigControl cannot take over until the user disables CAM's autostart and service. Until that is known, treat a running `CAMService` as "CAM active".

Consequence that the lead must decide on (section 20.4): with that conservative rule, `cooling.closeCam` alone can never unblock RigControl on the development PC, because `CAMService` is set to Auto, runs elevated and survives closing the app. A possible runtime resolution, **not adopted here because it is untested**: when only the service is running (no `NZXT CAM` / `cam_helper` process), open the device, send nothing for about 5 s and inspect the incoming headers. Reports with header `75 01`, `73 xx`, `31 xx` or `11 01` are replies to somebody else's requests (sub-code `01` = reply; pushed status is `75 02`), so their presence means another controller is active and RigControl stays blocked; their absence would allow taking control. This depends on U3 and U16.

### 12.5 LibreHardwareMonitor inside RigControl is also "another program"

- `LibreHardwareMonitorLib` creates `NzxtGroup` when `Computer.IsControllerEnabled` is true (Computer.cs lines 118-151). `NzxtGroup` enumerates every HID device with VID 0x1E71 and reads its product name (NzxtGroup.cs lines 22-24); it opens a stream only for PIDs it supports, and builds a `KrakenV3` for PID 0x300C (line 42), whose constructor opens the stream (`dev.TryOpen`, KrakenV3.cs line 71) and immediately writes `10 01` (line 75). The other NZXT devices on the development PC (0x2011, 0x2021) fall into the `default` branch and are only logged. Note that `dev.GetProductName()` (NzxtGroup.cs line 24) is called for **every** VID 0x1E71 HID device before the PID switch; on Windows that string query needs a device handle (opened without read/write access; this is from memory of HidSharp's source, **UNVERIFIED (U23)**). So merely constructing an LHM `Computer` with `IsControllerEnabled = true` touches all four NZXT HID devices and sends a report to the Kraken. Under the project's "never open NZXT HID devices during development" rule that flag must stay `false` in development and tests too.
- `KrakenV3` then writes `10 01` once, writes `74 01` on every `Update()`, and writes flat `72` packets whenever its Control sensors are in Software or Default mode (60 % pump, 40 % fan for Default). With no saved setting the mode is `Undefined` and nothing is written (Control.cs lines 36-44; KrakenV3.cs lines 141-194).
- The restored NuGet package 0.9.6 does contain the `KrakenV3` and `NzxtGroup` types (its XML doc lists them). A byte search (read-only, nothing loaded or run) of `runtimes/win-x64/lib/*/LibreHardwareMonitorLib.dll` finds the UTF-16 user strings "NZXT Kraken Elite", "NZXT Kraken Elite v2", "NZXT Kraken Z3" and "1.2.4", which are the names and the firmware label of the HEAD `KrakenV3` switch (KrakenV3.cs lines 33-49). That is strong circumstantial evidence that 0.9.6 drives PID 0x300C as HEAD does. (The `ref/` assemblies are reference stubs without these strings.) The same runtime DLL also contains the 2023-family channel headers as adjacent constant data: the byte sequences `72 02 01 01` and `72 01 01 00` sit 8 bytes apart in its initialised-data area (fifth pass, read-only byte search of `runtimes/win-x64/lib/net8.0/LibreHardwareMonitorLib.dll`). **UNVERIFIED (U18):** that the 0.9.6 code (channel bytes, writes) is byte-identical to HEAD. *Confirm:* decompile in a sandbox. The design rule below makes this moot.
- **Design rule:** exactly one component owns the Kraken. If RigControl implements this spec, set `IsControllerEnabled = false` on the LHM `Computer` (or otherwise keep LHM away from VID 0x1E71), and never drive the LHM Kraken Control sensors.

---

## 13. Test vectors

All packets are 64 bytes. Offsets 0..63, 16 bytes per line. "Asserted by test" means the reference project's own unit tests check those bytes; "derived" means computed from the documented algorithm with a separate script (which reproduced liquidctl's `test_curve_final_pwm` exactly).

Important provenance note: liquidctl's tests run against the **Z3** fixture, whose header is `72 01 00 00` / `72 02 00 00`. The tests assert the Report ID (`0x72`) and offsets 4..43. For the Elite only offsets 2 and 3 differ (`01 00` pump, `01 01` fan), taken from `_SPEED_CHANNELS_KRAKEN2023`, LHM and the kernel driver. No reference unit test covers the 2023 channel bytes.

### TV1 - SET_INTERVAL (host -> device)

Derivation: LQ line 710, `update_interval = 1`.

```
70 02 01 B8 01 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV2 - FINISH_INIT (host -> device)

Derivation: LQ line 711.

```
70 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV3 - GET_FIRMWARE (host -> device)

Derivation: LQ line 681; LHM line 11; ORGB lines 916-917.

```
10 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV4 - GET_STATUS (host -> device)

Derivation: LQ line 736; LHM line 14.

```
74 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV5 - Pump fixed 84 % (host -> device)

Derivation: profile `[(0,84),(58,84)]` + failsafe `(59,100)` -> 39 x 84 (0x54) then 100 (0x64). Offsets 4..43 **asserted by test** (`test_krakenz3_set_fixed_speeds_directly`, `data[3:43] == [84]*39 + [100]`); header per Elite channel ids.

```
72 01 01 00 54 54 54 54 54 54 54 54 54 54 54 54
54 54 54 54 54 54 54 54 54 54 54 54 54 54 54 54
54 54 54 54 54 54 54 54 54 54 54 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV6 - Fan fixed 50 % (host -> device)

Derivation: as TV5 with 50 (0x32). Offsets 4..43 **asserted by test** (same test, fan report).

```
72 02 01 01 32 32 32 32 32 32 32 32 32 32 32 32
32 32 32 32 32 32 32 32 32 32 32 32 32 32 32 32
32 32 32 32 32 32 32 32 32 32 32 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV7 - Pump curve (20,30) (30,50) (34,80) (40,90) (50,100) (host -> device)

Derivation: section 7.4. The 40 values in decimal, for 20..59 degC:
`30 32 34 36 38 40 42 44 46 48 50 58 65 72 80 82 83 85 87 88 90 91 92 93 94 95 96 97 98 99 100 100 100 100 100 100 100 100 100 100`.
Note 31 degC = 57.5 -> 58 and 33 degC = 72.5 -> 72 (round half to even). Offsets 4..43 **asserted by test** (`test_curve_final_pwm`, `test_krakenz3_set_speed_profile_directly`).

```
72 01 01 00 1E 20 22 24 26 28 2A 2C 2E 30 32 3A
41 48 50 52 53 55 57 58 5A 5B 5C 5D 5E 5F 60 61
62 63 64 64 64 64 64 64 64 64 64 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV8 - Fan curve, same points (host -> device)

Same test asserts the fan report carries the same 40 bytes. Only offsets 1..3 differ from TV7.

```
72 02 01 01 1E 20 22 24 26 28 2A 2C 2E 30 32 3A
41 48 50 52 53 55 57 58 5A 5B 5C 5D 5E 5F 60 61
62 63 64 64 64 64 64 64 64 64 64 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV9 - Pump fixed 10 % is clamped to 20 % (host -> device) - derived

Derivation: pump minimum 20 applied after interpolation; last point 100.

```
72 01 01 00 14 14 14 14 14 14 14 14 14 14 14 14
14 14 14 14 14 14 14 14 14 14 14 14 14 14 14 14
14 14 14 14 14 14 14 14 14 14 14 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV10 - Fan curve (20,20) (30,50) (40,100) (host -> device) - derived

The profile used (but not byte-asserted) in `test_krakenz3_not_totally_broken`. Decimal:
`20 23 26 29 32 35 38 41 44 47 50 55 60 65 70 75 80 85 90 95` then 20 x `100`.

```
72 02 01 01 14 17 1A 1D 20 23 26 29 2C 2F 32 37
3C 41 46 4B 50 55 5A 5F 64 64 64 64 64 64 64 64
64 64 64 64 64 64 64 64 64 64 64 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV11 - GET_LCD_INFO (host -> device)

Derivation: LQ line 719; first entry of every `*_hid` list in `test_krakenz3_response.py` (`[48, 1]`).

```
30 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV12 - Set brightness 60 %, orientation unchanged at 0 deg (host -> device)

**Asserted by test**: `brightness_hid` = `[48, 2, 1, 60, 0, 0, 1, 0]` (before padding).

```
30 02 01 3C 00 00 01 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV13 - Set orientation 90 deg, brightness unchanged at 50 % (host -> device)

**Asserted by test**: `orientation_hid` = `[48, 2, 1, 50, 0, 0, 1, 1]`.

```
30 02 01 32 00 00 01 01 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV14 - Status reply parse (device -> host), real capture from a Kraken Z3

`Z3_SAMPLE_STATUS` in `test_kraken3.py` (lines 34-37). **Asserted by test** (`test_krakenz3_reads_status_directly`).

```
75 01 2E 00 18 00 10 51 39 34 34 36 37 31 01 18
03 69 03 14 14 01 02 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

| Field | Bytes | Expected |
|---|---|---|
| Liquid temperature | [15]=0x18=24, [16]=0x03 | 24.3 degC |
| Pump speed | [17]=0x69, [18]=0x03 -> 0x0369 | 873 rpm |
| Pump duty | [19]=0x14 | 20 % |
| Fan speed | [23]=0x00, [24]=0x00 | 0 rpm |
| Fan duty | [25]=0x00 | 0 % |

### TV15 - Faulty status (device -> host), real capture from a Kraken X3

`X3_FAULTY_STATUS` (lines 29-32). The test asserts that a warning is logged. Expected in RigControl: temperature = invalid/sensor fault; pump 0x0ACC = 2764 rpm; pump duty 100 %. (X3 reports have no fan fields; this vector is for the `FF FF` rule only.)

```
75 02 20 00 36 00 0B 51 53 58 34 35 33 20 01 FF
FF CC 0A 64 64 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV16 - Synthetic status with non-zero fan fields (device -> host) - NOT a capture

Constructed from the offset table to exercise the fan parse. Opaque bytes 2..13 set to zero.

```
75 01 00 00 00 00 00 00 00 00 00 00 00 00 01 1F
07 DA 07 3C 3C 00 00 65 04 2D 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Expected: 31.7 degC; pump 0x07DA = 2010 rpm, 60 %; fan 0x0465 = 1125 rpm, 45 %.

### TV17 - Synthetic firmware reply (device -> host) - NOT a capture

Only the header and offsets 17..19 are meaningful. Expected "2.0.1".

```
11 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 02 00 01 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV18 - Synthetic LCD info reply (device -> host) - mirrors liquidctl's mock

`MockKraken` (test_kraken3.py lines 130-133) sets `reply[0x18] = 50`, `reply[0x1A] = 0`. Expected brightness 50 %, orientation 0 deg.

```
31 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 32 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV19 - Real Kraken 2023 Elite status report, pushed form (device -> host) - web capture

Source: WEB-G PR #605, debug log by maxawake, 14 June 2023, device "NZXT Kraken 2023 Elite" (see section 4). All 64 bytes were compared with the raw comment text from the GitHub REST API in the fifth pass (bytes 27..63 are `00`). Not a liquidctl test fixture. This is the only Elite (PID 0x300C) status sample found.

```
75 02 BB 8C 90 82 0E 58 0A 31 00 00 00 00 01 1E
02 12 05 1E 1E 01 02 FC 03 32 32 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

| Field | Bytes | Expected |
|---|---|---|
| Liquid temperature | [15]=0x1E=30, [16]=0x02 | 30.2 degC |
| Pump speed | [17]=0x12, [18]=0x05 -> 0x0512 | 1298 rpm |
| Pump duty | [19]=0x1E | 30 % |
| Fan speed | [23]=0xFC, [24]=0x03 -> 0x03FC | 1020 rpm |
| Fan duty | [25]=0x32 | 50 % |

Use it to test the parser and the "accept `75 02` for display only" rule (6.1).

### TV20 - App default: pump fixed 60 % and fan fixed 40 % (host -> device) - derived

Derivation: section 7.4 with `[(0,60),(58,60)]` / `[(0,40),(58,40)]` plus the failsafe; same shape as TV5/TV6 (which are asserted by test). 60 = 0x3C, 40 = 0x28.

```
72 01 01 00 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C
3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C
3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 3C 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00

72 02 01 01 28 28 28 28 28 28 28 28 28 28 28 28
28 28 28 28 28 28 28 28 28 28 28 28 28 28 28 28
28 28 28 28 28 28 28 28 28 28 28 64 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV21 - Real Kraken 2023 Elite LCD info reply (device -> host) - web capture

Source: the same WEB-G PR #605 log as TV19 (reply to `30 01`). All 64 bytes were compared with the raw comment text from the GitHub REST API in the fifth pass (bytes 28..63 are `00`). Expected: brightness [24] = 0x64 = 100 %, orientation [26] = 3 = 270 deg. Offsets 14..23 and 25, 27 (`05 00 80 00 00 10 80 02 80 02`, `01`, `FF`) are not interpreted by any reference; ignore them.

```
31 01 BB 8C 90 82 0E 58 0A 31 00 00 00 00 05 00
80 00 00 10 80 02 80 02 64 01 03 FF 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Suggested additional unit tests (no hardware): reply matcher skips `75 02` / `73 xx` / unknown headers while waiting for `11 01`; a report shorter than 26 bytes is ignored for status (the kernel ignores reports shorter than 20 bytes: `if (size < MIN_REPORT_LENGTH) return 0;` with `MIN_REPORT_LENGTH 20`; RigControl needs offset 25, so it requires >= 26); builder refuses pump bytes < 20, any byte > 100, and a last point < 100.

---

## 14. Disagreements between sources (resolved)

| Topic | Source A | Source B | Resolution |
|---|---|---|---|
| Firmware version offsets | LQ-doc protocol file: "Byte 3, 4, 5" | LQ code, LHM, ORGB, kernel: 17, 18, 19 | 17, 18, 19 |
| LCD brightness offset | LQ-doc: "Byte 18" | LQ code: `msg[0x18]` = 24 | 24 |
| Curve range | LQ-doc: "20 to 60 degree" (its "Byte 5-44" is 1-based = offsets 4..43, so position is not a disagreement) | LQ code + tests + kernel: offsets 4..43, 20..59 degC | 4..43, 20..59 |
| Elite pushes status unasked | WEB-G PR #605 Elite log: a `75 02` arrived with no `74 01` sent | WEB-K comment: Z-series "send a sensor report only when requested" | Always request; tolerate pushed reports (U3) |
| Lighting info request | LQ-doc: `20 01` | LQ code, ORGB: `20 03` | Not sent for this device at all |
| Duty bytes written | LHM: 60 bytes (4..63) | LQ, kernel: 40 bytes (4..43) | 40 bytes, zero padding |
| Init reports `70 ..` | LQ, kernel: sent | LHM: not sent | Send them (U2) |
| Status header check | LHM: must be `75 01` | LQ: none; kernel: Report ID `75` only | Section 6.1 |
| Fan minimum | LQ: 0 | LHM UI: 20 | Protocol allows 0; app floor is a product decision |
| Device label | LQ: "NZXT Kraken 2023 Elite (broken)" | LHM: "NZXT Kraken Elite"; kernel: `kraken2023elite` | Same hardware, PID 0x300C. Reason for "(broken)" unknown (U14) |

---

## 15. UNVERIFIED register

| Id | Statement | How to confirm |
|---|---|---|
| U1 | Windows reports 64-byte input and output report lengths for this unit | Read `GetMaxInputReportLength()` / `GetMaxOutputReportLength()` at runtime; refuse writes if not 64 |
| U2 | Whether the `70 ..` init is needed at all on this unit (LHM omits it; the PR #605 author calls it "useless" beyond enabling periodic status), and that it has no side effect such as resetting the active speed profile or blocking LCD orientation changes (one 2023 anecdote) | With CAM closed: status, init, status; compare duties |
| U3 | The Elite pushes unsolicited `75 02` status reports (probably every 0.5 s after SET_INTERVAL) on this unit's firmware, with the same field layout including fan fields (the PR #605 author says init makes the device "send its stats every x millisecond"; one 2023 Elite log in PR #605 shows an unrequested `75 02` with plausible values at 15-19 and 23-25, see TV19; the kernel parses any `0x75` report identically for the 2023 kind; but a kernel comment says Z-series only report on request) | Listen passively after init; compare with a `75 01` reply |
| U4 | USB bcdDevice 2.01 means firmware 2.0.x (Elite firmware 2.0.0 and 2.1.0 are reported in WEB-G #631, so a 2.x firmware is plausible) | GET_FIRMWARE |
| U5 | Firmware behaviour below 20 degC, at/above 60 degC, and between points | Observe at the low end only; do not test the high end |
| U6 | The firmware's own minimum pump duty | Do not test. Keep >= 20 |
| U7 | Fan duty 0 stops the fans | With CAM closed, flat fan 0 (last point 100), watch rpm |
| U8 | The Elite itself keeps running the last curve after the host stops (documented for the driver family) | Set a distinctive fan duty, exit RigControl, watch fans for several minutes |
| U9 | Power-up default (one web comment, model not named: pump 30 % fixed "silent mode"; fans unknown) | Cold boot without CAM autostart; poll status only |
| U10 | CAM drives the device with flat curves from software, so a flat duty is left behind when it exits | Passive listen cannot see CAM's writes; needs a USB capture, or observe duty vs liquid temperature after closing CAM |
| U11 | Safe maximum rate of `72` writes | Unknown; stay at <= 1 per channel per second |
| U12 | `30 01` / `30 02` work unchanged on the Elite with firmware 2.x. `30 01`: one 2023 Elite `31 01` capture of unknown firmware is sane (TV21) and one Elite on firmware 2.1.0 printed sane values through liquidctl (section 5). `30 02`: no Elite capture; 2023 user reports say brightness worked and orientation sometimes did not | GET_LCD_INFO returns sane values; after SET_LCD_CONFIG read back and compare |
| U13 | LCD brightness/orientation persist across power cycles | Observe after cold boot |
| U14 | Why liquidctl labels PID 300C "(broken)". No maintainer states it; all reported breakage concerns LCD image/GIF upload on firmware 2.x (issue #631), none concerns cooling (9.3) | liquidctl git history (shallow clone has no history) |
| U15 | Exact behaviour when CAM and RigControl both have the HID device open (open succeeds? errors?) | Try opening while CAM runs and record the result; no writes needed |
| U16 | Counting unrequested `73` / `75 01` reports reliably detects another controller | Depends on U3 and on the ack actually being sent |
| U17 | `CAMService` alone talks to the Kraken when the CAM window is closed | Passive 30 s listen with only the service running |
| U18 | LibreHardwareMonitorLib 0.9.6 (the restored package) handles PID 300C like the cloned HEAD | Package release notes / sandboxed decompile |
| U19 | Reply headers for `70 ..` (assumed `71 ..`) and that `73` / `31 02` acks are really sent | Log all input headers after each write |
| U20 | HidSharp zero-pads short writes | HidSharp source; avoided by always writing 64 bytes |
| U21 | The PnP layout (1.1) and CAM process/service snapshot (12.1) are still current (observed by passes 1-3, not re-observed in passes 4 and 5; the fifth pass's task brief independently lists the same devices, CAM processes and `CAMService`) | Runtime: the selection rule (1.1) and CAM detection (12.4) re-check them every time |
| U22 | Status bytes 19 / 25 report the duty the firmware is currently applying from the stored curve (needed by the write verification in 7.6) | Write a two-level curve with the step just above the current liquid temperature; watch the reported duty |
| U23 | HidSharp's `GetProductName()` opens a (zero-access) handle to each device on Windows (relevant to 12.5 only) | HidSharp source |

---

## 16. Implementation checklist (C#, HidSharp)

1. Find `HidDevice` with VID 0x1E71, PID 0x300C; require exactly one; path contains `mi_01`; usage page 0xFF00 / usage 0x0001; max input and output report lengths both 64.
2. Check for CAM (section 12.4). Choose monitor-only (zero writes; API.md `blockedBy` non-empty) or control mode. Re-check every poll cycle.
3. Open one `HidStream`; one writer lock; one reader loop dispatching on bytes 0-1.
4. Control mode: TV1, TV2, GET_FIRMWARE (wait `11 01`), then pump profile, 200 ms, fan profile.
5. Every second: GET_STATUS, wait for `75 ..`, parse per 6.2, apply fault rules, verify duties per 7.6.
6. Build curves only through one function that enforces: 40 points, pump >= 20, all <= 100, non-decreasing, last = 100, zero padding, correct channel bytes.
7. On `IOException` / device removal / a streak of **request** timeouts (for example 3 consecutive GET_STATUS with no `75` reply within 2 s): close, re-enumerate, back to step 1. A read that times out while no request is outstanding is normal (the device may send nothing unasked, U3) and is not an error; HidSharp signals it with a `TimeoutException` (general HidSharp behaviour, the XML docs only describe `ReadTimeout`), so the reader loop must catch it and continue.
8. On resume from sleep and on device arrival: back to step 1.
9. On exit: close the handle. Send nothing.
10. Never open the WinUSB interface. Never send anything outside section 3.

---

## 17. Second-pass verification log (2026-11-15)

What was re-checked, and how:

| Item | Method | Result |
|---|---|---|
| Reference revisions in section 0 | `git log -1` on each clone (read-only) | liquidctl `292dc56` 2026-06-16, LibreHardwareMonitor `9e2d677` 2026-11-11, OpenRGB `d09a493` 2026-11-14. Match |
| PnP layout in 1.1 (MI_00 WinUSB via `USB\MS_COMP_WINUSB`, MI_01 HidUsb, TLC `UP:FF00_U:0001`, `REV_0201`) | `Get-PnpDevice`, `Get-PnpDeviceProperty` | Match. Also seen: parent of the composite device is a `VID_05E3` USB hub |
| Other NZXT HID devices | `Get-PnpDevice` | One `1E71:2011`, two `1E71:2021`. Match |
| CAM presence in 12.1 | `Get-Process`, `Win32_Service`, file version of `NZXT CAM.exe` | `NZXT CAM` x5, `cam_helper` x2, `CAMService` Auto/Running, version 4.76.9.72. `camsvc` is the Windows svchost service. Match |
| Channel bytes, curve range, pump minimum, status offsets, firmware offsets | liquidctl, LHM, OpenRGB source; Linux `nzxt-kraken3.c` (web) | All agree with sections 5-7 |
| LHM claims in 12.5 | `Computer.cs` lines 118-151 and 548-554, `Control.cs` lines 33-44, package XML of 0.9.6 | Match |
| Test vectors TV5-TV13 | Rebuilt independently with a script; TV7 values also hand-checked (57.5 -> 58, 72.5 -> 72) | Byte-identical |
| TV14-TV18 decodes | Hand-decoded from the dumps | Match |
| WEB-G #627 (descriptor, 300C status with pump 30 % / fan 30 % / fan 0 rpm, bcdDevice 1.02) | Web fetch | Match |
| WEB-G PR #605 quotes | Web fetch | Mostly match. **Corrected:** the unsolicited `75 02` report was logged after a `0x36` write, not while waiting for `31 01`, and the log is from an Elite (section 4). **Clarified:** the "silent mode, pump 30 %" remark was about the standard 2023 Kraken 240 (section 7.5) |

Additions in this pass: HidSharp-based argument for numbered reports (1.2), read-buffer sizing and "do not use `OpenOption.Exclusive`" (1.2), exact floating-point evaluation order for curve interpolation (7.4).

Still not reachable without touching the device: every U-item in section 15. The fastest safe way to retire the most important ones (U1, U3, U15, U17) is a passive session: enumerate, open the HID path, read `GetMaxInput/OutputReportLength`, send nothing, and log report headers for 30 s, once with CAM running and once with only `CAMService` running.

---

## 18. Verification log (third pass, adversarial, 2026-11-15)

Method: every protocol fact was re-derived from the local clones (liquidctl `292dc56`, LibreHardwareMonitor `9e2d677`, OpenRGB `d09a493`) and the HidSharp 2.6.4 XML docs. Every curve packet was rebuilt with an independently written script (own code; no reference code was imported or run). PnP, process and service facts were re-read with read-only `Get-PnpDevice` / `Get-PnpDeviceProperty` / `Get-Process` / `Win32_Service`. Web pages (kernel source and docs, liquidctl #128, #627, PR #605) were re-fetched through a summarising fetch tool; quotes below are as returned by that tool and were cross-checked with a second, narrower fetch where they mattered. No device was opened and nothing was sent.

### 18.1 Corrections made in this pass

| # | Section | Was | Now | Evidence |
|---|---|---|---|---|
| C1 | 4, 6.1, 15 (U3), 13 (TV19) | PR #605 Elite log "truncated" after the pump bytes; said it "shows the Elite does push" `75 02` | Full line recovered: bytes 0..26 = `75 02 bb 8c 90 82 0e 58 0a 31 00 00 00 00 01 1e 02 12 05 1e 1e 01 02 fc 03 32 32`, giving fan 1020 rpm / 50 % at offsets 23..25. Claim softened to "may push": the kernel driver states Z-series report only on request, and CAM's state during that capture is unknown. Added as TV19 | WEB-G PR #605 (two fetches); WEB-K `kraken3_read_x53` comment and `kraken3_read_z53` annotation |
| C2 | 7.5, 15 (U9) | "silent mode" remark was "answering a question about a 2023 Kraken 240" | Neither the question (jensmeindertsma) nor the reply (brokenmass) names a model; the asker mentioned a "2023 Kraken 240 black" in a separate comment | WEB-G PR #605 |
| C3 | 12.2 | "On these models CAM continuously renders and sends LCD frames" | That remark is about the standard Kraken 2023; for the Elite it is UNVERIFIED | WEB-G PR #605 |
| C4 | 8 | 200 ms pump/fan gap "from LQ's 0.2 s"; "LQ sleeps 0.2 s after a burst" | The 0.2 s sleep is only in the Linux hwmon path (`_set_speed_profile_hwmon`, line 370). The direct path sends pump and fan back to back. 200 ms kept as a pure design rule | LQ lines 356-373, 411; `test_krakenz3_set_fixed_speeds_directly` |
| C5 | 8 | WEB-K "status-request mutex" | Only the buffer mutex in `kraken3_write_expanded` was confirmed; the rate-limit quotes come from the kernel **documentation**, not the .c file. *(Partly superseded by E1 in section 20: the raw kernel source does contain a status-request mutex, `z53_status_request_lock`.)* | WEB-K source and `nzxt-kraken3.rst` |
| C6 | 0.1 | OpenRGB "uses 64-byte packets ... for this NZXT device family" | OpenRGB uses 64 for 0x3012 but **512** for 0x3014 (2024 Plus). Strengthens U1 | `NZXTHue2Devices.cpp` device entries |
| C7 | 1.2 | `OpenOption.Exclusive` "does not lock out NZXT CAM" stated as a doc fact | The XML docs only call it "HIDSharp's exclusivity layer"; its effect on non-HidSharp processes is not stated (UNVERIFIED). Advice (do not use it) unchanged | HidSharp.xml `P:HidSharp.OpenOption.Exclusive` |
| C8 | 7.2, 14 | LQ-doc "Byte 5-44" listed as a disagreement with offsets 4..43 | The doc numbers request bytes from 1, so "Byte 5-44" = offsets 4..43. Only "20 to 60" disagrees | `nzxt_x3-z3-2023.md` lines 50-55, 77-80 |
| C9 | 12.5 | `NzxtGroup` "opens every HID device with VID 0x1E71" | It enumerates them all and reads product names; it opens a stream only for supported PIDs (0x300C included) | NzxtGroup.cs lines 22-70; KrakenV3.cs line 71 |
| C10 | 0.1 | `NZXTHue2Devices.h` lines 24-45 | Lines 23-45 | file |

### 18.2 Additions in this pass

- 1.1: #627 descriptor details: bulk `wMaxPacketSize` 512, HID report descriptor length 360 bytes (contents not available), and the caveat that 64-byte endpoints do not prove 64-byte reports.
- 1.2: kernel `report->id` argument for numbered reports; the point that Linux hidraw cannot distinguish numbered and unnumbered reports; optional runtime descriptor logging; the reason U1 is a real risk.
- 7.2: liquidctl guide's 60 degC warning and RigControl alarm thresholds (design rule).
- 7.5: kernel statement "The devices can only control the duty through a curve" as further evidence that the firmware runs the curve itself.
- 7.6: verification uses `75 01` replies only.
- 11: rows for other `70 ..` forms and unknown sub-commands of `10`, `30`, `74`.
- 13: TV19 (real Elite pushed status) and TV20 (app-default 60 % pump / 40 % fan packets).

### 18.3 Items confirmed unchanged

| Item | How checked | Result |
|---|---|---|
| PnP layout: composite parent `REV_0201`; MI_00 `WINUSB` "NZXT Kraken Elite" with compatible ID `USB\MS_COMP_WINUSB`, class FF; MI_01 `HidUsb`, class 03; one HID child `HID\VID_1E71&PID_300C&MI_01` with `HID_DEVICE_UP:FF00_U:0001` (single top-level collection, no `&Col` suffix) | PnP read | Confirmed |
| Other NZXT HID devices: one 0x2011, two 0x2021 | PnP read | Confirmed |
| CAM: `NZXT CAM` x5, `cam_helper` x2, `CAMService` Auto/Running, file version 4.76.9.72; `camsvc` is Windows "Capability Access Manager Service" | Process/service read | Confirmed |
| Report IDs: byte 0 = command / response code; output reports via interrupt OUT; input reports via interrupt IN; no feature reports; no checksum; little-endian u16 | LQ usb.py 443-485, kraken3.py 475-477, 741-747; LHM 83, 212-215, 235; WEB-K `report->id` dispatch | Confirmed (report length still U1) |
| Init: clear queue, `70 02 01 B8 01`, `70 01`, `10 01` -> `11 01`; interval code = `1 + round((0.5-0.5)/0.25)` = 1 | LQ 696-720; WEB-K `set_interval_cmd`, `finish_init_cmd`, probe order | Confirmed |
| Firmware at reply offsets 17/18/19 | LQ 255-256; LHM 84; ORGB `SendFirmwareRequest`; WEB-K `FIRMWARE_VERSION_OFFSET 17` | Confirmed |
| Status offsets 15/16 temp, 17-18 pump rpm LE, 19 pump duty, 23-24 fan rpm LE, 25 fan duty; `FF FF` = fault | LQ 738-747; LHM 214-238; WEB-K defines | Confirmed |
| Channel bytes 2023 family: pump `72 01 01 00`, fan `72 02 01 01` | LQ 69-72 and 573-584; LHM 43-44; WEB-K `kraken3_write_curve` | Confirmed |
| 40 points, liquid temp 20..59 degC, offsets 4..43, zero padding to 64 | LQ 380-382, 475-477; WEB-K `CUSTOM_CURVE_POINTS 40`, `SET_CURVE_DUTY_CMD_LENGTH (4 + 40)`, `memcpy_and_pad` to 64 | Confirmed |
| Pump min 20, max 100; fan 0..100 | LQ 69-72; WEB-K `PUMP_DUTY_MIN 20` (applied to channel 0 only); LHM 96, 111 | Confirmed (firmware's own limit still U6) |
| Fixed duty = flat curve with 59 degC point forced to 100 | LQ 413-414 + `normalize_profile`; WEB-K `kraken3_write_fixed_duty` | Confirmed |
| Curve maths (sort key, monotonic quirk, stop at first 100, interpolation order, banker's rounding, clamp after interpolation) | util.py 132-137, 185-246 | Confirmed |
| TV1-TV13 | Rebuilt byte by byte by script and by hand; TV5-TV8 offsets 4..43 match the `data[3:43]` assertions (`data` excludes byte 0) in test_kraken3.py 422-424 and 486-488; TV12/TV13 match `test_krakenz3_response.py` lines 4-5 | Byte-identical |
| TV14-TV18 decodes (24.3 degC / 873 / 20 / 0 / 0; FF FF / 2764 / 100; 31.7 / 2010 / 60 / 1125 / 45; "2.0.1"; 50 % / 0) | Script decode | Confirmed |
| LCD info offsets 0x18 / 0x1A; set packet `30 02 01 <b> 00 00 01 <o>` | LQ 728-732, 802-828; tests | Confirmed (Elite behaviour still U12) |
| Guide quotes: persistence while powered (line 8), CAM conflict (line 10), ring unsupported (line 29), init after Mechanical Off (line 44), duty table (93-96) | Guide file | Confirmed |
| LHM: firmware "1.2.4" label, 60-byte fill, flat 60 %/40 % defaults, resend-until-match loop, 5000 ms timeout, no `70` init | KrakenV3.cs | Confirmed |
| #627: 300C status with pump 30 %, fan 30 %, fan 0 rpm; bcdDevice 1.02; no firmware version shown; CAM state not stated | Web fetch | Confirmed |
| #128: interval byte meaning tested only on Kraken X63 fw 2.1.0 | Web fetch | Confirmed |

### 18.4 Still open

Every U-item in section 15 remains open. The most safety-relevant are U1 (report length; fail closed), U8/U9 (firmware keeps running the last curve; power-up default), U15/U17 (CAM coexistence) and U6 (firmware minimum pump duty; never go below 20). None can be closed without opening the device. The passive session described at the end of section 17 is still the recommended first step.

---

## 19. Verification log (fourth pass, adversarial, 2026-11-15)

Method: sections 0-18 were treated as unverified, including the third pass's own corrections. Every source citation was re-opened at the cited lines in the local clones (liquidctl `292dc56` 2026-06-16, LibreHardwareMonitor `9e2d677` 2026-11-11, OpenRGB `d09a493` 2026-11-14; revisions re-read with `git log -1`), the HidSharp 2.6.4 XML docs and the LibreHardwareMonitorLib 0.9.6 package. Every test vector TV1-TV21 was parsed out of this file and compared byte by byte with packets rebuilt by a newly written script (own code, running-max variant of 7.4; no reference code imported or run); the script also asserts that its TV7 values equal liquidctl's `test_curve_final_pwm` and that TV14/TV15 equal the `Z3_SAMPLE_STATUS` / `X3_FAULTY_STATUS` fixtures. Web sources (kernel `nzxt-kraken3.c`, liquidctl PR #605, issues #627 and #128) were re-fetched through a summarising fetch tool, with narrow repeat fetches for every quoted byte string. No device was opened, nothing was sent, and no PnP, process or service query was run in this pass (see U21).

### 19.1 Corrections made in this pass

| # | Section | Was | Now | Evidence |
|---|---|---|---|---|
| D1 | 0.1 | `NZXTHue2Devices.h` lines 23-45 (the third pass's C10) | Lines 24-45; line 23 is the end of the comment banner | file |
| D2 | 7.2 (and the third pass's C8) | LQ-doc "numbers response bytes from 0" | Its response numbering is inconsistent: status "Byte 15" is 0-based, LCD "Byte 18" is hex 0x18, bucket-info "Byte 18-19"/"20-21" are 1-based (code offsets 17-18/19-20). Never take an offset from that file alone. Conclusions unchanged | `nzxt_x3-z3-2023.md` lines 38-43, 69-70, 114-115; LQ lines 1082-1083 |
| D3 | 8 | Re-init after resume cites "LQ docstring lines 221-224" | `KrakenZ3.initialize` docstring lines 687-690 (221-224 is the identical `KrakenX3` text) | LQ |
| D4 | 13 (TV19) | Bytes 27..63 "show as zero on the page as far as it was read" | Bytes 0..28 quoted (27-28 = `00 00`); the rest reported as zeros by the fetch tool | PR #605, two fetches |
| D5 | 1.2 (HidSharp notes) | "65 and byte 0 always 0 means no Report IDs" | Added: 65 alone does not prove that (numbered 64-byte payloads also give 65); anything other than 64 still means "do not write" | HidSharp.XML `GetMaxInputReportLength` ("including the Report ID byte") |
| D6 | 7.1 | PR #605 "fan needs bytes 2-3 = `01 01`" | Quoted as written ("byte 3 and 4", 1-based = offsets 2-3), with author and date; kernel channel-byte logic added | PR #605 description; WEB-K `kraken3_write_curve` |
| D7 | 11 | "a byte 1 other than the allow-listed value" | Lists the allowed values explicitly (`30` has two: `01`, `02`) | section 3 |
| D8 | 1.1, 12.1, 15 | PnP / CAM snapshot presented as current | Marked as observed by earlier passes and not re-observed (U21); runtime checks cover it | this pass's scope |

### 19.2 Additions in this pass

- 1.2: kernel `kraken3_write_expanded` uses `hid_hw_output_report` with a 64-byte zero-padded buffer under `buffer_lock` (output reports, 64 bytes including the ID); HidSharp default `ReadTimeout` 3000 ms.
- 2 and 4: in the PR #605 Elite log, replies lag (the `37 03` arrived one read late, after an unrequested `75 02`), and offsets 2..13 are identical across `31 01`, `75 02`, `37 03`. The write sequence in that log contains no `70` or `74`.
- 4: issue #128 describes the SET_INTERVAL byte as the interval at which the device sends status updates (X63, firmware 2.1.0; 0x0/0x1 = 0.5 s, 0x0B = 3 s). Kernel: `set_interval_cmd` is sent to every kind, KRAKEN2023 included; `STATUS_VALIDITY 2000` "equivalent to period of four status reports".
- 9.1, 13: TV21, a real Elite `31 01` reply (brightness 100, orientation 3); weak support for U12.
- 9.3: PID 0x300C always takes liquidctl's older upload path; the fw-2 path is for 0x300E only. #627: a user reports static and GIF upload working on a 300C.
- 12.5: the LHM 0.9.6 runtime DLLs contain the HEAD `KrakenV3` strings for 0x300C (U18 narrowed).

### 19.3 Items re-derived and confirmed

| Item | How checked | Result |
|---|---|---|
| Constants `_READ_LENGTH 64`, `_WRITE_LENGTH 64`, `_MAX_READ_ATTEMPTS 12`, `_CRITICAL_TEMPERATURE 59`, `_COLOR_CHANNELS_KRAKEN2023 = {}` | LQ lines 42-44, 74, 85 | Confirmed |
| 0x300C match entry: `_SPEED_CHANNELS_KRAKEN2023`, description "NZXT Kraken 2023 Elite (broken)", 2 MB bulk buffer, 640x640 | LQ lines 573-584 | Confirmed |
| Channel ids pump `[1,1,0]` 20..100, fan `[2,1,1]` 0..100 | LQ 69-72; LHM 43-44; WEB-K `kraken3_write_curve`; PR #605 | Confirmed (4 sources) |
| Init order: clear queue, `70 02 01 B8 01`, `70 01`, `10 01` -> `11 01` (<= 12 reads), `30 01` -> `31 01`; `20 03` skipped because no colour channels; `70` writes not gated by hwmon | LQ 696-726 | Confirmed |
| Interval code `1 + round((0.5-0.5)/0.25)` = 1 | LQ 709; WEB-K `set_interval_cmd` | Confirmed |
| Firmware at offsets 0x11/0x12/0x13 | LQ 255-256; LHM 83-84; ORGB 929; WEB-K `FIRMWARE_VERSION_OFFSET 17` | Confirmed |
| Status request `74 01`; LQ takes the next report unchecked; LHM loops to `75 01`; kernel checks `report->id == 0x75`, `size >= 20`, waits `STATUS_VALIDITY` 2000 ms | LQ 734-747; LHM 206-212; WEB-K | Confirmed |
| Status offsets 15/16 temperature (`b15 + b16/10`; kernel `*1000 + *100` milli-degC), 17-18 pump rpm LE, 19 pump duty, 23-24 fan rpm LE, 25 fan duty; `FF FF` fault check | LQ 738-747; LHM 214-238; WEB-K defines and `kraken3_raw_event`; LQ-doc lines 38-43 | Confirmed |
| Offset 14 = `01` and byte 20 = byte 19 in all three liquidctl samples and in TV19 | fixtures | Confirmed |
| Curve: header + 40 bytes for 20..59 degC, zero-padded to 64; kernel `CUSTOM_CURVE_POINTS 40` ("For temps from 20C to 59C (critical temp)"), `SET_CURVE_DUTY_CMD_LENGTH (4 + 40)` | LQ 375-411, 475-477; WEB-K | Confirmed |
| Pump minimum 20 applied to the pump only (kernel `channel == 0 && percent_value < PUMP_DUTY_MIN`) | WEB-K; LQ 70; LHM 96 | Confirmed |
| Fixed duty = `[(0,d),(58,d)]` + failsafe -> 39 x d then 100; kernel "The devices can only control the duty through a curve" and "Force duty to 100% at critical temp" | LQ 413-414; WEB-K `kraken3_write_fixed_duty` | Confirmed |
| `normalize_profile` (sort key `(x, -y)`, skip equal x, compare with previous original y, break at first 100), `interpolate_profile` (lower/upper search, divide then multiply, Python `round`), clamp after interpolation | util.py 132-137, 185-246 | Confirmed; quirk example `(30,40),(35,30),(40,35)` -> `(30,40),(35,40),(40,35),(59,100)` re-derived by hand |
| Test assertions use `data[3:43]`, where `data` excludes byte 0 (`Report(data[0], data[1:])`), i.e. offsets 4..43 | `_testutils.py` line 59; test_kraken3.py 422-424, 486-488 | Confirmed |
| TV1-TV13, TV20 | Script rebuild, byte-compared with this file | All byte-identical |
| TV14-TV19, TV21 decodes (24.3/873/20/0/0; fault/2764/100; 31.7/2010/60/1125/45; "2.0.1"; 50/0; 30.2/1298/30/1020/50; 100/3) | Script decode; TV14/TV15 equal the liquidctl fixtures | Confirmed |
| LCD info at 0x18/0x1A; set packet `30 02 01 <b> 00 00 01 <o>`; brightness 0..100; orientation in {0,90,180,270} -> /90 | LQ 728-732, 802-828; test_krakenz3_response.py 4-5 | Confirmed |
| LCD upload opcodes (`36 03`, 16 x `30 04 nn`, `32 02`, `32 01`, `36 01`, bulk header `12 FA 01 E8 AB CD EF 98 76 54 32 10`, `36 02`, `38 01 04 nn`; `38 01 02 00` = liquid mode); bulk endpoint 0x02 | LQ 773-777, 974-1180; test_krakenz3_response.py lines 3, 6-30 | Confirmed (listed only to forbid them) |
| OpenRGB: no 0x300C; 0x2011 = `NZXT_RGB_FAN_CONTROLLER4_PID`, 0x2021 = `NZXT_RGB_CONTROLLER_2_PID`; 0x3012 64-byte, 0x3014 512-byte packets; `SendFan` = `62 01`; `SendFirmwareRequest` `10 01` -> `11 01`, version at 0x11-0x13; old Kraken detect = 0x170E/0x1715 only | ORGB files at the cited lines | Confirmed |
| LHM: `case 0x300C` "NZXT Kraken Elite", fw label "1.2.4", 60-byte duty fill (offsets 4..63), default 60 %/40 %, `Undefined` mode writes nothing, resend loop, 5000 ms timeout, no `70` init; `NzxtGroup` builds `KrakenV3` for 0x300C (line 42), others to `default`; `IsControllerEnabled` adds `NzxtGroup` | KrakenV3.cs, NzxtGroup.cs 22-70, Control.cs 33-44, Computer.cs 118-151 | Confirmed |
| HidSharp: `GetMaxInput/OutputReportLength` include the Report ID byte; `Write` "Place the Report ID in the first byte"; `OpenOption.Exclusive` is "HIDSharp's exclusivity layer" | HidSharp.XML (netstandard2.0) lines 333-344, 490-495, 583-591 | Confirmed |
| Guide quotes (lines 8, 10, 29, 44, 81, 93-96) | Guide file | Confirmed |
| WEB-G #627: 300C, bcdDevice 1.02, interface 0 bulk OUT 0x02 (512), interface 1 interrupt IN 0x81 / OUT 0x01 (64), `wDescriptorLength 360`, descriptor unavailable; status 33.5 degC / 1357 rpm / 30 % / fan 0 rpm / 30 % | Web fetch | Confirmed |
| WEB-G PR #605: Elite `75 02` bytes 0..28 exactly as in TV19; brokenmass 1 Sep 2023 "default is silent mode (pump at 30% fixed speed)"; CAM state during the log not stated | Web fetches | Confirmed |

### 19.4 Still open

Unchanged from 18.4, plus U21. Nothing in this pass changes a packet, an offset or a safety rule; the corrections are citations, wording and provenance. No contract (API.md) issue was found; this spec does not define UI shapes.

---

## 20. Verification log (fifth pass, adversarial, 2026-11-15)

Method: sections 0-19 were treated as unverified, including the earlier passes' own logs. The file was read end to end first to look for half-finished edits from an interrupted pass; none were found (section 19 is complete and every table is well-formed). Then:

- **Local source, re-opened at every cited line:** liquidctl `292dc56` (`kraken3.py` 1-130, 195-480, 480-870, 940-1180; `usb.py` 415-490; `util.py` 125-250; `tests/test_kraken3.py` 1-175, 395-560; `tests/_testutils.py` 1-110; `tests/test_krakenz3_response.py`; `docs/kraken-x3-z3-guide.md` 1-110; `docs/developer/protocol/nzxt_x3-z3-2023.md` complete; `README.md`, `CHANGELOG.md`), LibreHardwareMonitor `9e2d677` (`KrakenV3.cs` and `NzxtGroup.cs` complete, `Control.cs` 1-97, `Computer.cs` grep), OpenRGB `d09a493` (`NZXTHue2Devices.h` 15-70, `NZXTHue2Devices.cpp` 232-285, `NZXTHue2Controller.cpp` 136-172 and 898-960, `NZXTKrakenControllerDetect.cpp`, repository-wide search for `300C`). Revisions re-read with `git log -1`.
- **Packages (read-only, nothing loaded or executed):** HidSharp 2.6.4 `lib/netstandard2.0/HidSharp.XML`; LibreHardwareMonitorLib 0.9.6 (all `ref/` and `runtimes/` DLLs byte-searched for the `KrakenV3` strings and channel constants).
- **Web, as raw text (new in this pass):** `drivers/hwmon/nzxt-kraken3.c` (1037 lines) and `Documentation/hwmon/nzxt-kraken3.rst` (78 lines) from raw.githubusercontent.com, torvalds/linux master; liquidctl issue #627 (body + 13 comments), PR #605 (body + all 67 comments), issue #128 (body + 12 comments) and issue #631 (body + 41 comments) as JSON from the GitHub REST API. No summarising tool was involved.
- **Test vectors:** all 21 were parsed out of this file by a newly written script and compared byte for byte with packets rebuilt from scratch (own implementation of the literal liquidctl algorithm and of the running-maximum variant; no reference code imported or run). TV14/TV15 were compared with the hex fixtures in `test_kraken3.py`, TV19/TV21 with the raw log text in PR #605.
- **Not done:** no device was opened, nothing was sent, nothing was built, and no PnP / process / service query was run (this pass's task did not allow one; see U21). `docs/API.md` was read for the cooling section only.

### 20.1 Corrections made in this pass

| # | Section | Was | Now | Evidence |
|---|---|---|---|---|
| E1 | 8 (and note on 18.1 C5) | Kernel has "only the buffer mutex"; the third pass removed the "status-request mutex" claim | The kernel has both: `buffer_lock` (writes) and `z53_status_request_lock` (one status request at a time, taken in `kraken3_read_z53`, initialised in `kraken3_probe`) | `nzxt-kraken3.c` lines 99-100, 191-196, 254-284, 947-948 |
| E2 | 13 (suggested tests) | "a 20-byte or shorter report is ignored (WEB-K `MIN_REPORT_LENGTH 20`)" | The kernel ignores reports **shorter than** 20 bytes (`if (size < MIN_REPORT_LENGTH) return 0;`); a 20-byte report is accepted there. RigControl's own threshold (>= 26) is unchanged | `nzxt-kraken3.c` lines 70, 737-738 |
| E3 | 3, 4, 15 (U2) | SET_INTERVAL / FINISH_INIT "Needed: Yes (init)" | "Recommended, not proven required". LHM never sends them, and the author of the 2023/Elite support calls initialisation "actually useless" apart from enabling periodic status. The design rule (send it, re-apply the profile afterwards) is kept | PR #605 comment by brokenmass, 2023-09-01T17:16:49Z; KrakenV3.cs 71-125; `nzxt-kraken3.c` 59-61, 802-818 |
| E4 | 4, 15 (U3) | "the Elite **may** push `75 02` (one capture suggests it)" | "**probably** pushes every 0.5 s once SET_INTERVAL has been sent": the same author says init "instructs the device to send its stats every x millisecond", and the kernel constant comment equates 2000 ms with "four status reports". Still UNVERIFIED for this unit. Also added: the capture contains 23 reads, of which one is the unrequested `75 02` | PR #605 comments (brokenmass 2023-09-01; maxawake 2023-06-14); `nzxt-kraken3.c` line 37 |
| E5 | 12.4 | (a) "RigControl must never stop ... CAM"; (b) the CAM-active state allowed an "optional and user-enabled" `74 01` poll | (a) scoped to "on its own initiative, and never in development or tests": `docs/API.md` defines a user-triggered `cooling.closeCam`. (b) withdrawn: API.md says that while `blockedBy` is non-empty "RigControl does not talk to the devices", so the CAM-active state is zero-write | `docs/API.md`, section "cooling" |
| E6 | 0, 4, 13 (TV19, TV21) | Web facts "as summarised"; TV19 bytes 0..28 and TV21 bytes 0..40 quoted, the rest "reported as zeros by the fetch tool" | Raw text read; all 64 bytes of both reports compared with the log in the PR #605 comment and found identical | GitHub REST API, issue comment by maxawake 2023-06-14T17:41:43Z |
| E7 | 7.1 | "three independent sources agree" on the channel bytes, with the kernel listed twice | The three code bases agree but share one origin (the PR #605 author's reverse engineering; a comment of his quoted in the same thread says LibreHardwareMonitor "integrated my PRs"); kernel bullets merged; one real-Elite user confirmation added | PR #605 body and comments; KrakenV3.cs 39-45; `nzxt-kraken3.c` 354-377 |
| E8 | 7.5 | Non-volatile storage: "No evidence of it; assume not" | "No": the PR author states the pump "will lose all information if it loses power for more than a couple secons"; #128 reports the same for the X63. New row: no command is known that reads the stored curve back | PR #605 comment 2023-09-01T17:22:08Z; issue #128 body |
| E9 | 7.6 | Expected duty `curve[floor(t) - 20]` "+/-1 point of temperature for timing", presented as if the firmware's lookup rule were known | The mapping is tied to U5; the tolerance is spelled out (min..max of the three neighbouring points); new U22 for the meaning of the reported duty | no reference states the firmware's lookup rule |
| E10 | 16 (item 7) | "timeout streak" forces a reopen | Only request timeouts count; an idle read timeout is normal and must not trigger a reopen loop | follows from U3 and from monitor-only mode |
| E11 | 1.1 | "the task brief listed one 2021" | The current brief lists two | task brief |
| E12 | 9.2, 15 (U12) | GET_LCD_INFO evidence: one 2023 capture; SET_LCD_CONFIG: none | Added an Elite on firmware 2.1.0 printing sane LCD info through liquidctl, and the 2023 user reports (brightness worked, orientation failed after init). New design rule: read back after SET_LCD_CONFIG | issue #631 comment by vossiman 2025-05-05; PR #605 comment by guygastineau 2023-08-31 |
| E13 | 4 (step 5) | Read as if liquidctl queried LCD info only when LCD features are on | liquidctl sends `30 01` unconditionally in `KrakenZ3.initialize`; the condition is RigControl's | LQ lines 719-720 |

No packet, offset, constant or test vector changed in this pass.

### 20.2 Additions in this pass

- 1.2: runtime check `GetReportDescriptor().ReportsUseID` (HidSharp.XML) as a fail-closed guard next to the two length checks.
- 5: Elite firmware versions seen in the wild (2.0.0, 2.1.0), with the 2.1.0 `liquidctl initialize` output as real-device evidence for TV1/TV2/TV3/TV11 and the firmware and LCD-info offsets; cooling reported unaffected by the 2.0.1 update on the standard Kraken 2023.
- 6.2: bytes 21-22 and 26 in the Elite sample (informational, still "ignore").
- 7.5: power-loss quotes; "60 % being a good default" for the pump from the PR author; a maintainer's screenshot of the default state that could not be read.
- 8: kernel `reset_resume` re-sends the init pair but not the curves.
- 9.3: what is known about the "(broken)" label (all LCD, firmware 2.x) and two real-Elite reports of the LCD getting stuck after uploads / mode switches.
- 12.4: contract mapping (`blockedBy`, `cooling.closeCam`) and the `CAMService` dead-end with a possible, untested resolution.
- 12.5: LHM calls `GetProductName()` on every VID 0x1E71 device and writes `10 01` to the Kraken as soon as the group is created; the 0.9.6 DLL contains the 2023 channel constants.
- 15: U22, U23 added; U2, U3, U4, U12, U14, U21 reworded.

### 20.3 Items re-derived and confirmed

| Item | How checked | Result |
|---|---|---|
| Reference revisions: liquidctl `292dc56` (commit date 2026-06-16), LibreHardwareMonitor `9e2d677` (2026-11-11), OpenRGB `d09a493` (commit date 2026-11-14) | `git log -1` | Confirmed |
| LQ constants: `_READ_LENGTH 64`, `_WRITE_LENGTH 64`, `_MAX_READ_ATTEMPTS 12`, `_CRITICAL_TEMPERATURE 59`, `_COLOR_CHANNELS_KRAKEN2023 = {}` | kraken3.py 42-44, 74, 85 | Confirmed |
| 0x300C match entry: `_SPEED_CHANNELS_KRAKEN2023`, "NZXT Kraken 2023 Elite (broken)", 2 MB bulk buffer, 640x640 | kraken3.py 573-584 | Confirmed |
| Channel ids: pump `[1,1,0]` 20..100, fan `[2,1,1]` 0..100, giving headers `72 01 01 00` / `72 02 01 01` | kraken3.py 69-72, 379; KrakenV3.cs 43-44; `nzxt-kraken3.c` 63, 354-377 (KRAKEN2023 kind, set for PID 0x300C at 928-931); PR #605 body | Confirmed |
| `_write` zero-pads to 64; byte 0 is the command and is treated as the report number by the test harness (`Report(data[0], data[1:])`) | kraken3.py 475-477; `_testutils.py` 57-60 | Confirmed |
| Kernel write path: `memcpy_and_pad(..., MAX_REPORT_LENGTH, ..., 0x00)` then `hid_hw_output_report(hdev, buffer, 64)` | `nzxt-kraken3.c` 187-198 | Confirmed |
| Kernel dispatches on `report->id` (0x11 firmware, 0x75 status), ignores `size < 20`, parses status identically for sub-codes 01 and 02 | `nzxt-kraken3.c` 732-800 | Confirmed |
| Init: `clear_enqueued_reports`, `70 02 01 B8 <1>`, `70 01` (both outside the hwmon `if/else`, so always sent), `10 01` then read until `11 01` (<= 12 reads), `30 01` then read until `31 01`, `20 03` skipped when there are no colour channels | kraken3.py 696-726 | Confirmed |
| Interval code `1 + round((0.5 - 0.5) / 0.25)` = 1; kernel `set_interval_cmd[] = { 0x70, 0x02, 0x01, 0xB8, 1 }` ("Last byte sets the report interval at 0.5s"), `finish_init_cmd[] = { 0x70, 0x01 }`, both sent in `kraken3_init_device` for every kind and again on `reset_resume` | kraken3.py 709-711; `nzxt-kraken3.c` 59-61, 802-848 | Confirmed |
| Issue #128: Kraken X63, firmware 2.1.0; the byte at index 4 "defines the interval on which the device will send status updates"; `0x0` / `0x1` = 0.5 s, `0xb` = 3 s | raw issue body | Confirmed |
| Firmware reply: header `11 01`, version at offsets 0x11, 0x12, 0x13 | kraken3.py 255-256; KrakenV3.cs 83-84; `NZXTHue2Controller.cpp` 916-929; `nzxt-kraken3.c` 48, 740-744 | Confirmed |
| Status request `74 01`; LQ reads the next report unchecked; LHM loops until `75 01`; kernel waits up to `STATUS_VALIDITY` (2000 ms) for any `0x75` report | kraken3.py 734-747; KrakenV3.cs 206-212; `nzxt-kraken3.c` 37, 64, 252-289 | Confirmed |
| Status offsets: 15/16 temperature (`b15 + b16 / 10`; kernel `b15 * 1000 + b16 * 100` millidegrees), 17-18 pump rpm LE, 19 pump duty, 23-24 fan rpm LE, 25 fan duty; `FF FF` at 15/16 = fault ("is SATA power connected?") | kraken3.py 738-747; KrakenV3.cs 214-238; `nzxt-kraken3.c` 42-52, 754-790; `nzxt-kraken3.rst` 42-44; LQ-doc 38-43 | Confirmed |
| Kernel comment: Z-series "send a sensor report only when requested"; `kraken3_read_z53` "Covers Z53 and KRAKEN2023 device kinds" | `nzxt-kraken3.c` 237-238, 251 | Confirmed |
| Curve: 4-byte header + 40 duty bytes (raw percent) for 20..59 degC, zero-padded; kernel `CUSTOM_CURVE_POINTS 40` ("For temps from 20C to 59C (critical temp)"), `SET_CURVE_DUTY_CMD_LENGTH (4 + 40)` | kraken3.py 375-411; `nzxt-kraken3.c` 38, 71-73, 354-377 | Confirmed |
| Curve keyed to liquid temperature; "fixed [20-59] range"; "should be done in moderation ... they can lock up or discard the changes if they are too numerous at once" | `nzxt-kraken3.rst` 35-40; kraken3.py 383-386 | Confirmed |
| Pump minimum 20 (pump only: `channel == 0 && percent_value < PUMP_DUTY_MIN`), fan 0..100; LHM control ranges 20..100 for both | `nzxt-kraken3.c` 39, 205-219; kraken3.py 70-71; KrakenV3.cs 96, 111; guide 93-96 | Confirmed |
| "TODO adjust min duty value to what the firmware enforces" | kraken3.py 56, 63 (on the X and Z tables; the 2023 table carries no comment) | Confirmed |
| Fixed duty = `[(0, d), (58, d)]` + failsafe, giving 39 x d then 100; kernel "The devices can only control the duty through a curve" and "Force duty to 100% at critical temp" | kraken3.py 413-414; `nzxt-kraken3.c` 379-406 | Confirmed |
| `normalize_profile` (sort key `(x, -y)`, skip equal x, compare with the previous point's original y, stop at the first 100), `interpolate_profile` (lower/upper search, divide then multiply, Python `round`), `clamp` after interpolation | util.py 132-137, 185-246; the `normalize_profile` docstring examples and the first `interpolate_profile` example were reproduced by the script | Confirmed |
| Quirk example `(30,40),(35,30),(40,35)`: liquidctl gives `(30,40),(35,40),(40,35),(59,100)`; running maximum gives `(30,40),(35,40),(40,40),(59,100)`; the running maximum is byte-identical for every test vector | script | Confirmed |
| `time.sleep(0.2)` exists only in `_set_speed_profile_hwmon` | kraken3.py 356-373 | Confirmed |
| Test assertions: `number == 0x72`, `data[3:43]` (= offsets 4..43) equals `[84]*39 + [100]`, `[50]*39 + [100]`, `test_curve_final_pwm` | test_kraken3.py 39-80, 419-424, 483-488 | Confirmed |
| TV1-TV13, TV20 | Rebuilt from scratch, byte-compared with this file | All byte-identical |
| TV7 values: 31 degC = 57.5 rounds to 58, 33 degC = 72.5 rounds to 72 (round half to even); TV10 decimal list | script | Confirmed |
| TV14 = `Z3_SAMPLE_STATUS`, TV15 = `X3_FAULTY_STATUS` | hex fixtures, test_kraken3.py 29-37 | Byte-identical |
| TV19 and TV21 = the Elite log lines in PR #605 (all 64 bytes each) | raw comment text | Byte-identical |
| Decodes: TV14 24.3 / 873 / 20 / 0 / 0; TV15 fault / 2764 / 100; TV16 31.7 / 2010 / 60 / 1125 / 45; TV17 "2.0.1"; TV18 50 / 0; TV19 30.2 / 1298 / 30 / 1020 / 50; TV21 100 / 3 (270 deg) | script | Confirmed |
| Offset 14 = `01` in all status samples; byte 20 = byte 19 in all status samples | fixtures and TV19 | Confirmed |
| PR #605 Elite log: header "device: NZXT Kraken 2023 Elite"; writes `30 01`, `36 03`, sixteen `30 04 nn`, three `32 02 nn`, one `32 01 ..`; no `70` or `74`; unrequested `75 02` right after the `36 03` write; `37 03` one read late; offsets 2..13 identical in every reply | raw comment text | Confirmed |
| PR #605 body (29 May 2023): fan "byte 3 and 4 must be `0x1 0x1`", pump `0x1 0x0` "(but even `0x0 0x0`) works" | raw PR body | Confirmed |
| PR #605: "default is silent mode (pump at 30% fixed speed)" (brokenmass, 1 Sep 2023) answering a question that names no model; the "constantly rendered and sent by cam every x ms" remark is about the non-Elite Kraken 2023 | raw comments | Confirmed |
| Issue #627: PID 300c, bcdDevice 1.02, interface 0 vendor-specific with bulk OUT 0x02 (512), interface 1 HID with interrupt IN 0x81 / OUT 0x01 (64 each), `wDescriptorLength 360`, report descriptor "UNAVAILABLE"; status 33.5 degC / 1357 rpm / 30 % / 0 rpm / 30 %; "Setting GIFs and static images on the screen both work"; closed 2024-07-22 | raw issue body and comments | Confirmed |
| LCD info at 0x18 / 0x1A; set packet `30 02 01 <b> 00 00 01 <o>`; brightness 0..100; orientation {0, 90, 180, 270} divided by 90; read-modify-write | kraken3.py 728-732, 802-828; test_krakenz3_response.py 4-5; test_kraken3.py 130-133 | Confirmed |
| LQ-doc numbering: requests 1-based ("Byte 2-4" channel, "Byte 5-44" curve, "Byte 3/4/7/8" LCD set); firmware "Byte 3/4/5" wrong; LCD brightness "Byte 18" = 0x18; orientation "Byte 26" decimal; "20 to 60degree"; "almost always set as a flat curve" | `nzxt_x3-z3-2023.md` 12-14, 38-43, 50-55, 69-70, 77-80 | Confirmed |
| LCD upload opcodes and bulk header (listed only to forbid them): `36 03`, `30 04 nn`, `32 02`, `32 01`, `36 01`, `36 02`, `38 01 <mode> <bucket>`, `12 FA 01 E8 AB CD EF 98 76 54 32 10`, bulk endpoint 0x02; the firmware-2 path is selected only for PID 0x300E | kraken3.py 773-777, 808-813, 945-1180 | Confirmed |
| OpenRGB: no 0x300C anywhere; `NZXTHue2Devices.h` 24-45 PID list (0x2011 = `NZXT_RGB_FAN_CONTROLLER4_PID`, 0x2021 = `NZXT_RGB_CONTROLLER_2_PID`, 0x3012, 0x3014); 0x3012 64-byte and 0x3014 512-byte packets; `SendFan` = `62 01`; `SendFirmwareRequest` `10 01` then `11 01`, version at 0x11-0x13; old Kraken detector = 0x170E / 0x1715 only | files at the cited lines | Confirmed |
| LHM: `case 0x300C` "NZXT Kraken Elite", firmware label "1.2.4", 60-byte fill (offsets 4..63), defaults 60 % / 40 %, `Undefined` mode writes nothing, resend-until-equal loop with the "sets itself to 0%" comment, `ReadTimeout = 5000`, 2-byte writes for `10 01` / `74 01`, no `70` init; `NzxtGroup` line 42; `IsControllerEnabled` adds `NzxtGroup` | KrakenV3.cs; NzxtGroup.cs; Control.cs 33-45; Computer.cs 118, 131, 142, 554 | Confirmed |
| LibreHardwareMonitorLib 0.9.6: runtime DLLs contain "NZXT Kraken Elite", "NZXT Kraken Elite v2", "NZXT Kraken Z3", "1.2.4"; `ref/` stubs do not | byte search | Confirmed |
| HidSharp 2.6.4 docs: max report lengths include the Report ID byte ("If the device does not use Report IDs, the first byte will always be 0"); `Write` "Place the Report ID in the first byte"; `ReadTimeout` default 3000 ms; `OpenOption.Exclusive` = "HIDSharp's exclusivity layer" | HidSharp.XML (netstandard2.0) | Confirmed |
| Guide quotes at lines 8, 10, 29, 44, 81 and the duty table at 93-96 | guide file | Confirmed |
| liquidctl README: "NZXT Kraken 2023 Standard, Elite" note `p` ("Only partially supported"); `B` = "Broken in at least one significant way" | README.md 136, 169, 175 | Confirmed |

### 20.4 Still open, and points for the lead

All U-items in section 15 remain open; none can be closed without opening the device. The safety-relevant ones are unchanged: U1 (report length and report IDs; fail closed), U6 (never below 20 % pump), U8 / U9 (the device keeps the last curve only while powered; the power-up default is probably a fixed 30 % pump), U15 / U17 (CAM coexistence), plus the new U22 (meaning of the reported duty, which the write verification relies on).

Points that are about the UI contract rather than the protocol (reported, not changed here):

1. `cooling.closeCam` versus `CAMService`: with the conservative rule in 12.4 (a running `CAMService` counts as "CAM active"), closing the CAM app does not unblock RigControl on the development PC, because the service is Auto-start, elevated and keeps running. Either `blockedBy` has to be defined on the CAM app processes only, or the untested passive probe in 12.4 has to be accepted, or the UI must tell the user to disable the service.
2. `blockedBy` says RigControl "does not talk to the devices". Whether opening the HID handle and only listening (no writes) is allowed in that state is not defined. This spec now assumes zero writes and leaves listening as an option for the lead.
3. `CoolingChannel.mode` / `curve` cannot be read from the device (no read-back command is known); after a start they are only known once RigControl has written its own profile. The 59 degC point is always sent as 100 %, whatever the user's last point says.

## 17. Addendum (2026-11-15): LCD content is now in scope

At the user's request, section 9.3's decision ("LCD image upload ... out of scope") is reversed for user-triggered actions only.
Implemented in `src/RigControl.Cooling/Lcd.cs`, with its own allow-list (`KrakenLcdProtocol.IsAllowed`):
- Stored GIF / picture: liquidctl's `_send_data` sequence (asset type `01`), as in section 9.3.
- Live frames (infographics): the sequence captured from NZXT CAM on PID 0x300C by the MIT-licensed project
  KhalloufHassan/kraken-elite-screen-manager: one-time handshake (`10 02`, `70 02 01 B8 0B`, `74 01`, `36 04`, `30 01`, `36 03`,
  `30 02 00 00 00 00 1E`, `38 01 02`, delete 16 buckets, `30 02 01 <brightness> 00 00 00 1E`), then per frame `36 01 00 01 09`,
  bulk header + 1,228,800 bytes of 640x640 B,G,R in 245,760-byte transfers, `36 02`; every HID command is acknowledged before continuing.
- Still true: nothing is sent while CAM runs; the WinUSB interface is opened only while sending and released when CAM starts.
- **UNVERIFIED on this unit.** The risks recorded in 9.3 (screen stuck until power-off) still apply.

# NZXT controllers 1E71:2011 and 1E71:2021 — fan control and status protocol

> **About this file.** These are research notes written while RigControl was being developed, before and during the
> first builds. They describe the protocol and what was observed on the one development PC at that time. Where they
> say how RigControl should behave, the code and [docs/API.md](../API.md) are what counts today.

Spec for RigControl (C#, Windows 11, one specific PC). Written from the reference
clones only; nothing was built, run, or sent to any device. Read-only PnP
enumeration of the development PC was used for section 1.2.

Legend used throughout:

- **CONFIRMED** — stated in reference source (file and line given).
- **CROSS-CHECKED** — additionally matches a second independent source.
- **INFERRED** — derived by the spec author from confirmed facts; reasoning given.
- **UNVERIFIED** — could not be confirmed in source; what would confirm it is stated.

All offsets are zero-based into the 64-byte report **including** the report ID
byte at offset 0. All multi-byte integers are little-endian. There are no
checksums anywhere in this protocol. All unused bytes are 0x00.

---

## 0. Summary for the impatient

| Question | Answer |
| --- | --- |
| What is 1E71:2011? | NZXT RGB & Fan Controller, 2022 "3+6 channel" variant: **3 fan channels**, 6 LED channels. Fan control and status are supported. |
| What is 1E71:2021? | NZXT "2023 RGB Controller" (USB product string "NZXT RGB Controller"): **RGB only, 0 fan channels**, 3 LED channels. There is nothing for RigControl's fan code to do with it. |
| How many are in the development PC? | One 2011 and **two** 2021 (three NZXT HID devices besides the Kraken). |
| Transport | One vendor HID collection (usage page 0xFF00, usage 0x0001), 64-byte numbered output and input reports, no feature reports. |
| Fan duty command | `62 01 <mask> <d1> <d2> <d3>` zero-padded to 64 bytes, duty 0..100 percent. |
| Status | Unsolicited input report `67 02 ...` every update interval: fan type at 16+i, rpm LE16 at 24+2i, duty at 40+i, noise at 56. |
| Does duty persist if the app exits? | Documented yes for the HUE 2 / Smart Device V2 family ("persists as long as the device still gets power", liquidctl `docs/nzxt-hue2-guide.md` line 8); not specifically verified on 0x2011 fw 1.7.0. No reference documents a watchdog or failsafe. |
| Admin rights / drivers | None needed. Stock `HidUsb` driver, unprivileged access (liquidctl `README.md` lines 380-381; no "Z" driver-replacement flag for either PID, lines 155-156). No kernel driver, so HVCI is irrelevant. |
| Biggest hazards | (a) A set channel-mask bit with a zero duty byte **stops that fan**. (b) NZXT CAM writes the same registers; last writer wins. (c) `60 03` (detect fans) **resets the duty values** (Linux kernel driver comment, web) — never send it while another program is controlling the fans, and re-apply duty after it. |

---

## 1. Device identification

### 1.1 Product tables in the references

| PID | liquidctl name | liquidctl fan / LED channels | OpenRGB name | OpenRGB fan / RGB channels | Linux `nzxt-smart2` |
| --- | --- | --- | --- | --- | --- |
| 0x2011 | "NZXT RGB & Fan Controller (3+6 channels)", class `SmartDevice2` | 3 / 0 (lighting deliberately disabled: "protocol changed, see #541") | "NZXT RGB & Fan Controller" (`NZXT_RGB_FAN_CONTROLLER4_PID`) | 3 / 6, packet size 64, protocol LEGACY | listed: "NZXT RGB & Fan Controller (6 RGB)" |
| 0x2021 | "NZXT 2023 RGB Controller", class `Nzxt2023RgbController` | **0** / 3 | "NZXT RGB Controller" (`NZXT_RGB_CONTROLLER_2_PID`) | **0** / 3, packet size 64, protocol LEGACY | not listed |

Sources:

- liquidctl `liquidctl/driver/smart_device.py` lines 431-434 (0x2011), 705-715 (0x2021). CONFIRMED.
- OpenRGB `Controllers/NZXTHue2Controller/NZXTHue2Devices.h` lines 36 and 43; `NZXTHue2Devices.cpp` lines 328-343 (`nzxt_rgb_fan_controller4_device`: 6 RGB, 3 fan) and 447-462 (`nzxt_rgb_controller_2_device`: 3 RGB, 0 fan); `NZXTHue2ControllerDetect.cpp` lines 53 and 68. CONFIRMED.
- Linux kernel `drivers/hwmon/nzxt-smart2.c`, `nzxt_smart2_hid_id_table` (web cross-check, see section 16). CROSS-CHECKED for 0x2011.
- liquidctl `docs/nzxt-hue2-guide.md` lines 42-52: the 3+6 controller is "only partially supported ... only monitoring and fan control"; the 2023 RGB Controller "is a controller for RGB **only**, i.e., the fan speed is controlled externally", shipped with the Kraken 2023 and 3-fan sets, up to three F120/F140 RGB fans. CONFIRMED.

**0x2021 is an RGB-only controller with no fan channels.** In liquidctl its
`set_fixed_speed` and `_write_fixed_duty` raise `NotSupportedByDevice`
(`smart_device.py` lines 861-865) and its `get_status` returns only a channel
count (lines 806-814). RigControl must not create fan sensors or fan controls
for it. The fans whose LEDs plug into a 2021 get their PWM/tach from somewhere
else (the 2011's fan channels, the Kraken, or motherboard headers) — which one
is a wiring fact of the development PC that the references cannot tell us.

LibreHardwareMonitor does not support either PID: `NzxtGroup.cs` (lines 22-73)
handles only 0x170E, 0x2007, 0x2014, 0x3008, 0x300C, 0x300E, 0x3012, 0x1711 and
logs anything else as "Unknown Hardware PID". Note it still calls
`dev.GetProductName()` on every 1E71 HID device when controllers are enabled.

### 1.2 What was present on the development PC (read-only PnP enumeration, 2026-11-15; serial numbers removed)

| USB instance | Bus-reported name | bcdDevice | HID child hardware IDs |
| --- | --- | --- | --- |
| `USB\VID_1E71&PID_2011\<24-hex serial>` | NZXT RGB & Fan Controller | REV_0107 | `HID\VID_1E71&PID_2011&REV_0107`, `HID_DEVICE_UP:FF00_U:0001` |
| `USB\VID_1E71&PID_2021\<32-hex serial A>` | NZXT RGB Controller | REV_0105 | `HID\VID_1E71&PID_2021&REV_0105`, `HID_DEVICE_UP:FF00_U:0001` |
| `USB\VID_1E71&PID_2021\<32-hex serial B>` | NZXT RGB Controller | REV_0105 | `HID\VID_1E71&PID_2021&REV_0105`, `HID_DEVICE_UP:FF00_U:0001` |

Observations (CONFIRMED on the development PC by `Get-PnpDevice` / `Get-PnpDeviceProperty`):

- Each device is a plain (non-composite) USB HID device bound to `HidUsb`: no `&MI_xx` suffix, and exactly one HID child with no `&Col0x` suffix, i.e. **one interface, one top-level collection**.
- Top-level collection: **usage page 0xFF00, usage 0x0001** (vendor defined).
- There are **two** 2021 units. They can only be told apart by USB serial number string (the 32-hex-digit strings above). Use the serial, not the enumeration order.
- All three (and the Kraken Elite) hang off the same USB 2.0 hub `USB\VID_05E3&PID_0608`.
- `REV_0107` on the 2011 matches liquidctl issue #541, where a 1E71:2011 with release number 0x0107 reported firmware "1.7.0". `REV_0105` on the 2021 matches liquidctl PR #690, where the 2023 RGB Controller reported firmware "1.5.0". INFERRED: bcdDevice 0xMMmm equals firmware major.minor, so firmware can be displayed without any HID I/O. UNVERIFIED as a general rule (two data points); confirmed by comparing with the `11 01` reply of section 6.3.

---

## 2. HID transport on Windows

| Item | Value | Status |
| --- | --- | --- |
| VID / PID | 0x1E71 / 0x2011 (fan + RGB), 0x1E71 / 0x2021 (RGB only) | CONFIRMED |
| Interface | the only interface (no MI_xx) | CONFIRMED on the development PC |
| Top-level collection | usage page 0xFF00, usage 0x0001; the only collection | CONFIRMED on the development PC |
| Output report length | 64 bytes total, byte 0 = report ID | CONFIRMED in source (see below) |
| Input report length | 64 bytes total, byte 0 = report ID | CONFIRMED in source |
| Report IDs used | yes — the first byte of every packet (0x10, 0x20, 0x60, 0x62, 0x11, 0x21, 0x61, 0x67 ...) is the HID report ID | INFERRED, see below |
| Report type for commands | **output** reports (interrupt OUT / `WriteFile` / `HidStream.Write`) | CONFIRMED |
| Report type for replies and status | **input** reports (`ReadFile` / `HidStream.Read`) | CONFIRMED |
| Feature reports | none used by any reference | CONFIRMED (absence) |
| Checksums / sequence numbers | none | CONFIRMED (absence) |
| Padding | zero-fill to 64 bytes | CONFIRMED |
| Administrator rights | not required; Windows allows unprivileged access to these HID devices | CONFIRMED for liquidctl's use: `README.md` lines 380-381 ("Access permissions are not a concern on ... Windows"); no "Z" (driver replacement) note on README lines 155-156. On the development PC both PIDs are bound to `HidUsb` (section 1.2). |
| Driver install / HVCI | none; user-mode HID only, no kernel driver, no WinRing0/inpout | CONFIRMED by design of all three references |

Evidence:

- liquidctl `SmartDevice2._READ_LENGTH = 64`, `_WRITE_LENGTH = 64` (`smart_device.py` lines 450-451); `_write` zero-pads to `_WRITE_LENGTH` and calls the HID `write` (lines 191-193). liquidctl's HID `write` uses hidraw semantics: "first byte of the buffer ... should be set to the report number" (`liquidctl/driver/usb.py` lines 468-485). The same constants apply to `Nzxt2023RgbController` (lines 717-719).
- OpenRGB uses `packet_size = NZXT_HUE_2_PACKET_SIZE_64` for both PIDs and calls `hid_write(dev, usb_buf, packet_size)` with the command byte in `usb_buf[0]` (`NZXTHue2Controller.cpp` lines 158-167, 916-919).
- Linux `nzxt-smart2.c`: `OUTPUT_REPORT_SIZE 64`, structs start with `u8 report_id`, enums are named `OUTPUT_REPORT_ID_INIT_COMMAND = 0x60`, `OUTPUT_REPORT_ID_SET_FAN_SPEED = 0x62`, `INPUT_REPORT_ID_FAN_CONFIG = 0x61`, `INPUT_REPORT_ID_FAN_STATUS = 0x67`.

Why "numbered reports" is INFERRED and not CONFIRMED: no reference contains
the HID report descriptor of 0x2011 or 0x2021. The inference is that OpenRGB
writes exactly 64 bytes with a non-zero first byte through hidapi on Windows
and this family works there; on a device without report IDs Windows would
require a 65-byte buffer with a leading 0x00.

**Required runtime check (UNVERIFIED until run):** before the first write, read
the lengths from the HID caps (HidSharp: `HidDevice.GetMaxOutputReportLength()`
and `GetMaxInputReportLength()`; Win32: `HidP_GetCaps` `OutputReportByteLength`
/ `InputReportByteLength`).

| Caps result | Action |
| --- | --- |
| output 64, input 64 | Expected. Send the 64-byte packets of this spec unchanged; received byte 0 is the report ID. |
| output 65, input 65 | Device does not use report IDs (not expected). Prefix every packet with 0x00 on write and strip the first byte on read. Log a warning. |
| anything else | Refuse to write to the device; monitoring only if 0x67 parsing validates (section 6.1). |

Open the device **non-exclusively** (shared read and write) so NZXT CAM and
OpenRGB keep working. HidSharp 2.6.4 facts from its shipped XML documentation
(`hidsharp/2.6.4/lib/netstandard2.0/HidSharp.XML`):

| HidSharp member | Documented behaviour | Status |
| --- | --- | --- |
| `OpenOption.Exclusive` | HidSharp's own inter-process exclusivity layer; "Defaults to false" | CONFIRMED (XML line 583-591). Do not set it. |
| `DeviceStream.ReadTimeout` / `WriteTimeout` | default 3000 ms each; `Timeout.Infinite` disables | CONFIRMED (XML lines 268-282). Never set Infinite. |
| `HidDevice.GetMaxInputReportLength()` / `GetMaxOutputReportLength()` | length **including** the report ID byte; "If the device does not use Report IDs, the first byte will always be 0" | CONFIRMED (XML lines 333-344) |
| `HidStream.Write(byte[])` | "Place the Report ID in the first byte" | CONFIRMED (XML line 490-495) |
| `HidDevice.ReleaseNumberBcd`, `GetSerialNumber()`, `GetRawReportDescriptor()` | exist | CONFIRMED (XML) |
| Win32 share mode HidSharp passes to `CreateFile` | not documented | UNVERIFIED — confirm by opening while CAM runs (section 15 #11/#13) |
| Exception type on read timeout | not documented in the XML | UNVERIFIED — treat any `TimeoutException` / `IOException` from `Read` as "no report yet" |

If the open fails with a sharing violation, another program holds the device
exclusively: report "in use", do not retry in a tight loop.

Device matching rule for C#: VID 0x1E71, PID 0x2011, usage page 0xFF00 (there is
only one collection, so matching on VID/PID alone is sufficient). For 0x2021
match VID/PID and key each unit by its serial number string.

---

## 3. Report catalogue (complete allow-list for RigControl)

Only these report IDs may ever be written by RigControl. Everything else is
forbidden (section 13).

| Dir | Bytes 0-1 | Name | Applies to | Needed by RigControl | Source |
| --- | --- | --- | --- | --- | --- |
| OUT | `60 03` | Detect fans / start fan reporting | 2011 only | Active mode init | `smart_device.py` line 536; kernel `INIT_COMMAND_DETECT_FANS` |
| OUT | `60 02` | Set status update interval | 2011 only | Active mode init | `smart_device.py` lines 534-535; kernel `set_update_interval` |
| OUT | `62 01` | Set fan duty | 2011 only | Fan control | `smart_device.py` lines 666-669; OpenRGB `SendFan`; kernel `set_fan_speed_report` |
| OUT | `10 01` | Request firmware info | 2011, 2021 | Optional | `smart_device.py` lines 539, 782; OpenRGB `SendFirmwareRequest` |
| OUT | `20 03` | Request lighting/accessory info | 2011, 2021 | **Not recommended** (section 8.2) | `smart_device.py` lines 540, 783; OpenRGB `UpdateDeviceList` |
| IN | `67 02` | Fan status: type, rpm, duty, noise | 2011 | Monitoring | `smart_device.py` lines 567-583; kernel `fan_status_report` |
| IN | `67 04` | Fan status: voltage, current | 2011 | Optional monitoring | kernel `FAN_STATUS_REPORT_VOLTAGE` (web only) |
| IN | `61 03` | Fan configuration (reply to `60 03`) | 2011 | Optional | kernel `fan_config_report` (web only) |
| IN | `11 01` | Firmware info reply | 2011, 2021 | Optional | `smart_device.py` lines 543-545, 558 |
| IN | `21 03` | Lighting/accessory info reply | 2011, 2021 | Ignore (or parse passively) | `smart_device.py` lines 547-556, 560 |

Any other input report ID must be silently ignored (other programs' traffic
produces replies that every open handle receives, see section 12).

---

## 4. Initialisation / detect sequence (0x2011 only)

Two reference orderings exist; both are known to work on this family.

| Step | liquidctl `SmartDevice2.initialize` (lines 523-562) | Linux `init_device` |
| --- | --- | --- |
| 1 | discard already-queued input reports | `60 03` detect fans |
| 2 | `60 02 01 E8 01 01 E8 01` (interval byte 0x01, about 0.5 s) | `60 02 01 E8 03 01 E8 03` (interval byte 0x03, 1000 ms default) |
| 3 | `60 03` | — |
| 4 | `10 01` firmware request | — |
| 5 | `20 03` lighting info request | — |
| 6 | read up to 12 input reports until `11 01` (and `21 03` when lighting is enabled) have both been seen | fan config and status arrive asynchronously |

The liquidctl unit test asserts exactly 4 writes during initialise for a
3-fan device (`tests/test_smart_device2.py` lines 45-57). For 0x2011 liquidctl
has lighting disabled, so it still sends `20 03` but waits only for `11 01`
(`smart_device.py` lines 558-561).

**Recommended RigControl sequence (active mode, see section 12 for when to use it):**

1. Open the HID device (shared). Check report lengths (section 2).
2. Listen passively for up to 3 seconds. If a valid `67 02` arrives, reporting is already running (CAM or an earlier session initialised it) — **skip steps 3-4**.
3. Write `60 03` (test vector TV2).
4. Write `60 02 01 E8 03 01 E8 03` (TV7, 1 s interval — the kernel driver's default, and the init string quoted in liquidctl issue #128 where 0x03 is called the default; that this is what NZXT CAM sends is UNVERIFIED).
5. Optionally write `10 01` (TV3) and wait for `11 01`.
6. Do **not** write `20 03` (section 8.2).
7. Wait for the first valid `67 02` (allow 3 intervals). Only after that enable duty writes.
8. If step 3 was executed, immediately write the intended duty for every channel whose type byte is non-zero (one `62 01` packet per channel). Reason: the Linux driver comment in `handle_fan_status_report` says "fan detection resets pwm values" (web, kernel `nzxt-smart2.c`). The value the device resets to is UNVERIFIED (the only sample, TV8, shows 40 percent on unpopulated channels, which is suggestive but not proof).

The kernel driver also ignores every `67` report until it has received the
`61 03` fan-configuration reply ("Only accept other data after getting 0x61,
to make sure that fan detection is complete", web). RigControl should do the
same after it sends `60 03` itself: ignore `67 02` duty values until a `61 03`
has been seen or 2 intervals have passed.

When to re-run: at every boot, after resume from sleep, and after the device
re-enumerates. liquidctl's docstring says the device must be re-initialised
"every time the systems boots, resumes from a suspended state, or if the device
has just been (re)connected" (`smart_device.py` lines 512-517); the kernel
driver re-runs `init_device` in its `reset_resume` handler. Hot-plugged fans
are not controllable until `60 03` is sent again (kernel documentation
`nzxt-smart2.rst`, web).

UNVERIFIED: whether status reports stop entirely without `60 02`/`60 03` after
a cold power-on of fw 1.7.0. The liquidctl module docstring says initialisation
is needed to "enable status updates" (lines 106-109), which is why step 2 has a
timeout fallback instead of an assumption.

---

## 5. Commands (output reports), byte by byte

Every command is exactly 64 bytes: the listed bytes followed by 0x00 padding.

### 5.1 `60 03` — detect fans

| Offset | Length | Value | Meaning |
| --- | --- | --- | --- |
| 0 | 1 | 0x60 | report ID (init command) |
| 1 | 1 | 0x03 | sub-command: detect fans |
| 2 | 62 | 0x00 | padding |

Effect: the device answers with a `61 03` report carrying the detected fan
type per channel (kernel comment on `fan_config_report`: sent "in response to
'detect fans' command"; the type is "as detected by the device"). That the
device chooses DC or PWM per channel by itself is stated in liquidctl's module
docstring (`smart_device.py` lines 42-44). **Side effect: detection resets the
duty values** (kernel comment "fan detection resets pwm values", web) — see
section 4 step 8. liquidctl annotates the pair `60 02` + `60 03` as
"initialize fan reporting" (line 525).

### 5.2 `60 02` — set status update interval

| Offset | Length | Value | Meaning |
| --- | --- | --- | --- |
| 0 | 1 | 0x60 | report ID |
| 1 | 1 | 0x02 | sub-command: set update interval |
| 2 | 1 | 0x01 | constant, meaning unknown |
| 3 | 1 | 0xE8 | constant, meaning unknown |
| 4 | 1 | N | interval control byte |
| 5 | 1 | 0x01 | constant, meaning unknown |
| 6 | 1 | 0xE8 | constant, meaning unknown |
| 7 | 1 | N | interval control byte (same value again) |
| 8 | 56 | 0x00 | padding |

Interval mapping (the two sources agree to within measurement error):

| N | liquidctl issue #128 measurement | Linux kernel table |
| --- | --- | --- |
| 0x00 | 0.25 s | 0.25 s |
| 0x01 | 0.50 s | 0.488 s |
| 0x02 | 0.75 s | 0.744 s |
| 0x03 | 1.00 s | 1.00 s |
| 0x07 | — | 2.02 s |
| 0xFF | — | 65.5 s |

- liquidctl formula: `N = 1 + round((seconds - 0.5) / 0.25)`, evaluated with 0.5 s giving N = 1 (`smart_device.py` line 534).
- Kernel formula: N = 0 if ms <= 250, else `clamp(1 + round((ms - 488) / 256), 0, 255)`; inverse `ms = 250` for N = 0, else `488 + (N - 1) * 256`.
- RigControl must only use N = 0x03 (1 s) or N = 0x01 (0.5 s). Both intervals bytes must be equal. The interval is a **device-global** setting shared with every other program talking to the device.
- The two `01 E8` constants are never varied by any reference. Never send other values. (0x01E8 = 488 decimal big-endian, which equals the kernel's base interval in ms — a coincidence worth noting, meaning UNVERIFIED.)

### 5.3 `62 01` — set fan duty

| Offset | Length | Value | Meaning |
| --- | --- | --- | --- |
| 0 | 1 | 0x62 | report ID (set fan speed) |
| 1 | 1 | 0x01 | constant ("magic", must be 0x01) |
| 2 | 1 | mask | channel bit mask: bit 0 = fan 1, bit 1 = fan 2, bit 2 = fan 3 |
| 3 | 1 | duty1 | duty percent for fan 1, used only if mask bit 0 is set |
| 4 | 1 | duty2 | duty percent for fan 2, used only if mask bit 1 is set |
| 5 | 1 | duty3 | duty percent for fan 3, used only if mask bit 2 is set |
| 6 | 5 | 0x00 | duty slots for channels 4-8 (reserved; the kernel struct has 8 slots, the device has 3 channels) |
| 11 | 53 | 0x00 | padding |

Sources: liquidctl `_write_fixed_duty` (`smart_device.py` lines 666-669): message
`[0x62, 0x01, 0x01 << cid, 0x00, 0x00, 0x00]` with `msg[cid + 3] = duty`.
OpenRGB `SendFan` (`NZXTHue2Controller.cpp` lines 158-161): identical layout.
Kernel `struct set_fan_speed_report`: identical layout, 8 duty slots. CROSS-CHECKED (three implementations).

Rules:

- Channel index `cid` is 0, 1, 2 for fan1, fan2, fan3 (`smart_device.py` lines 501-502).
- Duty range is **0..100 decimal** (`_MIN_DUTY = 0`, `_MAX_DUTY = 100`, lines 133-134; liquidctl clamps before sending, line 184). Never send a value above 100 (0x64); device behaviour is UNVERIFIED.
- **Duty 0 with the mask bit set turns the fan off completely, in both PWM and DC mode** (kernel comment on `duty_percent`). A mask bit that is set while its duty byte is left at 0x00 therefore stops that fan. Build the packet so that only the bit for the channel being changed is set.
- All three references set exactly **one** mask bit per packet. The kernel comment says the report sets duty "for one or more channels", so multi-bit masks are plausible, but no reference ever sends one: treat multi-channel packets as UNVERIFIED and send one packet per channel.
- For voltage-controlled (DC, 3-pin) fans the lowest non-zero setting (duty 1) is about 9 V (kernel comment); the usable range is therefore compressed.
- The device accepts the write even when no fan is connected on that channel (kernel `set_pwm` comment: "the device seems to always accept pwm values - even when there is no fan connected").
- No acknowledgement report is documented; the kernel updates its cached value "without waiting". Confirming a write by reading the duty byte of a later `67 02` report (within 2 update intervals) is **spec-author policy**, supported by the fact that the kernel reads its `pwmN` value back from `duty_percent` of `67 02`. OpenRGB does one blocking `hid_read` after the write (line 167) but ignores its content; do not copy that — an untimed blocking read can hang.
- The kernel scales 0..255 to percent so that any positive input never rounds to 0 ("0 completely turns off the fan"). RigControl's percent API must likewise never turn a non-zero request into 0.
- Fans that were not detected at the last `60 03` ignore duty writes (kernel documentation: "new fans can't be controlled (`pwm*` changes will be ignored)").
- "sync" in liquidctl is just a loop over the three channels, one packet each (`set_fixed_speed`, lines 176-186).

### 5.4 `10 01` — request firmware info

| Offset | Length | Value |
| --- | --- | --- |
| 0 | 1 | 0x10 |
| 1 | 1 | 0x01 |
| 2 | 62 | 0x00 |

Reply: input report `11 01` (section 6.3). Sources: `smart_device.py` line 539
(SmartDevice2) and line 782 (2023 RGB controller); OpenRGB `SendFirmwareRequest`
lines 916-929. Note the NZXT Control Hub (0x2022, not in the development PC) uses `10 02`
/ `11 02` instead (`control_hub.py` lines 133, 151) — do not generalise.

### 5.5 `20 03` — request lighting / accessory info (documented for completeness)

| Offset | Length | Value |
| --- | --- | --- |
| 0 | 1 | 0x20 |
| 1 | 1 | 0x03 |
| 2 | 62 | 0x00 |

Reply: input report `21 03` (section 6.4). See section 8.2 for why RigControl
should leave this to OpenRGB.

---

## 6. Input report parsing

General rules for the read loop:

1. Each read returns one 64-byte report; byte 0 is the report ID.
2. Dispatch on bytes 0 and 1 together. Ignore every pair not listed in section 3.
3. Bytes 2..13 (12 bytes) are a device-constant block that appears in every report type (samples: `3A 00 3F 00 18 57 32 53 32 30 31 20` for a Smart Device V2, `<12 device-constant bytes>` for a 0x2011). INFERRED to be a hardware unique ID; the kernel calls bytes 2..15 "unknown static data". Do not interpret it. UNVERIFIED: whether it equals the 12-byte USB serial string of the 2011 (`<24-hex serial>`); a first live capture would show it.
4. Never assume a reply directly follows a request: periodic `67` reports and replies triggered by other programs are interleaved. liquidctl reads up to 12 reports looking for the wanted prefix (`_MAX_READ_ATTEMPTS = 12`, `_read_until`, lines 449 and 618-627) with a 5000 ms per-read timeout (`usb.py` line 83).

### 6.1 `67 02` — fan speed status (the main report)

| Offset | Length | Type | Field | Notes |
| --- | --- | --- | --- | --- |
| 0 | 1 | u8 | 0x67 | report ID |
| 1 | 1 | u8 | 0x02 | status type: speed |
| 2 | 12 | bytes | device-constant block | ignore |
| 14 | 1 | u8 | 0x03 in the only sample | kernel: "seems to be the number of fan channels, but I am not sure" |
| 15 | 1 | u8 | 0x00 in the only sample | unknown |
| 16 | 8 | u8[8] | fan type per channel | 0 = none / not detected, 1 = DC, 2 = PWM; index i = fan i+1; only 0..2 used |
| 24 | 16 | u16le[8] | fan speed in rpm | channel i at offsets 24+2i (low) and 25+2i (high); 0 when nothing connected |
| 40 | 8 | u8[8] | duty percent 0..100 | channel i at 40+i; "non-zero even for channels without fans connected" (kernel) |
| 48 | 8 | u8[8] | duplicate of the duty array | same values (kernel `duty_percent_dup`) |
| 56 | 1 | u8 | noise level, dB | onboard microphone "Case Noise" |
| 57 | 7 | — | not described by any reference | ignore |

Sources: liquidctl `parse_fan_info` (`smart_device.py` lines 567-580):
`mode_offset = 16`, `rpm_offset = 24` (+2 per channel, `msg[off+1] << 8 | msg[off]`),
`duty_offset = 40`, `noise_offset = 56`, `raw_modes = [None, 'DC', 'PWM']`.
OpenRGB (disabled code, `NZXTHue2Controller.cpp` lines 362-376): header
`67 02`, duty at `40 + i`, rpm at `24 + 2i` / `25 + 2i`. Kernel
`struct fan_status_report`. CROSS-CHECKED (three implementations).

Validation before accepting a `67 02` report (spec-author policy):

- length is 64 (at least 57), byte 0 = 0x67, byte 1 = 0x02;
- each of bytes 16..18 is 0, 1 or 2;
- each of bytes 40..42 is <= 100.

Soft check (log a warning, do **not** reject): bytes 40..42 equal bytes 48..50.
The kernel names 48..55 `duty_percent_dup`, but equality is seen in only one
sample (TV8), so a mismatch is not proof of a bad layout.

If any hard check fails, drop the report and count it; after 10 consecutive failures
mark the device "layout mismatch" and disable duty writes. This matters because
sibling products use the same ID with a different layout (H1 V2 uses `75 02`;
the Control Hub capture shows `67 01`; see `smart_device.py` lines 683-701 and
`tests/test_control_hub.py` lines 21-25, 69-73).

Honest gap: the references contain **no captured `67 02` report from a
0x2011**. The layout is confirmed for the family (sample from a Smart Device
V2, section 14 TV8) and both liquidctl and the kernel driver apply the same
parser to 0x2011. First-run validation on the real device is therefore
mandatory.

Noise level: liquidctl reports it for all `SmartDevice2` devices; whether the
0x2011 board has a microphone, or returns 0, is UNVERIFIED. Show it only if it
is non-zero in at least one report.

Mapping to UI: fan mode string = `none` / `DC` / `PWM`; when type is 0, show the
channel as "no fan detected" and disable its duty control (the kernel
documentation says `pwm*` changes are ignored for fans not detected, and
exposes `pwmN_mode` read-only: 1 for PWM fans, 0 for other fans or no fan).
Note liquidctl indexes `raw_modes[msg[16 + i]]` directly, so a type byte above
2 would crash it; RigControl's validation rejects such reports instead.

### 6.2 `67 04` — voltage / current status (optional)

| Offset | Length | Type | Field |
| --- | --- | --- | --- |
| 0 | 1 | u8 | 0x67 |
| 1 | 1 | u8 | 0x04 status type: voltage |
| 2 | 14 | bytes | static block (as 6.1) |
| 16 | 8 | u8[8] | fan type per channel (as 6.1) |
| 24 | 16 | u16le[8] | fan voltage in millivolts ("non-zero even when fan is not connected") |
| 40 | 16 | u16le[8] | fan current in milliamperes ("near-zero when disconnected") |

Source: Linux `nzxt-smart2.c` only (web). The device sends one `67 02` and one
`67 04` per update interval. liquidctl and OpenRGB ignore `67 04`. Status: web
cross-check only, UNVERIFIED against local clones; treat as optional telemetry
and validate plausibility (voltage <= 13000 mV) before display.

### 6.3 `11 01` — firmware info

| Offset | Length | Field | Status |
| --- | --- | --- | --- |
| 0 | 1 | 0x11 | CONFIRMED |
| 1 | 1 | 0x01 | CONFIRMED |
| 2 | 12 | device-constant block | INFERRED |
| 14 | 2 | USB product ID, little-endian (`11 20` on a 0x2011; `22 20` on a 0x2022 Control Hub) | INFERRED from two samples |
| 16 | 1 | 0x01 in both samples, unknown | — |
| 17 (0x11) | 1 | firmware major | CONFIRMED |
| 18 (0x12) | 1 | firmware minor | CONFIRMED |
| 19 (0x13) | 1 | firmware patch | CONFIRMED |
| 20 | 44 | not described | — |

Version string = `"{b17}.{b18}.{b19}"` (`smart_device.py` lines 543-545 and
786-788; OpenRGB line 929). Expected on the development PC: "1.7.0" for the 2011 and
"1.5.x" for both 2021 units (from bcdDevice, section 1.2; the patch digit is
not in bcdDevice).

### 6.4 `21 03` — lighting / accessory info

| Offset | Length | Field |
| --- | --- | --- |
| 0 | 1 | 0x21 |
| 1 | 1 | 0x03 |
| 2 | 12 | device-constant block |
| 14 | 1 | number of LED channels (6 on a 0x2011 per issue #541; 3 expected on a 0x2021) |
| 15 + 6c + a | 1 | accessory ID of accessory `a` (0..5) on channel `c` (0-based); 0x00 = empty slot |

Sources: liquidctl `parse_led_info` (lines 547-556), `HUE2_MAX_ACCESSORIES_IN_CHANNEL = 6`
(`liquidctl/util.py` line 21); OpenRGB `UpdateDeviceList` (lines 213-225:
`start = 0x0F + 6 * chan`). With 6 channels the table occupies offsets 15..50.

Accessory IDs (union of `liquidctl/util.py` lines 43-59 and OpenRGB
`NZXTHue2Controller.cpp` lines 222-322; the two disagree on a few names):

| ID | liquidctl name | OpenRGB name / LED count |
| --- | --- | --- |
| 0x01 | HUE+ LED Strip | Hue 1 strip / 10 |
| 0x02 | AER RGB 1 | Aer 1 fan / 8 |
| 0x04 | HUE 2 LED Strip 300 mm | Hue 2 strip / 10 |
| 0x05 | HUE 2 LED Strip 250 mm | Hue 2 strip / 8 |
| 0x06 | HUE 2 LED Strip 200 mm | Hue 2 strip / 6 |
| 0x07 | HUE 2 Cable Comb | (not listed) |
| 0x08 | (not listed) | Hue 2 Cable Comb / 14 |
| 0x09 | HUE 2 Underglow 300 mm | / 15 |
| 0x0A | HUE 2 Underglow 200 mm | / 10 |
| 0x0B | AER RGB 2 120 mm | / 8 |
| 0x0C | AER RGB 2 140 mm | / 8 |
| 0x10, 0x11 | Kraken X pump ring, logo | / 8, 1 |
| 0x13 | F120 RGB | / 18 |
| 0x14 | F140 RGB | / 18 |
| 0x15, 0x16 | (not listed) | F120 / F140 RGB Duo / 20 |
| 0x17 | F140 RGB Core | F120 RGB Core / 8 |
| 0x18 | (not listed) | F140 RGB Core / 8 |
| 0x19 | (not listed) | F120 RGB Core case version / 8 |
| 0x1B | F240 RGB Core | (not listed) |
| 0x1C | (not listed) | F280 RGB Core case version / 16 |
| 0x1D | F360 RGB Core | F360 RGB Core case version / 24 |
| 0x1E | (not listed) | Kraken Elite ring / 24 |
| 0x1F | (not listed) | F420 RGB / 24 |
| 0x2A | (not listed) | Kraken 2024 Plus fans (3x8) / 24 (OpenRGB line 316) |

Unknown IDs: liquidctl maps them to an "Unknown" pseudo-name (`util.py`
`_missing_`); OpenRGB counts them as 0 LEDs. RigControl must display unknown
IDs as hex, never fail.

### 6.5 `61 03` — fan configuration (reply to `60 03`)

| Offset | Length | Field |
| --- | --- | --- |
| 0 | 1 | 0x61 |
| 1 | 1 | 0x03 ("always 0x03") |
| 2 | 14 | static block |
| 16 | 8 | fan type per channel (0 none, 1 DC, 2 PWM) |

Source: Linux `struct fan_config_report` only (web). Optional; the same fan
types are repeated in every `67` report.

---

## 7. Fan duty: ranges, persistence, safe limits

| Topic | Fact | Status |
| --- | --- | --- |
| Channels | fan1, fan2, fan3 (cid 0..2, mask 0x01, 0x02, 0x04) | CONFIRMED |
| Range | 0..100 percent, integer | CONFIRMED |
| Control type | fixed duty only; no curves, no temperature source in the device (`set_speed_profile` raises `NotSupportedByDevice`, line 188-189) | CONFIRMED |
| PWM vs DC | chosen automatically per channel by the device at `60 03` | CONFIRMED (module docstring lines 42-44; kernel) |
| 0 percent | fan fully off in both modes | CONFIRMED (kernel, web) |
| Persistence when the host app exits | the last duty stays in effect; liquidctl docs: configuration "persists as long as the device still gets power, even if the system has gone to Soft Off (S5) state" (`docs/nzxt-hue2-guide.md` line 8) | CONFIRMED for the family; UNVERIFIED specifically on 0x2011 fw 1.7.0 |
| Persistence across loss of standby power | not persistent; device must be re-initialised (`smart_device.py` lines 106-109). Power-on default duty is UNVERIFIED (the only sample shows 40 percent on two unpopulated channels) | UNVERIFIED |
| Effect of `60 03` on duty | resets the duty values (to an undocumented value) | kernel comment, web; reset value UNVERIFIED |
| Watchdog / failsafe | none documented in any reference. Assume that if RigControl crashes after setting 0 percent, the fan stays off | absence in references (a hidden firmware failsafe cannot be ruled out, but must not be relied on) |
| Ack | none; verify through `67 02` | CONFIRMED (absence) |

Safe limits (spec-author policy, not from source — adjust per product decision):

- Clamp every request to 0..100 before building the packet; reject NaN/negative.
- Enforce a default floor of 25 percent for channels whose type is DC or PWM; allow lower values (including 0) only behind an explicit per-channel "allow fan stop" user setting.
- Never write to a channel whose reported type is 0.
- Send only on change, and at most one `62 01` packet per channel per second. No device rate limit is documented; this limit keeps traffic low next to CAM and OpenRGB.
- On normal exit, and in an unhandled-exception handler, either leave the last duty (default) or write a configured "exit duty" (suggest 50). Because the duty persists, do not exit while a channel is at 0.
- After each write, check bytes 40+i of the next two `67 02` reports. If the value differs, another program (CAM) is overriding: stop writing, surface "controlled by another application" (section 12).

---

## 8. Firmware and accessory detection

### 8.1 Firmware

Preferred order:

1. **No I/O:** bcdDevice from the HID device info (HidSharp `ReleaseNumberBcd`): 0x0107 gives "1.7", 0x0105 gives "1.5" (INFERRED, section 1.2).
2. **Passive:** if a `11 01` report happens to arrive (another program asked), parse it (section 6.3).
3. **Active:** write `10 01`, read up to 12 reports or 2 s for `11 01`. Safe on both PIDs per both references.

### 8.2 Accessories (LED devices)

`20 03` returns the accessory table (section 6.4). RigControl should **not**
send it:

- Lighting is owned by OpenRGB, which sends `20 03` itself at detection (`UpdateDeviceList`).
- OpenRGB notes the Smart Device V2 needs 100-200 ms to detect attached devices before answering (`NZXTHue2Controller.h` lines 49-56), and liquidctl measured about **2.5 s** for the lighting-info reply during initialisation (`usb.py` lines 80-83, citing #526), i.e. the request triggers a real re-detection, not a table lookup.
- liquidctl 1.3.0 changelog: "Fix LED blinking when executing `status` on a Smart Device V2" (`CHANGELOG.md` line 979). INFERRED: re-requesting lighting info makes the LEDs blink; the changelog does not name the command, so the cause is UNVERIFIED.

If the accessory list is wanted for display, get it from OpenRGB's zone/segment
data, or parse `21 03` passively when it arrives.

### 8.3 Fan presence

Use the fan type bytes (16..18) of `67 02`: 0 = no fan detected. Types are
fixed at the last `60 03`; a fan plugged in later shows rpm but is not
controllable until `60 03` is sent again (kernel documentation, web).

---

## 9. 0x2021 "NZXT RGB Controller" — what RigControl may do

| Action | Allowed | Reason |
| --- | --- | --- |
| Enumerate, show name / serial / bcdDevice firmware | yes | no I/O |
| Write `10 01`, parse `11 01` | optional | CONFIRMED safe (`smart_device.py` lines 782, 786-788) |
| Write `20 03`, parse `21 03` | not recommended | section 8.2 |
| Write `60 02`, `60 03`, `62 01` | **never** | device has no fan channels; liquidctl never sends these to it (initialize, lines 779-783, has no `60` writes); effect on this firmware is unknown |
| Expect `67` status reports | no | liquidctl's `get_status` for this class reads nothing (lines 806-814). Whether the device emits any periodic report is UNVERIFIED; ignore everything except `11 01` / `21 03` |
| Fan rpm / duty | not available | RGB only |

Two units are present; label them by serial number. Which physical fans hang
on which unit is discoverable only through the accessory table (via OpenRGB).

---

## 10. LED capability (summary only — lighting is handled through OpenRGB)

| Item | 0x2011 | 0x2021 |
| --- | --- | --- |
| LED channels | 6 (OpenRGB table; `21 03` byte 14 = 6 in issue #541) | 3 (both references) |
| Per channel | up to 6 accessories and 40 LEDs (`docs/nzxt-hue2-guide.md` line 6) | same family limits |
| Colour byte order | G, R, B (`smart_device.py` line 161; OpenRGB lines 407-409) | G, R, B |
| liquidctl support | **disabled** (0 colour channels, "protocol changed, see #541"); in #541 a legacy `28 03` effect packet had no visible effect on fw 1.7.0 | effects `2A 04`, per-LED `22 10` / `22 11` + apply `22 A0` (lines 827-859) |
| OpenRGB support | listed as LEGACY protocol: per-LED `22 10` / `22 11` + apply `22 A0`, effects `28 03` | listed as LEGACY protocol (same packets) |
| Conflict between references | yes — liquidctl says legacy lighting is broken on this PID, OpenRGB drives it as legacy. Whether OpenRGB direct mode works on fw 1.7.0 is UNVERIFIED (confirm by testing OpenRGB itself) | yes for effect mode (`2A 04` vs `28 03`); per-LED direct packets agree except apply byte 7 (0x08 in liquidctl, 0x28 in OpenRGB) |

RigControl must not send any `22`, `26`, `28` or `2A` report itself.

---

## 11. Ordering and timing constraints

| Constraint | Value | Source |
| --- | --- | --- |
| Init before fan control | `60 03` (and `60 02`) once per power cycle / resume / re-plug, unless reporting is already running | `smart_device.py` lines 512-517, 525-536; kernel `reset_resume` |
| Status cadence | one `67 02` plus one `67 04` per interval; 1 s with N = 3, about 0.5 s with N = 1, minimum 0.25 s | kernel comment; issue #128 |
| Reply latency for `20 03` | 100-200 ms per OpenRGB, but about 2.5 s per liquidctl (#526) during initialisation; OpenRGB waits up to 1000 ms per read, 10 reads; liquidctl uses a 5000 ms read timeout because of this | `NZXTHue2Controller.h` lines 49-56; `usb.py` lines 80-83 |
| After `60 03` | ignore `67` duty values until `61 03` arrives (kernel behaviour), then re-apply duty | kernel `handle_fan_status_report` (web) |
| Reply search window | up to 12 input reports (liquidctl), 5000 ms per read | `smart_device.py` line 449; `usb.py` line 83 |
| Stale data | liquidctl flushes the OS input queue before reading status (`clear_enqueued_reports`, lines 523, 582). In C# run a dedicated continuous reader thread instead, so the queue never goes stale and never overflows | `usb.py` lines 424-441 |
| Write pacing | none documented. Policy: >= 50 ms between any two writes, one duty write per channel per second | spec-author policy |
| Duty write confirmation | next `67 02` (<= 2 intervals) | spec-author policy (the kernel itself does not wait) |
| Stale-sensor rule | if no valid `67 02` for 5 intervals, mark rpm/duty values stale and stop writing duty | spec-author policy |

Threading: one reader thread per device doing blocking reads with a finite
timeout (for example 2000 ms; a timeout is not an error), one serialized writer.
Never issue a blocking read without a timeout (OpenRGB's `SendFirmwareRequest`
loop at lines 924-927 has none and can hang forever).

---

## 12. Behaviour when NZXT CAM (or OpenRGB) talks to the device at the same time

NZXT CAM is installed and must keep running. Facts and consequences:

| Fact | Status | Consequence for RigControl |
| --- | --- | --- |
| The protocol is stateless per packet (no session, no sequence numbers, no checksums) | CONFIRMED (absence in all references) | Interleaving packets from two programs cannot corrupt a frame. |
| The Linux driver documentation says it works alongside userspace tools on hidraw | web (kernel `nzxt-smart2.rst`) | Two masters are an anticipated situation. |
| Windows delivers every input report to every open handle of the HID collection | general Windows HID behaviour, not from the references — UNVERIFIED here | RigControl sees `67 02` reports that CAM enabled, and also replies to CAM's and OpenRGB's requests (`11 01`, `21 03`, `61 03`, lighting acks). Ignore unknown IDs; never treat an unexpected reply as an error. Likewise CAM sees replies to RigControl's requests. |
| Fan duty is a single register per channel; last writer wins | CONFIRMED by protocol shape | If CAM's fan curve for this controller is active, CAM will overwrite RigControl's duty at its own cadence (cadence UNVERIFIED). The fans will oscillate between the two targets. |
| The update interval is device-global | CONFIRMED by protocol shape | Changing it changes CAM's data rate too. Use N = 3 (the default quoted in issue #128 and the kernel default; whether CAM uses the same value is UNVERIFIED), and only when reporting is not already running. |
| `60 03` re-detects fans and resets duty values | detection CONFIRMED (liquidctl, kernel); duty reset from kernel comment (web) | Sending it while CAM controls the fans resets CAM's duties until CAM rewrites them. Do not send it when `67 02` reports are already flowing. |
| CAM may open the device exclusively | UNVERIFIED (OpenRGB coexisting with CAM is commonly reported, which suggests shared access) | Handle open failure gracefully. |
| OpenRGB sends `10 01` and `20 03` at detection and lighting packets afterwards; it never calls `SendFan` and its fan status code is disabled (`if(false)`, `NZXTHue2Controller.cpp` line 349) | CONFIRMED | OpenRGB does not fight over fan duty. Its `20 03` may cause a one-off LED blink at startup. |

Required operating modes:

| Mode | When | Writes allowed |
| --- | --- | --- |
| **Monitor (default)** | CAM is running, or ownership unknown | none. Parse `67 02` / `67 04` passively. If no `67 02` arrives within 5 s, show "no data (controller not initialised)" rather than initialising silently. |
| **Control** | the user has explicitly enabled fan control for this device, having set CAM's fan control for this controller aside | init sequence of section 4 (skipping what is already running) and `62 01`. |

Conflict detection in Control mode: after a duty write, if a later `67 02`
shows a duty for that channel different from the last value RigControl wrote,
on 3 separate occasions within 60 s, drop to Monitor mode and tell the user
another application is controlling the fans. Never try to "win" by writing
faster.

RigControl must never stop, restart or reconfigure CAM.

---

## 13. NEVER SEND list

No reference documents a firmware-update, bootloader-entry, factory-reset or
EEPROM-write command for these two devices. Because the dangerous commands are
unknown rather than known-absent, the rule is **deny by default**:

1. Never write any report whose bytes 0-1 are not one of: `60 02`, `60 03`, `62 01`, `10 01` (and `20 03` only if a future decision reverses section 8.2).
2. Never write `60 xx` or `62 xx` to a 0x2021.
3. Never write `62 01` with: a duty byte above 0x64; any of mask bits 3..7 set; more than one mask bit set; a set mask bit whose duty byte was not explicitly computed for this request (a zero stops the fan).
4. Never write `60 02` with constants other than `01 E8 N 01 E8 N`, with the two N bytes different, or with N outside {0x01, 0x03}.
5. Never write lighting reports (`22 xx`, `26 xx`, `28 xx`, `2A xx`) — OpenRGB owns lighting, and the two references disagree on the correct lighting protocol for both PIDs (section 10).
6. Never write commands that belong to sibling products: `10 02` and `66 01` (Control Hub, `control_hub.py` lines 130-133), `70 xx` / `74 xx` and every other `7x` report (Kraken family, `liquidctl/driver/kraken3.py` lines 246-247, 736), any 0x1E71 Smart Device V1 packets (`01 5C`, `01 5D`, `02 4D`, `02 4B`, `03 ...`; `smart_device.py` lines 287-288, 386-390). They are different protocols; their effect on these devices is unknown.
7. Never send HID feature reports, never call `HidD_SetFeature`, and never send control transfers other than what the OS HID stack does on its own.
8. Never send any report ID in the ranges not listed above "to see what happens" (no scanning, no fuzzing). An undocumented ID may be the bootloader entry.
9. Never write anything during system suspend/resume transitions; re-initialise only after resume completes and the device has re-enumerated.
10. Never write when the report-length check of section 2 failed, or when `67 02` validation (section 6.1) is failing.
11. Never write `60 03` (or `60 02`) while valid `67 02` reports are already arriving, and never in Monitor mode: detection resets duty values (section 5.1) and the interval is device-global.

---

## 14. Test vectors

All packets are 64 bytes, shown as 4 rows of 16. "OUT" vectors are what the
packet builder must produce byte-for-byte; "IN" vectors are parser inputs with
the expected parse result.

### TV1 (OUT) — set update interval, 0.5 s, as sent by liquidctl

Derivation: `[0x60, 0x02, 0x01, 0xE8, N, 0x01, 0xE8, N]` with
`N = 1 + round((0.5 - 0.5) / 0.25) = 1` (`smart_device.py` lines 534-535), zero
padded to 64. Cross-check: the debug log in liquidctl issue #541 from a real
1E71:2011 shows report 0x60 followed by `02:01:e8:01:01:e8:01:00:...`.

```
60 02 01 E8 01 01 E8 01 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV2 (OUT) — detect fans

Derivation: `[0x60, 0x03]` (`smart_device.py` line 536; kernel
`detect_fans_report`), zero padded.

```
60 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV3 (OUT) — firmware info request

Derivation: `[0x10, 0x01]` (`smart_device.py` line 539; OpenRGB lines 916-917).
The liquidctl mock device keys its reply on exactly these two bytes
(`tests/test_smart_device2.py` line 25).

```
10 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV4 (OUT) — set fan3 to 50 percent

Derivation: the call made in liquidctl's own test
`test_not_totally_broken` (`tests/test_smart_device2.py` line 137:
`set_fixed_speed(channel="fan3", duty=50)`). cid = 2, mask = `0x01 << 2` = 0x04,
`msg[2 + 3] = 50 = 0x32` (`smart_device.py` lines 666-669). The test does not
assert the bytes; they follow deterministically from the driver code.

```
62 01 04 00 00 32 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV5 (OUT) — set fan1 to 100 percent

Derivation: cid = 0, mask = 0x01, `msg[3] = 100 = 0x64`.

```
62 01 01 64 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV6 (OUT) — set fan2 to 30 percent

Derivation: cid = 1, mask = 0x02, `msg[4] = 30 = 0x1E`.

```
62 01 02 00 1E 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV7 (OUT) — set update interval, 1 s (RigControl default)

Derivation: kernel formula with 1000 ms:
`N = 1 + round((1000 - 488) / 256) = 3`. Same bytes that liquidctl issue #128
quotes as the Smart Device V2 init string `60 02 01 E8 03 01 E8 03` (0x03
described there as the default).

```
60 02 01 E8 03 01 E8 03 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### TV8 (IN) — `67 02` status, from liquidctl's test suite

Source: `SAMPLE_STATUS` in `tests/test_smart_device2.py` lines 11-14 (captured
from a Smart Device V2, liquidctl issue #292, with fan 1 mode adapted to PWM).

```
67 02 3A 00 3F 00 18 57 32 53 32 30 31 20 03 00
02 00 00 00 00 00 00 00 FC 03 00 00 00 00 00 00
00 00 00 00 00 00 00 00 32 28 28 00 00 00 00 00
32 28 28 00 00 00 00 00 30 00 00 00 00 00 00 00
```

Expected parse (identical to the test's `expected` list, lines 67-78):

| Field | Bytes | Value |
| --- | --- | --- |
| Fan 1 type | [16] = 02 | PWM |
| Fan 1 rpm | [24..25] = FC 03 | 0x03FC = 1020 |
| Fan 1 duty | [40] = 32 | 50 percent |
| Fan 2 type | [17] = 00 | none |
| Fan 2 rpm | [26..27] = 00 00 | 0 |
| Fan 2 duty | [41] = 28 | 40 percent |
| Fan 3 type | [18] = 00 | none |
| Fan 3 rpm | [28..29] = 00 00 | 0 |
| Fan 3 duty | [42] = 28 | 40 percent |
| Noise | [56] = 30 | 48 dB |
| Duty duplicate | [48..50] = 32 28 28 | equals [40..42] (validation passes) |

### TV9 (IN) — `67 02` all-zero status

Source: `tests/test_smart_device2.py` line 132: `[0x67, 0x02] + [0] * 62`.

```
67 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Expected parse: all three fans type none, 0 rpm, 0 percent; noise 0 dB. The
report is valid; all three duty controls must be disabled (type 0).

### TV10 (IN) — `11 01` firmware reply from a real 1E71:2011

Source: debug log in liquidctl issue #541 (web). Only the first 20 bytes were
quoted there; the remaining 44 bytes are zero-filled **by the spec author** and
may differ on a real device. Use only for testing offsets 0..19.

```
11 01 12 80 03 0A C1 64 76 AB 40 9B 04 5F 11 20
01 01 07 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Expected parse: firmware = [17].[18].[19] = "1.7.0"; bytes [14..15] = `11 20`
= 0x2011 little-endian.

### TV11 (IN) — `21 03` accessory reply from a real 1E71:2011

Source: same issue #541 log; first 22 bytes quoted, remainder zero-filled by
the spec author.

```
21 03 12 80 03 0A C1 64 76 AB 40 9B 04 5F 06 14
00 00 00 00 00 14 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Expected parse: [14] = 6 LED channels; channel 1 accessory 1 = [15] = 0x14
(F140 RGB); channel 2 accessory 1 = [15 + 6] = [21] = 0x14 (F140 RGB); all
other slots empty.

### TV12 (IN) — `67 04` voltage report, SYNTHETIC

Not from any capture; constructed by the spec author from the kernel struct
layout to exercise the parser. Fan 1 PWM 12000 mV 150 mA, fan 2 DC 9000 mV
300 mA, fan 3 none 12000 mV 0 mA.

```
67 04 00 00 00 00 00 00 00 00 00 00 00 00 03 00
02 01 00 00 00 00 00 00 E0 2E 28 23 E0 2E 00 00
00 00 00 00 00 00 00 00 96 00 2C 01 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

Expected parse: types [16..18] = PWM, DC, none; voltages [24..29] = 0x2EE0,
0x2328, 0x2EE0 = 12000, 9000, 12000 mV; currents [40..45] = 0x0096, 0x012C,
0x0000 = 150, 300, 0 mA.

### Negative tests the packet builder must pass

| Request | Required behaviour |
| --- | --- |
| duty 101 or 255 | clamp to 100 (TV5 shape) or reject; never emit a byte above 0x64 |
| channel index 3 or above | reject; never emit mask 0x08..0x80 |
| "set all fans" | three packets, masks 0x01, 0x02, 0x04; never mask 0x07 |
| any duty write to a 0x2021 | reject before building a packet |
| input report `67 01`, `75 02`, or `67 02` with byte 16 = 0x05 | ignored / rejected by validation, no sensor update |

---

## 15. UNVERIFIED items and how to confirm each

| # | Statement | How to confirm |
| --- | --- | --- |
| 1 | The devices use numbered HID reports with 64-byte output and input report lengths | Read HID caps at runtime (section 2) or dump the report descriptor (`HidDevice.GetReportDescriptor()` in HidSharp). No device write needed. |
| 2 | The `67 02` layout of section 6.1 holds on 0x2011 firmware 1.7.0 | Log the first passive `67 02` reports while CAM is running and compare rpm/duty with CAM's display. |
| 3 | Bytes 2..13 are a per-device unique ID equal to the USB serial | Compare one captured report with the serial string. |
| 4 | bcdDevice equals firmware major.minor | Compare with a `11 01` reply. |
| 5 | Duty persists after the writing program exits on 0x2011 (documented for the family) | Set a duty, close the app with CAM closed, watch `67 02` duty from a second passive reader or the fan itself. |
| 6 | Power-on default duty and whether status reports flow before any `60` command | Cold boot with CAM autostart disabled, passive read only. |
| 7 | 0x2011 reports a meaningful noise level at offset 56 | Observe byte 56 over time. |
| 8 | Multi-bit masks in `62 01` work | Not needed; do not test. |
| 9 | `20 03` causes LEDs to blink / re-detect | Observe when OpenRGB starts. |
| 10 | Whether 0x2021 emits any periodic input report | Passive read for 10 s. |
| 11 | NZXT CAM opens the devices non-exclusively, and how often it rewrites duty | Attempt a shared open while CAM runs; watch `67 02` duty after a manual write in Control mode. |
| 12 | Every open handle receives every input report on Windows | Standard HID class behaviour; confirm with two passive readers. |
| 13 | HidSharp's Win32 handle share mode (its own `OpenOption.Exclusive` layer is documented as default false, which is CONFIRMED) | Open the device in RigControl while CAM and OpenRGB run; both must keep working. |
| 14 | `67 04` and `61 03` layouts (kernel source via web only) | Passive capture; plausibility of 12 V rails. |
| 17 | The duty value the device resets to after `60 03`, and whether running fans briefly change speed | Only observable by sending `60 03`; not needed if RigControl never sends it while reporting is running. |
| 18 | HidSharp exception type on read timeout | Unit test against a fake stream, or observe once on the real device in passive mode. |
| 15 | OpenRGB's legacy lighting packets actually drive 0x2011 fw 1.7.0 | Test in OpenRGB itself; outside this spec's scope. |
| 16 | Which physical fans (case, radiator) are wired to fan1..fan3 of the 2011 | Physical inspection or CAM's channel view. |

---

## 16. Sources

Local clones (primary; paths relative to the reference folder):

| Repository | Revision | Files used |
| --- | --- | --- |
| liquidctl | 292dc561361c (2026-06-16) | `liquidctl/driver/smart_device.py`, `liquidctl/driver/control_hub.py`, `liquidctl/driver/usb.py`, `liquidctl/util.py`, `tests/test_smart_device2.py`, `tests/test_control_hub.py`, `tests/test_nzxt_h1_v2.py`, `tests/_testutils.py`, `docs/nzxt-hue2-guide.md`, `CHANGELOG.md`, `README.md`, `extra/linux/71-liquidctl.rules` |
| OpenRGB | d09a493f8b44 (2026-11-14) | `Controllers/NZXTHue2Controller/NZXTHue2Devices.h`, `NZXTHue2Devices.cpp`, `NZXTHue2Controller.h`, `NZXTHue2Controller.cpp`, `NZXTHue2ControllerDetect.cpp`, `RGBController_NZXTHue2.cpp` |
| LibreHardwareMonitor | 9e2d677cefff (2026-11-11) | `LibreHardwareMonitorLib/Hardware/Controller/Nzxt/NzxtGroup.cs` |

openrazer and NvAPIWrapper contain nothing relevant to these devices.

NuGet package (read as a file, not loaded): `hidsharp/2.6.4/lib/netstandard2.0/HidSharp.XML`
(API documentation for `OpenOption`, `DeviceStream`, `HidDevice`, `HidStream`).
liquidctl `README.md` lines 155-156 and 169-176 (support flags) and 380-381
(Windows needs no access permissions).

Web cross-checks (reachable on 2026-11-15; read through a summarising fetch
tool, so quoted kernel details should be re-checked against the kernel tree
before being relied on beyond what the local clones already confirm):

- Linux kernel `drivers/hwmon/nzxt-smart2.c` (torvalds/linux master): report IDs, struct layouts, interval table, device ID table including 0x2011, set-fan-speed semantics (0 = off, DC minimum about 9 V).
- Linux kernel `Documentation/hwmon/nzxt-smart2.rst`: coexistence with hidraw tools, hot-plug needs re-detect, 250 ms minimum interval. Only a summary was obtained, not the verbatim text.
- liquidctl issue #541: real 1E71:2011, release 0x0107, firmware 1.7.0, fan control working, legacy lighting not working; source of TV10 and TV11 and of the TV1 cross-check.
- liquidctl issue #128: meaning of the interval byte; init string `60 02 01 E8 03 01 E8 03` with 0x03 called the default (the thread does not say which program sent it).
- liquidctl PR #690: 2023 RGB Controller, firmware 1.5.0, three channels, ships with Kraken 2023 Elite.
- LibreHardwareMonitor issue #1305: open request for `VID_1E71&PID_2011&REV_0107` (H9 Elite controller); no protocol details, not implemented.

Local machine (read-only): `Get-PnpDevice` / `Get-PnpDeviceProperty` for
`VID_1E71` instances. No device handle was opened and no report was sent or
read.

---

## Verification log

Adversarial review, 2026-11-15. Every fact below was re-derived from the local
clones (liquidctl 292dc561361c, OpenRGB d09a493f8b44, LibreHardwareMonitor
clone), the HidSharp 2.6.4 XML documentation, read-only PnP queries on this
PC, and web fetches of the Linux kernel `nzxt-smart2.c` / `nzxt-smart2.rst`
(torvalds/linux master) and liquidctl issues #541 and #128. Web pages were read
through a summarising fetch tool. Nothing was built, run, or sent to any
device.

### Corrections made

| # | Where | Was | Now | Evidence |
| --- | --- | --- | --- | --- |
| C1 | §0, §4 step 8, §5.1, §7, §11, §12, §13 item 11 | Side effects of `60 03` on running fans "UNVERIFIED" | `60 03` **resets the duty values**; re-apply duty after it, never send it while reporting is running, and ignore `67` duty until `61 03` arrives | kernel `handle_fan_status_report` comments "fan detection resets pwm values" and "Only accept other data after getting 0x61" (web) |
| C2 | §0, §2 | Admin rights not addressed | No admin, no driver replacement, no kernel driver (HVCI irrelevant) | liquidctl `README.md` lines 380-381, 155-156, 169-176; PnP shows `HidUsb` |
| C3 | §2 | HidSharp non-exclusive default "UNVERIFIED — the NuGet package has no source" | `OpenOption.Exclusive` default false is CONFIRMED by the package's XML docs; read/write timeouts default 3000 ms; report-length methods include the report ID byte. Only the Win32 share mode and timeout exception type remain UNVERIFIED | `HidSharp.XML` lines 268-282, 333-344, 490-495, 583-591 |
| C4 | §5.3, §11 | Duty-write confirmation via next `67 02` attributed to "kernel `set_pwm` comment" | Marked as spec-author policy; the kernel comment says the device always accepts values and the kernel updates "without waiting" | kernel `set_pwm` comment (web) |
| C5 | §5.1 | "the device probes each channel, decides DC or PWM" attributed to the kernel comment | Kernel comment only says `61 03` is the reply to detect-fans with types "as detected by the device"; the auto DC/PWM choice is cited to liquidctl docstring lines 42-44 | kernel struct comments (web); `smart_device.py` 42-44 |
| C6 | §5.3 | sync loop cited as lines 179-186 | `set_fixed_speed`, lines 176-186 | `smart_device.py` |
| C7 | §6.1 | Duty duplicate (48..50 == 40..42) was a hard rejection rule | Downgraded to a logged soft check; only one sample shows equality | TV8 only |
| C8 | §6.1 | "kernel exposes `pwmN_enable` read-only for exactly this purpose" | Not confirmed; replaced with what the kernel docs do say (`pwm*` changes ignored for undetected fans; `pwmN_mode` read-only) | `nzxt-smart2.rst` (web) |
| C9 | §6.4 | Accessory table missing OpenRGB ID 0x2A | Added 0x2A (Kraken 2024 Plus fans, 24 LEDs) and unknown-ID handling | OpenRGB `NZXTHue2Controller.cpp` line 316; liquidctl `util.py` `_missing_` |
| C10 | §8.2, §11 | Lighting-info reply latency given as 100-200 ms only | Added liquidctl's ~2.5 s observation (#526), the reason for its 5000 ms timeout | `usb.py` lines 80-83 |
| C11 | §0, §7 | Persistence and "no failsafe" stated flatly | Persistence marked as documented for the family, not verified on 0x2011; failsafe stated as "none documented", not "none exists" | `docs/nzxt-hue2-guide.md` line 8 |
| C12 | §5.3 | Missing rule on scaling | Added: a non-zero request must never become 0 (kernel `scale_pwm_value` rationale) | kernel (web) |
| C13 | §15, §16 | — | Added UNVERIFIED items 17-18; added HidSharp XML and README to sources | — |

### Items confirmed unchanged

| Item | Result | Evidence |
| --- | --- | --- |
| 0x2011 = "NZXT RGB & Fan Controller (3+6 channels)", 3 fan channels; liquidctl colour channels 0 ("protocol changed, see #541") | confirmed | `smart_device.py` 431-434 |
| 0x2021 = "NZXT 2023 RGB Controller", **0 fan channels**, 3 LED channels; `set_fixed_speed`/`_write_fixed_duty` raise `NotSupportedByDevice`; `get_status` reads nothing | confirmed | `smart_device.py` 705-715, 806-814, 861-865; guide line 52 "RGB **only**" |
| OpenRGB: 0x2011 = 6 RGB / 3 fan, 64-byte, LEGACY; 0x2021 = 3 RGB / 0 fan, 64-byte, LEGACY; detector names | confirmed | `NZXTHue2Devices.h` 36, 43; `NZXTHue2Devices.cpp` 328-343, 447-462; `NZXTHue2ControllerDetect.cpp` 53, 68 |
| The development PC: one 2011 (REV_0107) and two 2021 (REV_0105), single non-composite HID interface, TLC UP:FF00 U:0001, `HidUsb`, all plus Kraken on hub 05E3:0608 ("USB2.0 Hub") | confirmed (re-queried) | `Get-PnpDevice`, `Get-PnpDeviceProperty` |
| `_READ_LENGTH`/`_WRITE_LENGTH` 64, `_MAX_READ_ATTEMPTS` 12, zero padding in `_write`, 5000 ms default timeout | confirmed | `smart_device.py` 191-193, 449-451, 717-719; `usb.py` line 83 |
| Init order liquidctl: flush, `60 02 01 E8 01 01 E8 01`, `60 03`, `10 01`, `20 03`, wait only for `11 01` on 0x2011; 4 writes asserted in test | confirmed | `smart_device.py` 523-561; `tests/test_smart_device2.py` 45-57 |
| Kernel order: `60 03` then `60 02 01 E8 N 01 E8 N`; interval maps 0→250 ms, N→488+(N-1)·256 ms; default 1000 ms; minimum 250 ms | confirmed | kernel source and rst (web) |
| Interval formula liquidctl `1 + round((secs - .5)/.25)` = 1 at 0.5 s; kernel gives 3 at 1000 ms, 1 at 500 ms | recomputed | `smart_device.py` 534 |
| `62 01 <1<<cid> d1 d2 d3`, cid 0..2, duty 0..100 clamped, one mask bit per packet in all three implementations; kernel struct has 8 duty slots; `magic` "Should be 0x01" | confirmed | `smart_device.py` 133-134, 184, 666-669; OpenRGB 158-161; kernel `set_fan_speed_report`, `set_pwm` (web) |
| Duty 0 turns the fan off completely; DC minimum (duty 1) about 9 V | confirmed | kernel struct comment and rst (web) |
| `67 02` offsets: type 16+i, rpm LE16 at 24+2i, duty 40+i, duplicate 48+i, noise 56; static block 2..15 with byte 14 possibly the channel count; minimum report size 57 | confirmed | `smart_device.py` 567-580; OpenRGB 372-373; kernel `fan_status_report`, `unknown_static_data` comment "Byte 12 seems to be the number of fan channels" (web) |
| `67 04` voltage mV LE16 at 24+2i, current mA LE16 at 40+2i; `61 03` type bytes at 16+i, magic "Always 0x03" | confirmed (web only, as the spec already says) | kernel structs (web) |
| `11 01` firmware at 0x11/0x12/0x13; `21 03` channel count at 14, accessories at 15+6c+a | confirmed | `smart_device.py` 543-556, 786-788; OpenRGB 215, 929; `util.py` line 21 |
| OpenRGB never calls `SendFan`; its fan status read is `if(false)`; `SendFirmwareRequest` read loop has no timeout | confirmed | grep of `Controllers/NZXTHue2Controller`; `NZXTHue2Controller.cpp` 349, 924-927 |
| Lighting summary: G,R,B order; liquidctl 2021 uses `2A 04` effects and apply byte 7 = 0x08; OpenRGB legacy uses `28 03` and apply byte 7 = 0x28 | confirmed | `smart_device.py` 161, 833-859; OpenRGB 533-540, 602-605 |
| Sibling-product commands in NEVER SEND list (`66 01`, `10 02`, `70 xx`, `74 01`, `01 5C`, `01 5D`, `02 4B`, `02 4D`, `03`) | confirmed | `control_hub.py` 129-134; `kraken3.py` 246-247, 710-711, 736; `smart_device.py` 287-288, 386-390 |
| LibreHardwareMonitor supports neither PID; calls `GetProductName()` on every 1E71 HID device | confirmed | `NzxtGroup.cs` 22-70 |
| CHANGELOG "Fix LED blinking when executing `status` on a Smart Device V2" is in 1.3.0 | confirmed (line 979 under `## [1.3.0]`, line 954) | `CHANGELOG.md` |
| Kernel docs: coexists with hidraw userspace tools "with no known issues"; hot-plugged fans need re-detect | confirmed | `nzxt-smart2.rst` (web) |
| Issue #541: 0x2011 rel 0x0107, fw 1.7.0, fan control worked, lighting did not; log bytes of TV1, TV10 (first 20 bytes) and TV11 (first 22 bytes) | confirmed | issue page (web) |
| Issue #128: `60 02 01 E8 03 01 E8 03` with byte 3 = 1.00 s called the default, 0.25 s steps measured; sender was liquidctl with edited bytes, not CAM | confirmed | issue page (web) |
| Test vectors TV1-TV12: each exactly 64 bytes; TV8 is byte-identical to liquidctl `SAMPLE_STATUS`; TV4-TV6 regenerated from the `_write_fixed_duty` algorithm; TV10 parses to "1.7.0"/0x2011; TV11 to 6 channels with 0x14 on channels 1 and 2; TV12 to 12000/9000/12000 mV and 150/300/0 mA | recomputed by script | — |

### Still not verified by this review

- Whether the devices use numbered reports (INFERRED only; needs HID caps at runtime). No reference shows the report descriptor, and the Linux evidence is ambiguous because hidraw output looks the same either way.
- liquidctl PR #690 (2021 firmware 1.5.0) was not re-fetched; the 0x2021 identification does not depend on it.
- The duty value after `60 03` reset, the power-on default duty, and CAM's write cadence.

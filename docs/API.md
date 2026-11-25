# RigControl UI ↔ backend contract

The UI (React, in WebView2) talks to the C# backend with JSON messages. This file is the single source of
truth for every method and event. The C# modules and `src/ui/src/types.ts` must match it exactly.

## Envelope

UI → backend (`window.chrome.webview.postMessage(object)`):

```json
{ "id": 12, "method": "cooling.setCurve", "params": { "deviceId": "kraken-1", "channel": "pump", "points": [...] } }
```

Backend → UI (`PostWebMessageAsJson`), reply:

```json
{ "id": 12, "ok": true,  "result": { ... } }
{ "id": 12, "ok": false, "error": { "code": "unsafe", "message": "Pump duty must be at least 60%." } }
```

The Linux and macOS app (Photino) carries the same messages as JSON text instead of objects:
`window.external.sendMessage(JSON.stringify(request))` and `window.external.receiveMessage(text => JSON.parse(text))`.
`src/ui/src/bridge.ts` picks the channel once at start: `window.chrome.webview` if it is there (the Windows app),
else `window.external.sendMessage` (the Linux and macOS app), else the made-up data of the developer preview, which
is only ever built into `npm run dev` or a build made with `VITE_RIGCONTROL_MOCK=1`. A packaged build that finds no
app shows "RigControl's window did not connect" and every call fails with the code `noHost`; it never shows made-up
data. A request longer than 4 million characters is dropped unread by the Linux and macOS app.

Backend → UI, push event (no id):

```json
{ "event": "sensors.snapshot", "data": { ... } }
```

Error codes: `badRequest`, `unknownModule`, `unknownMethod`, `invalidArgs`, `unavailable` (hardware/service missing
or module not ready), `needsAdmin`, `unsafe` (refused by a safety rule), `cancelled`, `internal`.
Messages are written for the user and shown as-is.

Conventions: JSON camelCase; enums are camelCase strings; temperatures in °C, power in W, clocks in MHz, memory in
MB unless the name says GB; percentages are integers 0–100; colours are `"#RRGGBB"`; a value the backend cannot read
is `null` (never NaN, never a made-up number). Times are ISO-8601 strings.

### Shared types

```ts
type ModuleState = "notStarted" | "starting" | "ready" | "degraded" | "failed" | "stopped";
type ModuleInfo  = { name: string; displayName: string; state: ModuleState; message: string | null };
type CurvePoint  = { tempC: number; dutyPercent: number };   // integers
```

Curve rules (enforced by the backend, mirrored in the UI): ≥ 2 points, temperatures strictly increasing within the
device's allowed range, duty never decreasing as temperature rises, every duty within `minDutyPercent..100`.

---

## app

| Method | Params | Result |
|---|---|---|
| `app.getInfo` | – | `{ version, isElevated, dataDirectory, logDirectory, machineName, platform, capabilities }` (see below) |
| `app.getModules` | – | `ModuleInfo[]` |
| `app.getSettings` | – | `AppSettings` |
| `app.setSettings` | `Partial<AppSettings>` | `AppSettings` |
| `app.restartAsAdmin` | – | `{ restarting: boolean, alreadyElevated: boolean }` (error `cancelled` if UAC declined) |
| `app.openExternal` | `{ url }` (https only) | `{ opened: true }` |
| `app.openLogFolder` | – | `{ opened: true }` |
| `app.getLogs` | `{ max?: number }` | `{ time, level: "info"\|"warn"\|"error", source, message }[]` |
| `app.quit` | – | `{ quitting: true }` |
| `app.setNativeView` | `{ view: "audio" \| null }` | `{ view: "audio" \| null }` (see below) |
| `app.getAudioStatus` | – | `{ state: "starting"\|"running"\|"alreadyRunningElsewhere"\|"vaultUnreadable"\|"failed", message: string\|null }` |
| `app.pickFile` | `{ title?: string, filter?: string }` (Win32 filter, e.g. `"Programs (*.exe)\|*.exe"`) | `{ path: string \| null }` (null = cancelled) |

`AppSettings = { minimizeToTray: boolean; startMinimized: boolean; startWithWindows: boolean }`

`startWithWindows` means "start elevated at sign-in": RigControl registers a Windows scheduled task (`RigControl`,
logon trigger, highest privileges, `--minimized`). Changing it needs administrator rights once, so `app.setSettings`
may show a UAC prompt and take a while (error `cancelled` if declined); the UI allows 120 s for that call.

Native views: the Audio section is the merged Audio Controller app, which is native WPF, not web UI.
`setNativeView({view:"audio"})` narrows the WebView to the navigation rail (`RAIL_WIDTH` = 232 px, left) and shows
the native Audio view in the rest of the window; `setNativeView({view:null})` gives the whole window back to the web UI.
While a native view is shown the web UI must render only the rail.

Event `app.modulesChanged` → `ModuleInfo[]`.

`platform` is `"windows"`, `"linux"`, `"macos"` or `"other"`. `capabilities` is a sorted `string[]` of what works in
this run: one name per module that is really there (`"lighting"`), its parts (`"lighting.effects"`,
`"lighting.screen"`, `"presence.custom"`, `"presence.media"`, ...) and what the app itself can do (`"host.tray"`,
`"host.elevation"`, `"host.autostart"`, `"host.overlay"`, `"host.audio"`, `"host.filePicker"`, `"host.firstRun"`,
`"host.helpers"`, `"host.restartPc"`). On Windows everything is listed. The full list of names is in
`src/RigControl.Hosting/ModuleCatalog.cs`.

A module that has no version for this system is still listed by `app.getModules`, with `state: "failed"` and a
`message` that starts with `Not available on this system`. Every call to it fails with `unavailable` and the same
sentence. The UI hides such a page or shows one calm panel, and should not keep calling it. On Linux and macOS the
`app` methods that only Windows has (`restartAsAdmin`, `pickFile`, `pickFolder`, `getBundled`, `uninstallOldApp`,
`restartPc`, `cancelRestartPc`, `getFirstRun`, `installHelpers`, `completeFirstRun`) fail the same way;
`setNativeView` answers `{ view: null }` and `getAudioStatus` answers `state: "unavailable"`. See docs/PORTING.md.

The Linux and macOS app's `capabilities` hold `lighting`, `presence`, their parts that work there and, where it can
be set up, `host.autostart`; no other `host.*` name. There `app.getSettings` always answers `minimizeToTray: false`
and `startMinimized: false` (there is no tray), `startWithWindows` means "start when I sign in" (one file in the
user's own autostart folder, no prompt), and `app.setSettings` changes only `temperatureUnit` and `startWithWindows`.

What the UI does with this (`src/ui/src/appContext.tsx`: `can(name)`, `notWindows`, `notOnThisSystem(module)`):
a page whose module is such a stand-in is left out of the menu and never drawn, so it asks for nothing; the Audio
entry needs `host.audio`; "Restart as administrator" needs `host.elevation`; the tray settings need `host.tray`;
the helper programs need `host.helpers` and the first-time setup `host.firstRun` (without them `app.getBundled` and
`app.getFirstRun` are not called); the in-game panel settings need `host.overlay` (`overlay.getState` is not
called); the GIGABYTE Control Center box needs `lighting.gcc`; the sound controls need `lighting.audio`; the music,
watching, extension and game parts of the Games page need `presence.media`, `presence.watching`,
`presence.extension` and `presence.games`. `can` answers true until `app.getInfo` has answered and for an app that
sends no list, so on Windows nothing is ever hidden by it.

---

## sensors (LibreHardwareMonitor: temperatures, loads, motherboard fans)

| Method | Params | Result |
|---|---|---|
| `sensors.getSnapshot` | – | `SensorSnapshot` |
| `sensors.getFanControls` | – | `FanControl[]` |
| `sensors.getTemperatureSources` | – | `{ id, name, hardware }[]` (sources a fan curve may follow) |
| `sensors.setFanFixed` | `{ id, dutyPercent }` | `FanControl` |
| `sensors.setFanCurve` | `{ id, sourceId, points: CurvePoint[] }` | `FanControl` |
| `sensors.setFanAuto` | `{ id }` | `FanControl` (hands the header back to the BIOS) |

```ts
type SensorSnapshot = {
  time: string;
  elevated: boolean;                 // false => only partial data (see notes)
  cpu: null | { name: string; packageTempC: number|null; coreMaxTempC: number|null; loadPercent: number|null;
                powerW: number|null; clockMHz: number|null };
  gpu: null | { name: string; tempC: number|null; hotspotTempC: number|null; loadPercent: number|null;
                powerW: number|null; coreClockMHz: number|null; memoryClockMHz: number|null;
                fanPercent: number|null; fanRpm: number|null; vramUsedMB: number|null; vramTotalMB: number|null };
  memory: { usedGB: number|null; totalGB: number|null; loadPercent: number|null };
  board: null | { name: string; superIo: string|null };
  temperatures: { id: string; name: string; hardware: string; value: number|null }[];
  fans: { id: string; name: string; hardware: string; rpm: number|null; controlId: string|null }[];
  notes: string[];                   // plain-language limitations, e.g. "Run as administrator to read CPU temperatures."
};

type FanControl = {
  id: string; name: string;
  rpmSensorId: string | null;
  mode: "auto" | "fixed" | "curve";  // auto = BIOS / firmware in charge
  dutyPercent: number | null;        // last value written / read
  curve: null | { sourceId: string; points: CurvePoint[] };
  minDutyPercent: number;            // backend refuses anything lower
  canControl: boolean;
  reason: string | null;             // why canControl is false
};
```

Safety rules: software curves only run while RigControl runs; on exit every header is handed back to the BIOS.
A curve whose temperature source becomes unreadable runs at 100 %. Headers whose name contains "pump" are never
controllable. Requires administrator; otherwise every method except `getSnapshot` returns `needsAdmin`.

Event `sensors.snapshot` → `SensorSnapshot`, about once per second.

---

## nvidia (NVAPI / NVML / Windows display APIs)

| Method | Params | Result |
|---|---|---|
| `nvidia.getInfo` | – | `{ available, driverVersion, driverBranch, gpus: { index, name, vramMB, busId, chip, family, architecture }[], limitations: string[] }` |
| `nvidia.getPower` | – | `{ powerW, limitW, defaultLimitW, minLimitW, maxLimitW, canSet, reason }` |
| `nvidia.setPowerLimit` | `{ watts }` | same as `getPower` (needs admin) |
| `nvidia.getOverclock` | – | `{ available, coreMHz, coreMin, coreMax, memoryMHz, memoryMin, memoryMax, canSet, reason, applyAtStart, notice }`: clock offsets (NVML `nvmlDeviceGetClockOffsets`, P0); min/max are the driver's range narrowed to RigControl's caps (core −500…+300, memory −1000…+1500 MHz) |
| `nvidia.setOverclock` | `{ coreMHz?, memoryMHz?, applyAtStart? }` | same as `getOverclock` (needs admin; `unsafe` outside the range). Voltage is never changed. |
| `nvidia.resetOverclock` | – | same as `getOverclock`, both offsets 0 |
| `nvidia.getDriverSettings` | `{ profile?: string }` (omit = global) | `DriverSetting[]` |
| `nvidia.setDriverSetting` | `{ key, value, profile?: string }` | `DriverSetting` |
| `nvidia.resetDriverSetting` | `{ key, profile?: string }` | `DriverSetting` |
| `nvidia.listProfiles` | `{ query?: string }` | `{ name, programs: string[], isPredefined, modifiedCount }[]` (max 200) |
| `nvidia.addProgram` | `{ exePath }` | `{ name, programs, isPredefined, modifiedCount }` (finds or creates a profile) |
| `nvidia.getDisplays` | – | `Display[]` |
| `nvidia.setDisplayMode` | `{ displayId, width, height, refreshHz }` | `{ applied: true, revertToken, revertInSeconds }` |
| `nvidia.confirmDisplayMode` | `{ revertToken }` | `{ kept: true }` |
| `nvidia.revertDisplayMode` | `{ revertToken }` | `{ reverted: true }` |
| `nvidia.setVibrance` | `{ displayId, value }` | `Display` |
| `nvidia.checkDriverUpdate` | – | `{ installed, latest, newer, downloadUrl, checkedAt, error }` |

```ts
type DriverSetting = {
  key: string;                      // stable key, e.g. "vsync", "maxFrameRate", "lowLatency"
  name: string; group: string; description: string;
  kind: "enum" | "number" | "toggle";
  value: number;                     // raw driver value
  defaultValue: number | null;
  isDefault: boolean;
  options: { value: number; label: string }[] | null;   // for enum / toggle
  min: number | null; max: number | null; unit: string | null;  // for number
};

type Display = {
  id: string; name: string; primary: boolean;
  width: number; height: number; refreshHz: number;
  modes: { width: number; height: number; refreshHz: number }[];
  vibrance: null | { value: number; min: number; max: number; defaultValue: number };
};
```

Display-mode safety: `setDisplayMode` applies the mode and starts a timer; unless `confirmDisplayMode` arrives within
`revertInSeconds` (15) the previous mode is restored automatically.

---

## cooling (NZXT Kraken Elite and NZXT controllers over USB HID)

| Method | Params | Result |
|---|---|---|
| `cooling.getDevices` | – | `CoolingStatus` |
| `cooling.setFixed` | `{ deviceId, channel, dutyPercent }` | `CoolingDevice` |
| `cooling.setCurve` | `{ deviceId, channel, points: CurvePoint[] }` | `CoolingDevice` |
| `cooling.setLcd` | `{ deviceId, brightness?: number, orientation?: 0\|90\|180\|270 }` | `CoolingDevice` |
| `cooling.closeCam` | `{ disableAutoStart?: boolean }` | `CoolingStatus`. User-triggered only. Closes the NZXT CAM app and stops its background service so RigControl can take over; with `disableAutoStart` it also stops CAM starting with Windows. `needsAdmin` unless elevated (the service). |
| `cooling.setCamAutoStart` | `{ enabled: boolean }` | `CoolingStatus`. Turns CAM's start-with-Windows (startup entry + service start type) on or off. `needsAdmin` unless elevated. |
| `cooling.rescan` | – | `CoolingStatus` |

```ts
type CoolingStatus = {
  blockedBy: string[];               // e.g. ["NZXT CAM"]: while non-empty RigControl never writes to the devices
  devices: CoolingDevice[];
  cam: { appRunning: boolean; serviceRunning: boolean; startsWithWindows: boolean };
};

type CoolingDevice = {
  id: string; name: string; product: string; firmware: string | null;
  connected: boolean;
  liquidTempC: number | null;
  channels: CoolingChannel[];
  lcd: null | { brightness: number; orientation: number };
  notes: string[];
};

type CoolingChannel = {
  id: string;                        // "pump", "fan", "fan1", ...
  name: string;
  kind: "pump" | "fan";
  rpm: number | null;
  dutyPercent: number | null;
  minDutyPercent: number;
  curveTempRange: { min: number; max: number } | null;   // null => only fixed duty
  maxCurvePoints: number | null;
  curveSource: "liquid" | null;      // what the device's own firmware follows
  storedOnDevice: boolean;           // true => keeps running after RigControl exits
  mode: "unknown" | "fixed" | "curve";
  fixedDutyPercent: number | null;
  curve: CurvePoint[] | null;
};
```

Event `cooling.status` → `CoolingStatus`, about once per second.

---

## lighting (OpenRGB SDK server)

| Method | Params | Result |
|---|---|---|
| `lighting.getStatus` | – | `LightingStatus` |
| `lighting.startOpenRgb` | – | `LightingStatus` (starts the installed OpenRGB as a hidden server and connects). RigControl also does this by itself at startup when OpenRGB is installed. |
| `lighting.installOpenRgb` | – | `{ started: boolean, message }`. Opens OpenRGB's own download page in the browser (winget has no current package, and RigControl never installs programs itself). |
| `lighting.setColor` | `{ deviceIds?: number[], color }` (omit ids = every device) | `LightingStatus`. Without `deviceIds` ("Everything at once") the colour also goes to every paired network light whose brand takes part (`joinsEverything`) and can take a colour; black switches them off. Then it works without OpenRGB too, as long as a network light is paired. A network light that fails shows a `problem` on its row; the call only fails (`unavailable`, naming them) when OpenRGB isn't connected and every network light it tried failed. |
| `lighting.setZoneColor` | `{ deviceId, zoneId, color }` | `LightingStatus` |
| `lighting.setZoneSize` | `{ deviceId, zone, leds }` | `LightingStatus`. Sets how many lights hang on a header (a strip, or daisy-chained fans). Only for zones with `resizable: true`, within `ledsMin`..`ledsMax`; Lian Li fan hubs take multiples of 16 only. Call it only when the user asks: OpenRGB saves the number itself and keeps it after a restart. Errors: `invalidArgs` (unknown device/zone, size outside the limits), `unavailable` (fixed zone, device left to its own program, OpenRGB not running). |
| `lighting.setMode` | `{ deviceId, modeId, colors?: string[], speed?: number, brightness?: number }` | `LightingStatus` |
| `lighting.off` | `{ deviceIds?: number[] }` | `LightingStatus`. Without `deviceIds` it also switches off every paired network light whose brand takes part (`joinsEverything`), as `setColor` above. |
| `lighting.listScenes` | – | `{ name, savedAt }[]` |
| `lighting.saveScene` | `{ name }` | `{ name, savedAt }[]` |
| `lighting.applyScene` | `{ name }` | `LightingStatus`. Scenes hold the PC's own lights (through OpenRGB) only: network lights are not saved in them, because several brands can't say what they show (Govee) or show an effect a scene couldn't rebuild. Applying one is still a change by hand, so it stops the effects. |
| `lighting.deleteScene` | `{ name }` | `{ name, savedAt }[]` |

On Linux and macOS (no `lighting.openrgbServer`): OpenRGB is never looked for or started. `openRgb.installed` and
`openRgb.path` say nothing useful there; `startOpenRgb` only connects to an OpenRGB the user started (and RigControl
looks every 10 seconds by itself); `installOpenRgb` opens openrgb.org. `getSync` and `listSyncSources` list 8
effects (no `screen`, no sound effects) and no capture sources, and a saved effect that is not offered reports and
plays as `solid`. `setSync` with `effect` `screen`, `visualizer`, `ripple` or `audiocolor`, with `bass`, `mid` or
`treble` true, or with a capture source fails with `unavailable` and a message that starts with
`Not available on this system`, and changes nothing; so do `takeOverGcc` and `giveBackGcc`. `getGcc` answers
`installed: false`.

```ts
type LightingStatus = {
  openRgb: { installed: boolean; path: string | null; running: boolean; connected: boolean;
             port: number; version: string | null; message: string | null };
  devices: LightDevice[];
  conflicts: string[];               // other RGB software currently running, e.g. "Armoury Crate"
};

type LightDevice = {
  id: number; name: string; type: string; vendor: string;
  ledCount: number;
  zones: { id: number; name: string; ledCount: number;
           ledsMin: number; ledsMax: number;       // the device's own limits for this zone
           resizable: boolean }[];                 // true = the user may set ledCount with lighting.setZoneSize
  modes: { id: number; name: string; colorCount: number; supportsSpeed: boolean; supportsBrightness: boolean;
           speedMin: number|null; speedMax: number|null; brightnessMin: number|null; brightnessMax: number|null }[];
  activeModeId: number;
  color: string | null;              // the most common LED colour right now, for swatches
};
```

Event `lighting.changed` → `LightingStatus`.

### lighting: network lights (Wi-Fi lights, local control only)

Lights paired over the user's own network: Nanoleaf, and whichever other brands this build has a provider for (see
`brands`). No cloud account is ever used. Pairing keys are kept protected for the Windows user in the settings file and
are only ever sent to the address they were paired with. Nothing is searched for until `scanNetworkLights` is called.

| Method | Params | Result |
|---|---|---|
| `lighting.getNetworkLights` | – | `NetworkLights`. Sends nothing to the network by itself (it only reads each paired light's description once, in the background). |
| `lighting.scanNetworkLights` | `{ brand?: string }` (omit = every brand) | `NetworkLights` once the search is over (every brand at the same time, each for a bounded time; allow 35 s). Call only on the user's click. Only networks with a router are searched (never a VPN adapter or a virtual switch), and at most 64 lights of a brand are listed. When the user stopped it, the result has `scanStopped: true` and `scanFinished` names the brands that were done: only those count as searched. Errors: `invalidArgs` (unknown brand), `unavailable` (a search is already running). |
| `lighting.cancelNetworkScan` | – | `NetworkLights`. Stops a running search. What the brands that were already done had found is kept; a brand that was still searching keeps what it had found earlier. The stopped `scanNetworkLights` call then returns with `scanStopped: true`. |
| `lighting.pairNetworkLight` | `{ brand, address, options?: { [key]: value } }` | `NetworkLights`. `address` is a private IPv4 address (a found light's, or typed by the user when the brand's `manualAddress` is true). `options` answers the brand's `options`. Some brands wait for a button press: allow 50 s. Errors: `invalidArgs` (unknown brand, address not on the home network, bad option), `unavailable` (the light refused or didn't answer; the message says what to do, and starts with "Not paired:" when the device paired but has nothing RigControl can colour). A light that paired but couldn't be read straight away is still saved: it comes back in `paired` with a `problem`, and is read again in the background. |
| `lighting.forgetNetworkLight` | `{ id }` (a paired light's `id`) | `NetworkLights`. The light is put back as it was if it was following; its box stays on the layout as "not connected", so pairing it again puts it back in place. Error: `invalidArgs` (not paired). |
| `lighting.setNetworkLight` | `{ id, on?: boolean, brightness?: number, color?: "#RRGGBB" }` (at least one) | `NetworkLights`. Changes one paired light by hand, without an effect. `brightness` is 0-100 (a brand without 0 takes its lowest). A colour or a brightness without `on` also switches the light on; black switches it off. The first change remembers what the light was doing (kept with the pairing, so it survives a restart) and the row shows `takenOver: true`. A change by hand stops the light sync (`lighting.getSync` → `enabled: false`), as a change on the PC's own lights does; a light the effect was driving is first put back as it was. Changes to one light go one at a time, at most as often as its brand allows (Yeelight: every 2.5 s; Hue: every second). Errors: `invalidArgs` (not paired, nothing to change, brightness outside 0-100, colour not `#RRGGBB`), `unavailable` (the brand can't do that: the message is the reason from `controls`; or the light refused or didn't answer, in plain words, also shown as the row's `problem`). |
| `lighting.giveBackNetworkLight` | `{ id }` | `NetworkLights`. Puts a light RigControl took over back to what it was doing before the first change by hand (its own effect, scene or colour, brightness and on or off, as far as its brand allows), then forgets that it took it over. Also stops the light sync. Errors: `invalidArgs` (not paired, or not taken over), `unavailable` (the brand can't give back, with the reason, or the light didn't answer). |

```ts
type NetworkLights = {
  brands: NetworkBrand[];            // every brand this build can drive
  paired: NetworkLight[];
  found: FoundLight[];               // from the last search(es)
  scanning: boolean;
  scanProblems: string[];            // plain sentences, e.g. "WLED: the search took too long and was stopped."
  notSupported: { brand: string; name: string; why: string }[];   // asked-for things that can't be done locally, and why
  scanStopped: boolean;              // the last search was stopped by the user before every brand had finished
  scanFinished: string[];            // brands whose search ran to its end in the last search
};

type NetworkBrand = {
  brand: string;                     // id used in calls and keys, e.g. "wled"
  name: string;                      // "WLED"
  tested: boolean;                   // false = built but not tried on real hardware: the page must say so
  notes: string;                     // what this brand can and can't do here
  steps: string[];                   // pairing instructions, in order
  returnsSecret: boolean;            // true = the light hands over a key at pairing (kept protected)
  manualAddress: boolean;            // true = the user may type the light's address
  options: { key: string; label: string; choices: { value: string; label: string }[]; default: string | null }[];
  nothingFound: string | null;       // what to check when a search finds none of this brand
  maxFramesPerSecond: number;
  canSearch: boolean;                // false = can't be searched for (Cololight): show the address box only, no search button
  followsUnplaced: boolean;          // false = a paired light is left alone until it is placed on the layout (Philips Hue)
};

type NetworkLight = {
  id: string;                        // also its key on the layout: "net|<brand>|<deviceId>" ("nanoleaf|<name>|<serial>" for a Nanoleaf)
  brand: string; brandName: string;
  deviceId: string;                  // the device's own id (serial number, MAC address)
  name: string; model: string; address: string;
  tested: boolean;
  kind: "single" | "strip" | "points";   // one colour / lights in a row / lights with known places
  points: number;                    // how many light points (0 until it has been read)
  pointName: string;                 // "panel", "LED", "light"
  placed: boolean;                   // has a box on the layout
  streaming: boolean;                // takes colours from RigControl right now
  problem: string | null;            // plain sentence when it doesn't answer
  note: string | null;
  controls: NetworkLightControls | null;  // what it can be told directly; null for a brand this version doesn't know
  on: boolean | null;                // as last read from the light (null = not known, or the brand can't say)
  brightness: number | null;         // 0-100, as last read
  color: string | null;              // "#RRGGBB" at full strength, as last read; null for an effect or a white
  takenOver: boolean;                // RigControl changed it by hand and remembers what it did before
  joinsEverything: boolean;          // false = left out of "Everything at once" and "off" (a Hue Bridge: it can be a whole house)
  followsUnplaced: boolean;          // false = the effects leave it alone until it is placed (a Hue Bridge)
};

type LightAbility = { can: boolean; why: string | null };   // why = a plain sentence when it can't
type NetworkLightControls = { power: LightAbility; brightness: LightAbility; colour: LightAbility; readBack: LightAbility; giveBack: LightAbility };

type FoundLight = { brand: string; brandName: string; deviceId: string; name: string; model: string; address: string; paired: boolean };
```

Layout: every paired light is one zone in `lighting.getLayout` with `key` = its `id` and `zone` = -1. Place it with
`lighting.setLayout` like any other zone. A `strip` is cut into equal slices along the longer side of its box
(`reverse` turns it round); `points` take the picture under their own place inside the box; a `single` light takes
its whole box. While an effect or the screen sync runs, a placed light follows its part of the picture; a paired
light that is not placed follows the one overall colour, unless its brand has `followsUnplaced: false` (a Philips
Hue Bridge is left alone until it is placed, because one bridge can drive a whole house). A placed Hue Bridge drives the colour lights of its
entertainment area only (every colour light when the bridge has no such area). When the sync stops, or the light
leaves the layout, it is put back as it was; the log says so only when that worked.

What each brand can be told directly (`controls`), as built:

| Brand | On/off | Brightness | Colour | Read back | Give back |
|---|---|---|---|---|---|
| Nanoleaf | yes | yes | yes | on, brightness, colour (when a plain colour) | its effect (or colour), brightness, on/off |
| WLED | yes | yes | yes (Solid, on the segments selected in WLED) | on, brightness, colour (when Solid) | each segment's effect, speed, palette and colours, brightness, on/off, and a running playlist |
| LeetDesk Aura | yes | yes | no (nothing published about it) | on, brightness | brightness and on/off |
| Philips Hue | yes | yes | yes | on, brightness, colour (when they agree) | each light's colour or white, brightness, on/off |
| Govee | yes | yes | yes | no (answers only reach a fixed port RigControl doesn't wait on for this) | no (nothing to put back without reading it) |
| Yeelight | yes | yes | yes | on, brightness, colour | its colour, white or hue, brightness, on/off (a colour flow comes back as its plain colour) |
| Cololight | yes | yes | yes | on, brightness, colour (when a plain colour) | the effect or colour it showed, brightness, on/off |

The list reads each light that can say what it shows at most every 15 seconds while the page asks for
`getNetworkLights`, never while an effect drives it.

Lights on the network do not need OpenRGB. With at least one paired, the effects (`lighting.setSync { enabled: true }`)
can be switched on and keep playing on them while OpenRGB is not connected; the sync's `status` then ends in
"(the PC's own lights are waiting for OpenRGB)". With none paired and OpenRGB not connected, switching the effects on
fails with `unavailable`.

The older Nanoleaf calls still work and act on the same paired Nanoleaf: `lighting.getNanoleaf`,
`lighting.findNanoleaf`, `lighting.pairNanoleaf { ip }`, `lighting.forgetNanoleaf`, each returning
`{ paired, ip, name, panels, streaming, problem, found }`.

## Additions: cooler screen and Armoury Crate

### cooling (screen)
- Device objects gain `screen` (null when the cooler is not open): `{ mode: "unset"|"liquid"|"infographic"|"media", layout, primary, secondary, accent, mediaName, live, problem, layouts[], sources[] }`.
- `cooling.setLcdScreen { deviceId, mode, layout?, primary?, secondary?, accent?, path? }` -> device. User-triggered. `infographic` puts the screen in live mode and sends a frame about once a second; `media` stores a picture/GIF (given by `path`) in the cooler; `liquid` returns to the cooler's own screen. Errors: `unavailable` (CAM running, screen channel busy, cooler did not answer), `invalidArgs`.
- `cooling.getLcdPreview { layout?, primary?, secondary?, accent? }` -> `{ image }` (PNG data URL). Sends nothing to the cooler.
- `cooling.setLcd` while live: rotation is applied to the frames RigControl draws; brightness uses the live-mode command.

### sensors (Armoury Crate)
- `sensors.getArmouryCrate` -> `{ installed, running[], startsWithWindows }`.
- `sensors.closeArmouryCrate { disableAutoStart }` -> state. Needs administrator (`needsAdmin`). User-triggered only.
- `sensors.setArmouryCrateAutoStart { enabled }` -> state. Needs administrator. Switching off records which services/tasks were changed so switching on restores only those.

## Additions: recording, driver install, DLSS/RTX settings

### capture (new module)
- `capture.getStatus` -> `{ available, problem, recording, recordingSince, recordingFile, replayRunning, lastSaved, hotkeyProblems[], settings }`. Event `capture.status` carries the same object; `capture.notice { kind, text }` feeds the on-screen overlay.
- `capture.startRecording` / `capture.stopRecording` / `capture.saveReplay` / `capture.openFolder` -> status.
- `capture.setSettings { folder?, fps?, bitrateMbps?, codec?, display?, systemAudio?, replayEnabled?, replaySeconds?, overlayStats?, recordHotkey?, replayHotkey?, overlayHotkey? }` -> status.
- Screen is captured with the bundled ffmpeg (`ddagrab` -> NVENC); system sound is fed in through a pipe from WASAPI loopback. Instant replay is a ring of 5-second MPEG-TS pieces in `<data>\replay-buffer`, deleted when switched off and on exit.
- `app.pickFolder { title? }` -> `{ path }`.

### nvidia
- `nvidia.installDriver` -> `{ started, version }`. Downloads only from `*.download.nvidia.com` over https, requires a valid Authenticode signature by NVIDIA Corporation, then starts NVIDIA's installer. Progress event `nvidia.driverDownload { version, receivedMB, totalMB, percent }`. `checkDriverUpdate` gains `canInstall`.
- New curated settings (group "DLSS and RTX"): `dlssOverride`, `dlssPreset`, `dlssRayReconstruction`, `dlssFrameGen`, `smoothMotion`, `rtxHdr`, `rtxHdrProcessing`. Ids from NVIDIA Profile Inspector's CustomSettingNames.xml; each is offered only if the installed driver knows it.

## Additions: health check, first-time setup, Discord attach

### health (new module)
Looks for other programs and Windows settings that get in RigControl's way and explains them. It only looks: it never
closes, changes or installs anything.
- `health.getReport` -> `HealthReport` (the last check; checks first if there is none yet). `health.rescan` -> `HealthReport` (always checks). Event `health.report` carries the same object, on the first check and whenever the findings change. The check also runs every 60 s.
- `HealthReport = { scannedAt, findings: Finding[], counts: { blocks, warn, info }, system }`. Findings are sorted blocks, warn, info; then by module and name.
- `Finding = { id, module, name, kind, severity, whatItBreaks, whatToDo, verified, fix, evidence }`. `module` is one of `sensors`, `cooling`, `lighting`, `capture`, `audio`, `nvidia`, `drivers`, `presence`, `overlay`, `bios`, `system`. `kind` is `process`, `service`, `driver`, `registry`, `setting` or `task`. `severity` is `info`, `warn` or `blocks`. `verified: false` means the entry has not been tried next to a real install yet.
- `fix` is null or `{ label, method, args, confirm }`: a button RigControl already has (`lighting.takeOverGcc`, `sensors.closeArmouryCrate`, `overlay.setSettings`). The UI must show `confirm` and wait for a yes before calling `method` with `args`, then call `health.rescan`.
- `system = { elevated, fastStartup, memoryIntegrity, driverBlocklist, controlledFolderAccess, pendingRestart, gameBarRecording, gameBarBackgroundRecording, gameBarLogoButton, gameMode, dynamicLighting, windowsUpdateDrivers, sessionCount, otherSessionRunsRigControl }`. A boolean is null when it could not be read.

### app (first-time setup and carried programs)
- `app.getFirstRun` -> `FirstRunInfo = { done, elevated, autostart, restartSuggested, restartForAutostart, restartForEqualiser, otherUsersSignedIn, accountIsAdministrator, otherAccount, programFolderOpen, hardware: { label, value }[], features: { id, name, available, reason }[], helpers: Helpers, findings: Finding[] }`. `otherUsersSignedIn` counts other Windows users with a desktop right now; `accountIsAdministrator` is false for a standard Windows account (it can never start RigControl as administrator by itself); `otherAccount` is true when RigControl runs as administrator under a different account than the one signed in; `programFolderOpen` is true when programs without administrator rights can change RigControl's own folder. The system scan is built from what the modules already report; it waits a few seconds for modules that are still starting. `done` is true on a PC whose settings file was already there before this version (nothing is asked again); only a new data folder starts with `done: false`. The UI shows the setup when `done` is false, and its one step "RigControl needs these programs" also on a set-up PC while `helpers.needsYes` is true.
- `Helpers = { items: Helper[], consented, needsYes, pending, serviceWillMove, notCarried: string[], problems: string[] }` with `Helper = { id: "openrgb" | "pawnio" | "equalizerapo", name, what, licence, carried, installed }`. The helper programs are required: there is one yes for all of them and no yes or no per program. `carried` = this build has the program inside (a source build without the `third_party` files carries none); `installed` = it is on this PC (for OpenRGB: RigControl's own copy in its Program Files folder). `pending` = something carried is not set up yet (a program is missing, or an OpenRGB service does not run RigControl's copy). `needsYes` = `pending` and no yes yet: the setup step is shown and nothing is installed. `consented` = the yes exists, or nothing is pending (a PC that already has everything counts as a yes: it is never asked and nothing is installed again). `serviceWillMove` = an OpenRGB service exists and the yes will point it at RigControl's copy. `notCarried` = names of programs that are neither on the PC nor in this build. `problems` = what went wrong at the last try, one line per program.
- `app.installHelpers` -> `FirstRunInfo`. The "Install and continue" button: the user's one yes for all helper programs, then every carried program that is missing is installed and an existing OpenRGB service is moved to RigControl's copy (the call waits up to 4 minutes; give it a long timeout). Only a copy that runs as administrator takes the yes: it is saved where only administrators can write (`HKLM\SOFTWARE\RigControl\Bundled`, DWORD `allHelpers` = 1). Without administrator rights the call fails with `needsAdmin` and nothing is recorded anywhere; the UI restarts RigControl as administrator (`app.restartAsAdmin`) and the user presses the button again there. Nothing in the settings file ever counts as a yes. With nothing pending it changes nothing. After the yes a failed set-up is reported in `helpers.problems` (`pending` stays true, `needsYes` is false) and calling it again tries again; RigControl also tries at every start as administrator. After PawnIO was installed the sensors are told (`sensors.driverInstalled`) and open again with it; when Equalizer APO was installed the answer has `restartSuggested` and `restartForEqualiser` set. Errors: `needsAdmin`, `unavailable`. (Replaces `app.setHelperConsent { id, allow }`, which is gone.)
- `app.completeFirstRun { autostart?: boolean }` -> `FirstRunInfo`. Changes "start with Windows" only when the value differs from now (may show a UAC prompt; error `cancelled` leaves the setup unfinished), then marks the setup as done (settings section `firstRun`). `restartSuggested` is true for one of two reasons, each with its own flag: `restartForAutostart` (start with Windows was just turned on, or it is on and RigControl is not running as administrator; never on a standard Windows account) and `restartForEqualiser` (Equalizer APO was installed during the setup). It never installs anything before the yes; the UI does not offer to finish or skip the setup while `helpers.needsYes` is true (it offers "Install and continue" or "Quit RigControl"). "Start with Windows" is one scheduled task per Windows user (`RigControl-<SID>`); the single task older versions made (`RigControl`) only counts for, and is only changed by, the user it was made for.
- `app.restartPc { evenWithOtherUsers?: boolean }` -> `{ scheduled: true, seconds: 30 }`. Asks Windows to restart in 30 seconds (Windows shows its own notice). The UI asks "Restart your PC now?" first; nothing else calls it. While other Windows users are signed in it refuses (`unsafe`) unless `evenWithOtherUsers` is true: the UI sets that only after telling the user that their programs close too. `app.cancelRestartPc` -> `{ cancelled: boolean }` (false when no restart was counting down).
- `app.getBundled` -> `{ status, helpers: Helpers | null, oldApps, elevated }`. `helpers` is the same object as in `FirstRunInfo` (null while RigControl is starting); it replaces the earlier `items` list. Settings > Built in shows each program's state and, while `helpers.pending` is true, one "Install the missing programs" button: it opens the setup step when `needsYes`, restarts as administrator when not elevated, and otherwise calls `app.installHelpers`.

### presence on Linux and macOS
Discord is reached through its Unix socket (`discord-ipc-0` to `-9`) in the user's private runtime folder; a socket
in a folder that others can open (always `/tmp`) is never used. Capabilities: `presence.custom` and `presence.apps`.
The app card names a well-known running program from a short fixed list (process names only, no window title).
`presence.rescan`, `presence.play`, `presence.getArt`, `presence.setGameEnabled` and `presence.getExtension` fail with
`unavailable` and a message that starts with `Not available on this system`. There is no game list, no music or
watching card and no browser extension link there.

### presence (Discord attach)
The status card attaches to the Discord desktop app of the Windows user RigControl runs for; another user's or
session's Discord is never used and no stored Discord login is read.
- `presence.getStatus` / event `presence.status` gain `attachedUser: { id, name } | null` (can be set while the status is off), `discordState: "attached" | "found" | "notRunning" | "otherUser" | "noAnswer"`, `discordReason: string | null` (plain words; null when attached or found), `attachNote: string | null` (for 5 minutes after Discord switched account) and `extensionPort: number | null`.
- `discordRunning` is true only for this Windows session's Discord (`attached`, `found`, `noAnswer`).
- `presence.setCustomStatuses` refuses (`invalidArgs`) an `app_id` that RigControl uses for its own cards.
- Discord application ids: an official build carries them encrypted inside the program; they are not in the source, never logged and never returned by any call. `presence.getStatus` / `presence.status` gain `setUp: boolean` (false when the build has no id for any of its main cards: game, music, app), `setupNote: string | null` (plain words: what is missing and what to do; null when all three main cards have an id) and `ownAppIds: string[]` (the card names the user gave their own id for, never the ids). In a build without ids `status` reads "Discord status isn't set up in this build" while nothing can be shown; nothing is opened or sent for a card without an id. A game Discord knows and a custom status with its own `app_id` still show.
- Your own ids (for people who build RigControl themselves): settings section `presence`, entry `appIds`, a map of card name to id, for example `"presence": { "appIds": { "game": "…", "music": "…", "app": "…" } }`. Names: `game`, `music`, `app`, `netflix`, `hulu`, `disney`, `primeVideo`, `max`, `crunchyroll`, `paramount`, `peacock`, `appleTv`, `custom1` to `custom9`. Each id is 17 to 20 digits and may be used for one card only; an entry that is wrong is left out and named in the log (the name, not the id). An entry here wins over a built-in id.
- `presence.setAppIds { ids: { [name]: string } }` -> status. Replaces the whole `appIds` entry (an empty value removes a card's id; `{}` removes all) and takes effect at once: open cards are closed and opened again with the new ids. Everything is checked first; with one wrong entry nothing is saved. Errors: `invalidArgs` (unknown name, not 17 to 20 digits, a name twice, the same id for two cards).
- Card updates go out as fast as Discord allows: 5 per 20 seconds on each card's own connection. A change is sent at once while a place is free; otherwise only the newest state of that card is kept and it is sent the moment a place frees (older ones are never sent late, taking a card down is never lost). An unchanged card is not sent again, except as a keep-alive (game and watching every 20 s, music every 15 s, custom every 60 s), which is skipped when no place is free.
- The browser extension link listens on the first free port of 127.0.0.1:47610-47619 and answers only this Windows user's browser (anything else gets 403). Extension 1.4.0 and newer find the port by themselves. `GET /status?nonce=<16-64 hex digits>` adds `proof` = HMAC-SHA256(this user's secret, nonce) as hex; the copy of extension 1.5.0 that `presence.getExtension` hands out carries that secret (`link-token.js`) and only reports to an app that proves it. A copy without the secret (the `extension/` folder itself) cannot check and reports to the first RigControl that answers.
- `sensors.driverInstalled` -> `{ reopened: boolean }`. Opens the sensors again when the PawnIO driver was installed after RigControl started (it also notices by itself within a few seconds). Does nothing without administrator rights or when the driver was already in use.
- Health: per-user programs (the `presence` entries and the programs under `overlay`) only count when they run in this Windows user's own session. `sensors.armoury-crate` is found by running programs and services only; switched-on tasks in `\ASUS\` are their own note, `sensors.asus-startup-tasks` (info, no fix). `system.program-folder-open` (info) says that RigControl starts as administrator from a folder that programs without administrator rights can change.

### bios
- `bios.getAvailability` -> `{ available, reason, board, biosVersion }`. On any other board or BIOS version the module's status message starts with "Not available on this PC: " and the UI leaves the BIOS section out of the CPU page (the page itself belongs to the `cpu` module and is always shown).

### cpu (new module: the CPU page on any PC)
Who the processor is, and tuning through Windows' own power plan settings. No driver is used and nothing is written to
the processor (no MSR, voltage or AMD SMU writes). Intel is tested; AMD is built but has not been tested on real
hardware. Live readings are not repeated here: the page takes temperature, load, clock and power from
`sensors.snapshot`. The CPU page is in the menu on every PC; only its BIOS part depends on `bios` (see above).
- `cpu.getInfo` -> `CpuInfo = { name, vendor: "Intel" | "AMD" | "Other", vendorId, family, model, stepping, cores, threads, performanceCores, efficiencyCores, known, generation, codeName, line, summary, tested, note }`. `name` is the processor's own name (CPUID); `family`/`model`/`stepping` are the CPUID numbers with their extended parts added. `cores`/`threads` come from Windows (0 when unknown); `performanceCores`/`efficiencyCores` are null unless the processor has two kinds of cores. `known` = this version's list has the family and model; then `generation` ("13th gen Core", "Zen 4"), `codeName` (may be null for AMD) and `line` are set. `summary` is one line to show; for an unknown model it is the name plus "newer than this version knows". `tested` is true for Intel only; `note` is null or the plain reason ("AMD support is built but has not been tested on real hardware").
- `cpu.getPower` -> `CpuPower = { available, problem, planId, planName, hasBattery, changed, changedPlans: string[], settings: CpuPowerSetting[] }` for the power plan in use now. `available: false` with `problem` when Windows did not give its power plan (then `settings` is empty). `changed` = RigControl has changed this plan and can put it back; `changedPlans` names every plan it has changed.
- `CpuPowerSetting = { key, available, ac, dc, min, max, zeroMeansNoLimit, unit, options, coreTypesDiffer, originalAc, originalDc }`. `key` is `boostMode` (0 to 6, `options: { value, label }[]`), `maxStatePercent` (30 to 100), `minStatePercent` (0 to 100), `maxFrequencyMHz` (0 = no limit, else 800 to 10000) or `minCoresPercent` (core parking: 0 to 100). `ac` is the value while plugged in, `dc` on battery (only matters when `hasBattery`). `available: false` (values null) when Windows on this PC does not have the setting. `coreTypesDiffer` = on a processor with fast and efficient cores Windows keeps the setting once per kind and the two differ now (`ac`/`dc` show the first kind); always false on a processor with one kind of core. `originalAc`/`originalDc` are the values from before RigControl's first change, null while unchanged.
- `cpu.setPower { ac?: Values, dc?: Values }` -> `CpuPower`, with `Values = { boostMode?, maxStatePercent?, minStatePercent?, maxFrequencyMHz?, minCoresPercent? }` (whole numbers; a field left out is not changed). Every value is checked against its range and the minimum state may not be above the maximum. Before a setting is changed for the first time its values are saved in the settings file (section `cpu`, `powerOriginals`: plan id -> Windows setting id -> `{ ac, dc }`); if that save fails nothing is changed. Where Windows has the setting for the second kind of core, it gets the same value. The plan is applied at once. Errors: `invalidArgs` (out of range, `dc` on a PC without a battery, nothing given), `unavailable` (Windows has no such setting, or the plan could not be read or saved), `needsAdmin` (Windows refused the change).
- `cpu.resetPower` -> `CpuPower`. "Put back how it was": writes the saved values back in every plan RigControl changed (a plan deleted since is skipped), applies the plan in use, and forgets the saved values. Does nothing when nothing was changed. Errors: `needsAdmin`, `unavailable`.

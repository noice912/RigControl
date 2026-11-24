# OpenRGB integration spec (lighting via a local headless OpenRGB SDK server)

> **About this file.** These are research notes written while RigControl was being developed, before and during the
> first builds. They describe the protocol and what was observed on the one development PC at that time. Where they
> say how RigControl should behave, the code and [docs/API.md](../API.md) are what counts today.

Status: research spec for RigControl implementers. Written 2026-11-15. **Adversarially verified twice on 2026-11-15.** The first pass was interrupted before it wrote its log; its in-place edits (the ones worded "Corrected:") were all re-checked by the second pass. The second pass re-derived every protocol fact from the OpenRGB clone, re-read OpenRGB.NET HEAD from GitHub, re-queried the Codeberg release API, and recomputed every example packet. It did **not** re-run the live-PC checks (PnP, registry, services); those remain the original author's observations and are labelled as such. Second-pass changes are worded "**Pass 2:**". Read the "Verification log" at the end before relying on any item.
Scope: every lighting operation in RigControl goes through an OpenRGB process running as a local SDK server, driven over TCP by RigControl.

## 0. Sources, versions and how to read this document

| Item | Value |
|---|---|
| Reference clone | `ref/OpenRGB`, commit `d09a493f8b44bcfb6482c09bf8c4cfb4eb5d4791`, 2026-11-15 01:54 -03:00 ("Add NZXT Kraken 2024 Plus (1E71:3014) controller"). `OpenRGB.pro:28-30` gives MAJOR=1, MINOR=1, SUFFIX=git, so this is **1.1 plus unreleased commits**, not a release build. |
| Latest OpenRGB releases (web, Codeberg release API) | `release_1.1` (2026-10-16) and `release_1.0` (2026-09-12). Windows assets: `OpenRGB_1.1_Windows_64_8e16de2.msi` and `OpenRGB_1.1_Windows_64_8e16de2.zip`, from `https://codeberg.org/OpenRGB/OpenRGB/releases`. GitLab release pages now point to Codeberg for downloads. |
| SDK protocol version in the clone | `OPENRGB_SDK_PROTOCOL_VERSION 6` (`NetworkProtocol.h:28`). |
| OpenRGB.NET (NuGet) | Latest is **3.1.1** (2024-07-28), net8.0, MIT (nuget.org/packages/OpenRGB.NET). GitHub HEAD is commit `442b448` "3.1.1", so HEAD is the same code as the package. **Maximum protocol version 4.** |
| Web access | Available. I used nuget.org, the GitHub API and raw files for OpenRGB.NET, the Codeberg and GitLab release APIs, pawnio.eu, github.com/namazso/PawnIO, and web search for the winget id and the WinRing0 Defender detection. |
| Live PC checks (read only) | Get-PnpDevice, device properties, the registry (HVCI, CI config, uninstall keys), and Get-Service/Get-Process listings. No device was opened and nothing was installed or changed. **Pass 2:** these were made by the original author only and were not repeated. Every statement in sections 5-7 that starts from a PnP id, a SUBSYS value, an installed version or a registry value rests on that single observation. |
| Released 1.1 vs the clone | The clone is 1.1 plus unreleased commits. Nothing in this document was checked against a released binary. The 1.1 release notes (Codeberg API) list "Fix an issue with the OpenRGB SDK server device indices on protocol versions < 6", so **OpenRGB 1.0 has a known index bug below protocol 6: require OpenRGB ≥ 1.1**. |

Conventions:
- `file:line` refers to the OpenRGB clone unless stated otherwise.
- **UNVERIFIED** marks a statement I could not confirm in source or on the live PC. Each one says what would confirm it.
- All multi-byte integers on the wire are **little-endian**. The server `memcpy`s native x86 integers and never byte-swaps (for example `NetworkServer.cpp:3617`, `RGBController.cpp` `COPY_DATA_FIELD`).
- The clone is newer than the 1.1 release. Where the behaviour of the released 1.1 binary could differ from the clone, I say so.

---

## 1. Running OpenRGB as a headless SDK server

### 1.1 Command line

Source: `cli.cpp:1420-1896` (`cli_pre_detection`), `startup/main_Windows.cpp`, `startup/startup.cpp`, `ResourceManager.cpp:1215-1465`.

| Flag | Argument | Effect | Source |
|---|---|---|---|
| `--server` | none | Start the SDK server. It does **not** count as a "config arg", so `OpenRGB.exe --server` alone does not open the GUI. | `cli.cpp:1550-1553`, `1885-1893` |
| `--server-port N` | 1024-65535 | Set the port. It also implies `--server`. Out of range prints help and exits **with exit code 0** (`exit(0)`, `cli.cpp:1879-1883`), so a zero exit code does not mean success. | `cli.cpp:1558-1594` |
| `--server-host H` | host or IP | Set the bind address. It also implies `--server`. | `cli.cpp:1599-1615` |
| `--config DIR` | existing directory | Use DIR as the configuration directory. DIR must already exist, otherwise help is printed and the process exits with code 0. Note: the `ResourceManager` constructor runs before the CLI is parsed, so `%APPDATA%\OpenRGB\` is still created (and its `OpenRGB.json` read) on every launch even when `--config` is given (`ResourceManager.cpp:223-230`, `1109-1154`). **Pass 2:** it is also written: the constructor saves default `Server` keys and the `ProfileManager` constructor saves default profile keys into `%APPDATA%\OpenRGB\OpenRGB.json` when they are missing, and creates `%APPDATA%\OpenRGB\profiles\` (`ResourceManager.cpp:146-180`, `325-330`, `ProfileManager.cpp:91-150`, `1049-1054`). On Windows the path is taken from the wide command line, so non-ASCII paths work (`cli.cpp:1465-1466`). | `cli.cpp:1461-1482` |
| `--localconfig` | none | Use `./` as the configuration directory. | `cli.cpp:1452-1456` |
| `--nodetect` | none | Skip device detection. Useful only for protocol tests. | `cli.cpp:1487-1491` |
| `--noautoconnect` | none | Do not try to attach to an existing local server. **Not needed with `--server`**, because `tryAutoConnect = tryConnect && !startServer`. | `cli.cpp:1496-1500`, `ResourceManager.cpp:1221` |
| `--loglevel L` | 0-6 or `fatal`/`error`/`warning`/`info`/`verbose`/`debug`/`trace` | Set the log level. | `cli.cpp:1620-1685` |
| `-v`, `-vv`, `--print-source` | none | Console verbosity. Only visible when started from a console. | `cli.cpp:1843-1865` |
| `--gui`, `--startminimized`, `--i2c-tools`/`--yolo` | none | Force the GUI. **Do not use for headless operation.** | `cli.cpp:1801-1820` |
| `-h`/`--help`, `-V`/`--version` | none | Print and `exit(0)`. | `cli.cpp:1825-1838`, `1879-1883` |
| any other token | n/a | Treated as a post-detection CLI command (`--device`, `--mode`, `--color`, ...). Avoid it: RigControl must use the SDK instead. | `cli.cpp:1871-1874` |

**Pass 2, how the GUI decision is made** (`cli.cpp:1421`, `1885-1893`): the parser counts "config args" in `cfg_args` and starts the GUI when `argc - cfg_args <= 1`. `--server` adds 0, `--server-host` and `--server-port` add 1 each (although each consumes two tokens), `--config` and `--loglevel` add 2 each, `--nodetect`/`--noautoconnect`/`-v`/`-vv`/`--print-source`/`--localconfig` add 1 each. For the recommended line below, `argc` = 10 and `cfg_args` = 6, so `argc - cfg_args` = 4 and no GUI is started. A line made only of `--config DIR --loglevel L` (difference 1) **would** open the GUI. Always keep `--server` in the line.

**Recommended launch line** (RigControl owns its own config directory and creates it before launch):

```
"C:\Program Files\OpenRGB\OpenRGB.exe" --server --server-host 127.0.0.1 --server-port 6742 --config "%LOCALAPPDATA%\RigControl\openrgb" --loglevel info
```

- The default port is **6742** (`NetworkProtocol.h:44`). The default bind host in this clone is **127.0.0.1** (`NetworkProtocol.h:33`, `ResourceManager.cpp:1332-1342`). The OpenRGB.json `Server.default_host` and `Server.default_port` settings override the defaults when the CLI flags are absent (`ResourceManager.cpp:1330-1354`). Always pass `--server-host 127.0.0.1` explicitly anyway. **UNVERIFIED:** older OpenRGB builds may have bound to all interfaces by default. The 1.0rc3.1 release note says the background service runs "to localhost only".
- **Pass 2:** connect to the literal address `127.0.0.1`, never to the name `localhost`. With `--server-host 127.0.0.1` the server has one IPv4 socket only (`NetworkServer.cpp:494-510`), and "local client" status is granted only when the peer address is exactly `127.0.0.1` or `::1` (`NetworkServer.cpp:1027-1048`).
- **Pass 2:** the sockets are bound in `StartServer()`, but `listen()` is called on a separate connection thread (`NetworkServer.cpp:578-589`, `983`), so a connect attempt can be refused for a short moment after the process starts. The retry loop in 1.2 covers this.
- **Pass 2:** the log file is `<config>\logs\OpenRGB_#.log` by default (`LogManager.cpp:167-200`, keys `LogManager.log_file`, `logfile`, `file_count_limit`, `loglevel`, `log_console`). RigControl can read it for diagnostics.
- **No window.** In headless mode `startup()` never creates a `QApplication` or dialog (`startup.cpp:67-176`). The Windows entry point calls `AttachConsole(ATTACH_PARENT_PROCESS)` only to reuse a parent console (`main_Windows.cpp:453-463`), which implies a GUI-subsystem exe with no console of its own. **UNVERIFIED:** `OpenRGB.pro` has no `CONFIG += console`, so qmake should default to the Windows subsystem, but I did not inspect a release binary's PE header. Confirm with `dumpbin /headers OpenRGB.exe` (look for subsystem 2) or a test launch. Launch it from .NET with `ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true }` either way.
- A **single-instance gotcha**: if the user starts the OpenRGB GUI while our server runs, the GUI auto-connects to the local server as a client instead of detecting devices itself (`ResourceManager.cpp:1231-1280`). That is fine. **Corrected:** do **not** rely on a second `--server` instance failing to bind. The server sets `SO_REUSEADDR` on its listening socket (`NetworkServer.cpp:521`). On Windows that option lets a second socket bind the same address and port, so a second instance will most likely bind successfully and both processes will detect and drive the same hardware. **UNVERIFIED** by test (no OpenRGB on the development PC). RigControl must therefore check for an existing listener on 127.0.0.1:6742 (for example `IPGlobalProperties.GetActiveTcpListeners()`) **and** for a running `OpenRGB` process or service before launching its own instance.

### 1.2 How the client knows the server is ready

Order of events in `ResourceManager::Initialize` (`ResourceManager.cpp:1359-1390`):

1. The server socket is created, bound and listening (`InitializeServer`, then `server->StartServer()`). This happens **before** detection.
2. `DetectionManager::BeginDetection()` runs asynchronously. Each controller registration triggers `UpdateDeviceList()` and a `NET_PACKET_ID_DEVICE_LIST_UPDATED` (id 100) to every connected client (`ResourceManager.cpp:62-66`, `1064-1067`, `NetworkServer.cpp:394-395`, `4039-4043`).

Readiness ladder for RigControl:

| Stage | Test |
|---|---|
| Process alive | `Process.HasExited == false`. **Corrected:** exit code 1 means the server was not online after initialisation (`startup.cpp:164-175`), but the process does **not** exit promptly: `common_main` still calls `ResourceManager::ServiceShutdown()`, which waits for the (already started, asynchronous) device detection to finish, and then `DetectionManager::Cleanup()` (`main_Windows.cpp:648-653`, `ResourceManager.cpp:894-913`). So a failed server still opens and probes every device, and exits only after detection. Exit code 0 can also mean a CLI argument error (help printed). Do not use exit timing as the readiness signal; use the port test below with an overall timeout. |
| Port open | TCP connect to 127.0.0.1:6742 succeeds. Retry every 250 ms for up to 15 s. |
| Protocol alive | Send `REQUEST_PROTOCOL_VERSION`. Expect a 4-byte reply within 1 s (OpenRGB's own client waits 1 s: `NetworkClient.cpp:1781-1803`). |
| Detection finished, protocol ≥ 6 | The server sends `NET_PACKET_ID_DETECTION_COMPLETE` (103) to protocol-6 clients only (`NetworkServer.cpp:876-885`, `3971-3983`). It is a one-shot broadcast to clients connected **at that moment**: a client that connects after detection finished never receives it, and there is no packet to query detection state. Always combine it with the timeout/stability heuristic of the next row. **Pass 2:** on Windows it may also precede the HID controllers (see "HID devices arrive through hot-plug" below), so apply the stability heuristic after it as well. |
| Detection finished, protocol ≤ 5 | No explicit signal. Treat detection as finished when no `DEVICE_LIST_UPDATED` has arrived **and** `REQUEST_CONTROLLER_COUNT` has been stable for 3 s. **UNVERIFIED** heuristic: SMBus/DRAM detection on the development PC may take several seconds. Measure and tune on the PC. |

**Pass 2, HID devices arrive through hot-plug on Windows.** The Windows build defines `HID_HOTPLUG_ENABLED=1` and links `hidapi-hotplug` (`OpenRGB.pro:395`, `416-424`). With it, `BackgroundDetectDevices` does not walk an enumerated HID list; it registers hidapi hot-plug callbacks with the `HID_API_HOTPLUG_ENUMERATE` flag and every HID controller is created inside the "device arrived" callback (`DetectionManager.cpp:853-858`, `2111-2169`). Consequences:
- The detection order is I2C buses, I2C detectors, DRAM detectors, I2C-PCI (GPU) detectors, HID, USB serial, other (`DetectionManager.cpp:825-878`).
- **UNVERIFIED:** whether the callbacks for already-connected devices run before `hid_hotplug_register_callback` returns. The clone ships hidapi-hotplug as a binary only. If they run later on hidapi's own thread, `DETECTION_COMPLETE` (103) can be sent **before** the ASUS, NZXT and Razer controllers exist. Never treat 103 as "the list is final". Confirm by logging the order of 100 and 103 on the PC.
- Unplugging a HID device unregisters and deletes its controller, and re-plugging creates a new one, each with a `DEVICE_LIST_UPDATED` (`DetectionManager.cpp:2171-2206`). The client must be able to rebuild its device list at any time, not only at start-up.
- **UNVERIFIED:** that the released 1.1 Windows binary was built this way.

Detection settings that affect timing live in `OpenRGB.json` → `"Detectors"`: `initial_detection_delay_ms` and `hid_safe_mode` (`DetectionManager.cpp:741-772`). Per-detector enable flags live under `"Detectors"."detectors"."<detector name>": true|false` (read at `DetectionManager.cpp:1018-1020` and the other detector loops; defaults written at `1878-2010`). The key is the detector's registered name string, and one key covers every registration with that name (for example `"NZXT RGB & Fan Controller"` covers PIDs 2009/2010/200E/2011/2019/2020/201F, and `"NZXT RGB Controller"` covers 2012 and 2021: `NZXTHue2ControllerDetect.cpp:50-56`, `67-68`).

**Pass 2, rules for pre-seeding `Detectors.detectors`:**
- A key that is missing is added with the detector's built-in default, which is `true` for every detector named in this document (`DetectionManager.h:190-197`, `533-537`). A device is therefore probed unless its key is explicitly `false`.
- A key whose name matches no registered detector is **deleted** before detection and the file is re-saved (`DetectionManager.cpp:2020-2104`, log line `Removing stale detector "…" from settings`). A misspelt name silently leaves the device enabled. After the first run, re-read the file and check that every key RigControl wrote is still there.
- Disabling a device's specific detector makes it eligible for the one generic detector, `"HID LampArray Device"` (HID usage page 0x59, usage 0x01, any VID/PID: `HIDLampArrayControllerDetect.cpp:50`, `DetectionManager.cpp:1537-1600`). To keep OpenRGB away from a device, set **both** its own detector and `"HID LampArray Device"` to `false`. **UNVERIFIED** which devices on the development PC expose a LampArray collection.
- Every enabled I2C detector runs against every available bus and applies its own filter (`DetectionManager.cpp:1000-1050`). Once PawnIO gives OpenRGB the chipset SMBus, far more than the ENE DRAM detector will probe it (section 5). The full list of detector names is only known after one detection run, when OpenRGB writes them all into `OpenRGB.json`.

On the first completed detection of a headless server, OpenRGB loads the `ProfileManager.service_startup_profile` profile if it is enabled (`ResourceManager.cpp:881-892`, `ProfileManager.cpp:442-444`, `1274-1299`). All auto-profiles default to `enabled: false` (`ProfileManager.cpp:91-143`). RigControl's pre-seeded `OpenRGB.json` must keep them disabled.

**Pass 2:** every newly registered controller also gets its saved manual configuration re-applied (`LoadControllerConfiguration`, from `<config>\Configuration.json`) and, if a profile is active, that profile's state written to the device (`LoadControllerActiveProfile`) before it appears in the list (`DetectionManager.cpp:467-507`, `ProfileManager.cpp:452-467`). With no active profile the second step does nothing.

### 1.3 Shutting it down cleanly

There is **no SDK packet that stops the server**. I checked the packet table in `NetworkProtocol.h:102-209`. Options:

| Method | Clean? | Notes |
|---|---|---|
| Windows service stop (`SERVICE_CONTROL_STOP` / `PRESHUTDOWN`) | **Yes.** `WaitWhileServerOnline` sees the stop flag, calls `StopServer()`, then `ServiceShutdown()` (optional exit profile) and `DetectionManager::Cleanup()` (`main_Windows.cpp:208-240`, `524-536`, `630-653`). | Only applies when OpenRGB was installed as the `OpenRGB` service by the MSI service option (section 2). Stopping a service needs rights on the service, normally admin. |
| `Process.Kill()` / TerminateProcess on our own child | **No.** Skips `ServiceShutdown` and controller destructors. | Hardware keeps the last state it was sent. Acceptable for RigControl. Before killing, send the "exit" lighting state through the SDK and allow time for two asynchronous layers to flush: the server's per-controller packet queue threads (`NetworkServer.cpp:1084-1173`, `1488-1546`) and each controller's own device-call thread, because `RGBController::UpdateLEDs()`/`UpdateMode()` only set a flag that a background thread later writes to the device (`RGBController.cpp:2024-2029`, `2053-2058`, `2082-2170`). A v6 ACK is sent when the flag is set, not when the device write finishes. **UNVERIFIED:** the 300 ms figure is a heuristic. Measure it on the PC. |
| Ctrl+C/Ctrl+Break via a hidden console | **UNVERIFIED.** No `SetConsoleCtrlHandler` exists in the source (grep). The default handler calls ExitProcess, which skips the same cleanup. | Not recommended. |
| Closing a window | Not applicable. There is no window in headless mode. | |

**Recommendation:** RigControl starts OpenRGB as a child process, assigns it to a Win32 Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so it dies with RigControl, and on exit sends the final lighting state, waits 300 ms, then kills it. If the OpenRGB Windows service is installed, RigControl must **not** start its own instance. It connects to the service on 6742 instead. Detect the service with `ServiceController("OpenRGB")`.

---

## 2. Finding OpenRGB on Windows; config folder

### 2.1 Install locations and identification

| Fact | Value | Source / confidence |
|---|---|---|
| MSI default install dir | `%ProgramFiles%\OpenRGB\` (WiX `ProgramFiles64Folder` / `INSTALLDIR` Name=`OpenRGB`) | `scripts/build-msi.ps1:226-228`, with `$PRODUCTNAME="OpenRGB"` at line 40 |
| Executable name | `OpenRGB.exe` (`TARGET = OpenRGB`) | `OpenRGB.pro:59` |
| Uninstall registry entry | MSI product: DisplayName `OpenRGB`, Publisher `OpenRGB` (WiX `Product Name='OpenRGB' Manufacturer='OpenRGB'`), under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}`. ProductCode is random per build (`Id='*'`). UpgradeCode is a deterministic v5 UUID (`build-msi.ps1:79`). | `build-msi.ps1:151-156`. **Corrected:** the GitLab CI MSI job runs the bash `scripts/build-msi.sh`, not the `.ps1` (`.gitlab-ci.yml:687-705`). The `.sh` has the same `Product Name`/`ServiceInstall` lines (`build-msi.sh:197`). **UNVERIFIED** that the published Codeberg 1.1 MSI came from that CI job. Confirm by installing and reading the key. |
| Marker key | `HKCU\Software\OpenRGB\OpenRGB` (empty value, Start-menu shortcut component) | `build-msi.ps1:251` |
| MSI optional service | Feature `OpenRGBRegisterService` installs service **`OpenRGB`** (DisplayName "OpenRGB", Description "OpenRGB SDK Server"), Start=auto, account `[SERVICEACCOUNT]`. | `build-msi.ps1:236-241`, `260-262`. Neither script ever sets the `SERVICEACCOUNT` property (grep), and an empty `ServiceInstall` account means LocalSystem under Windows Installer rules. **UNVERIFIED** on the published MSI. Check `sc qc OpenRGB` after install. The MSI's own dialog calls the service option "experimental" and says settings do not synchronise between the GUI and the service (`build-msi.ps1:210`). |
| Service config dir | `<install dir>\service_config\` (created by the exe when started as a service). The service ignores command-line args and always runs `START_SERVER | NO_AUTO_CONNECT`. | `main_Windows.cpp:548-573` |
| Portable build | Yes. `OpenRGB_1.1_Windows_64_8e16de2.zip` is published next to the MSI on Codeberg. Run it from any folder. PawnIO modules (`SmbusI801.bin` etc.) must sit next to `OpenRGB.exe` (`i2c_smbus_pawnio.cpp:285-295`). | Codeberg release API (web) |
| winget id | `CalcProgrammer1.OpenRGB` (third-party winget catalogues list it). | **UNVERIFIED** which version winget currently ships (a catalogue page showed v0.8). Confirm with `winget show CalcProgrammer1.OpenRGB` (a read-only query, to be run by the user). **Never use the winget package if it is older than 1.0rc2:** OpenRGB 0.8 switched to WinRing0 (Codeberg release notes 0.8), which the development PC's vulnerable-driver blocklist rejects (section 6). RigControl never runs winget or installers itself. |
| Chocolatey | `openrgb` and `openrgb.portable` packages exist. | web search. Version not checked. |
| Present on the development PC now? | **No.** No OpenRGB uninstall entry, no `%ProgramFiles%\OpenRGB`, no `%APPDATA%\OpenRGB`, and no PawnIO or WinRing0 service or driver. | Live read-only checks, 2026-11-15 |

**Discovery algorithm for RigControl** (in order): (1) a path the user set in RigControl settings; (2) the `OpenRGB` service `ImagePath` (`HKLM\SYSTEM\CurrentControlSet\Services\OpenRGB\ImagePath`); (3) uninstall entries (HKLM, both views, and HKCU) with `DisplayName` starting with `OpenRGB`. If `InstallLocation` is empty (WiX does not set it by default), fall back to (4) `%ProgramFiles%\OpenRGB\OpenRGB.exe`; (5) a RigControl-managed portable copy (`%LOCALAPPDATA%\RigControl\OpenRGB\OpenRGB.exe`). Read the file version of the exe and log it. Read the SDK protocol version from the server at runtime, not from the file version.

### 2.2 Configuration folder

| What | Path | Source |
|---|---|---|
| Default config dir | `%APPDATA%\OpenRGB\` (created if missing) | `ResourceManager.cpp:1109-1154` |
| Settings file | `<config>\OpenRGB.json` | `ResourceManager.cpp:230`, `601` |
| Controller configuration (manual zone sizes, names) | `<config>\Configuration.json`. Written by `ProfileManager::SaveConfiguration()`, which the server calls after every SDK `RESIZEZONE` and `CONFIGUREDEVICE` (`NetworkServer.cpp:2887`, `2952`). | `ProfileManager.cpp:904-927` |
| Profiles (1.x) | `<config>\profiles\*.json`. Legacy `.orp` files are migrated and renamed `.orp.bak`. | `ProfileManager.cpp:1052`, `214`, `1113-1161` |
| Plugins | `<config>\plugins\` | `PluginManager.cpp:100` |
| Service mode | `<install dir>\service_config\` | `main_Windows.cpp:562-572` |
| Overridden by | `--config DIR` / `--localconfig` | `cli.cpp:1452-1482` |

Useful `OpenRGB.json` keys, all read from source:

| Key | Meaning |
|---|---|
| `Server.default_host`, `Server.default_port` | Defaults when no CLI flags are given (`ResourceManager.cpp:1330-1354`). |
| `Server.legacy_workaround` | Accept wrong `data_size` from protocol ≤4 clients (`ResourceManager.cpp:1418-1426`, `NetworkServer.cpp:3134`). |
| `Server.all_controllers` | Also expose controllers from other servers OpenRGB is a client of (`ResourceManager.cpp:1435-1449`). Leave false. |
| `Detectors.detectors.<name>` | Enable or disable an individual detector (`DetectionManager.cpp:1878-1908`). |
| `Drivers.shared_smbus_access` (default true), `Drivers.smbus_sleep_mode` | PawnIO SMBus global mutex and sleep behaviour (`i2c_smbus_pawnio.cpp:101`, `139-172`). |
| `ProfileManager.{open,exit,resume,suspend,service_startup,service_shutdown}_profile` | `{ "enabled": bool, "name": string }`, all default `enabled: false` (`ProfileManager.cpp:91-143`). Keep them disabled so OpenRGB never applies a profile on its own. |

RigControl should run OpenRGB with its **own** `--config` directory so it can pre-seed `OpenRGB.json` (for example, to disable unwanted detectors) without touching a user's GUI config. **UNVERIFIED:** the full schema with default values. Start OpenRGB once with `--config <dir>` and read the generated file.

---

## 3. The OpenRGB SDK protocol (enough to hand-roll a client)

Primary sources: `NetworkProtocol.h`, `NetworkServer.cpp`, `NetworkClient.cpp`, `RGBController/RGBController.cpp` (serialisers), `RGBController/RGBControllerInterface.h` (enums), and `Documentation/OpenRGBSDK.md`. Where the doc and the code disagree, the code wins and I note it.

### 3.1 Versions

| Ver | Release | Adds (wire-relevant) |
|---|---|---|
| 0 | 0.3 | Base protocol. Controller-data request has no payload. |
| 1 | 0.5 | `REQUEST_PROTOCOL_VERSION`; vendor string in device data. |
| 2 | 0.6 | Profile packets 150-153. |
| 3 | 0.7 | Mode `brightness_min/max/brightness`; `SAVEMODE` (1102). |
| 4 | 0.9 | Zone segments; plugin packets 200-201. |
| 5 | 1.0rc1 (header comment says rc2) | Zone flags, controller flags, LED display names, effects-only zones, `REQUEST_RESCAN_DEVICES`, `CLEARSEGMENTS`/`ADDSEGMENT`. |
| 6 | 1.0 | Unique controller IDs, ACK packets, server name/flags/hostname, client flags and "local client", detection progress packets, per-zone modes, `SIGNALUPDATE`, settings/log/profile-JSON packets; `mode_value` and `led_value` removed from the wire. |

Sources: `NetworkProtocol.h:16-28`, `OpenRGBSDK.md:9-19`. The doc still marks 6 as "unreleased", but the clone is 1.1+git with version 6, and the 1.1 release notes mention "protocol versions < 6". **UNVERIFIED** that the released 1.1 binary reports 6. Read the reply to `REQUEST_PROTOCOL_VERSION`.

### 3.2 Framing

Every packet, in both directions, is a 16-byte header followed by `pkt_size` bytes of payload (`NetworkProtocol.h:52-58`):

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | magic | ASCII `ORGB` = `4F 52 47 42` |
| 4 | 4 | `pkt_dev_id` (u32) | Controller index (protocol ≤5) or unique controller ID (protocol ≥6). 0 for non-controller packets. |
| 8 | 4 | `pkt_id` (u32) | Command id (table 3.3) |
| 12 | 4 | `pkt_size` (u32) | Payload length in bytes, excluding the header |

Server-side rules (`NetworkServer.cpp:1255-1326`): a bad magic byte closes the connection, and so does `pkt_size > 8 MiB` (`OPENRGB_SDK_MAX_PACKET_SIZE`, `NetworkProtocol.h:38`). An unknown `pkt_id` gets status `ERROR_UNSUPPORTED`, which is only visible to protocol-6 clients as an ACK (`NetworkServer.cpp:1584-1594`).

Strings: `u16 len` followed by `len` bytes **including the trailing NUL**. Exception: whole-packet strings such as client name, profile name and JSON use `pkt_size = strlen+1` with no length prefix. Encoding is raw bytes. **UNVERIFIED** for non-ASCII. Send UTF-8 and keep names ASCII.

Colour (`RGBColor`, 4 bytes) is a u32 built as `R | G<<8 | B<<16` (`RGBControllerInterface.h:23-29`). On the wire it is bytes **R, G, B, 0x00**.

### 3.3 Packet ids used by RigControl

Full list: `NetworkProtocol.h:102-209`.

| Id | Name | Dir | Min ver | Payload |
|---|---|---|---|---|
| 0 | REQUEST_CONTROLLER_COUNT | C→S, S→C | 0 | Request: empty. Reply: `u32 count`; at ≥6 followed by `count × u32 unique_id` (`NetworkServer.cpp:3592-3641`). |
| 1 | REQUEST_CONTROLLER_DATA | C→S, S→C | 0 | Request: ≤5: `u32 protocol_version` (any other payload size is treated as version 0); ≥6: payload ignored, the negotiated version is used (`NetworkServer.cpp:1111-1133`). Reply: Device Data block (3.5), header `pkt_dev_id` = the requested id (`NetworkServer.cpp:3693`). **If the id/index is invalid the server sends no reply at all** (at ≥6 only an OK ACK, see 3.4), so every data request needs a client-side timeout (`NetworkServer.cpp:1528-1546`, `3651`). **Pass 2:** at ≤5 the payload version is not clamped to the negotiated version. The server uses it as given both to choose the reply format and to decide whether `pkt_dev_id` is an index (<6) or a unique id (≥6) (`NetworkServer.cpp:1120-1132`, `3649`, `4474-4512`). Always send exactly the negotiated version. |
| 10 | ACK | S→C | 6 | `u32 acked_pkt_id, u32 status`; header `pkt_dev_id` = acked dev id (`NetworkServer.cpp:3568-3590`). |
| 40 | REQUEST_PROTOCOL_VERSION | C→S, S→C | 1 | Request: `u32 client_max`. Reply: `u32 server_max` (**not** the negotiated value: `NetworkServer.cpp:3710-3723`). The server stores `min(client_max, 6)` for this connection (`NetworkServer.cpp:1674-1697`). |
| 50 | SET_CLIENT_NAME | C→S | 0 | NUL-terminated string. No reply (an ACK at ≥6). |
| 51 | SET_SERVER_NAME | S→C | 6 | NUL-terminated string. Sent right after the version reply (`NetworkServer.cpp:1341-1345`, `3779-3798`). |
| 52 / 53 | SET_CLIENT_FLAGS / SET_SERVER_FLAGS | C→S / S→C | 6 | `u32 flags` (bits in 3.8). |
| 54 / 55 | SET_CLIENT_HOSTNAME / SET_SERVER_HOSTNAME | C→S / S→C | 6 | Strings. |
| 100 | DEVICE_LIST_UPDATED | S→C | 1 | Empty. Sent to **all** clients regardless of version (`NetworkServer.cpp:4039-4043`). |
| 101 / 102 / 103 | DETECTION_STARTED / PROGRESS / COMPLETE | S→C | 6 | Progress payload: `u32 data_size, u32 percent, u16 len, char[len]` (`OpenRGBSDK.md:368-385`). |
| 140 | REQUEST_RESCAN_DEVICES | C→S | 5 | Empty. Re-runs full detection, which re-probes (and writes to) every device, SMBus included (`NetworkServer.cpp:1731-1736`, `ResourceManager.cpp:935-965`). Do not send it automatically. |
| 150 | PROFILEMANAGER_GET_PROFILE_LIST | C→S, S→C | 2 | Reply: `u32 data_size, u16 n, n × (u16 len, char[len])`. |
| 151 | SAVE_PROFILE | C→S | 2 | Name string. **Requires "local client" status, which requires protocol ≥6** (`NetworkServer.cpp:2414-2420`, `3725-3756`). |
| 152 | LOAD_PROFILE | C→S | 2 | Name string. No local-client requirement (`NetworkServer.cpp:2375-2402`). |
| 153 | DELETE_PROFILE | C→S | 2 | Name string. Requires local client (`NetworkServer.cpp:2274-2280`). |
| 156 | GET_ACTIVE_PROFILE | C→S, S→C | 6 | Reply: string (empty = none). |
| 157 / 160 | ACTIVE_PROFILE_CHANGED / PROFILE_LIST_UPDATED | S→C | 6 | **Pass 2 (added).** Pushed only to clients that set the `SUPPORTS_PROFILEMANAGER` client flag. 157: name string. 160: same block as the 150 reply (`NetworkServer.cpp:3951-3969`, `4139-4160`). |
| 158 | PROFILE_LOADED | S→C | 6 | **Pass 2 (added).** Whole profile as a JSON string. Only to local clients with the `SUPPORTS_PROFILEMANAGER` flag (`NetworkServer.cpp:4162-4180`). |
| 159 | PROFILE_ABOUT_TO_LOAD | S→C, C→S | 6 | **Pass 2 (added).** Empty. Sent to every client with the `SUPPORTS_PROFILEMANAGER` flag before a profile is loaded. The server then waits until each such client has sent an empty packet 159 back **and** all controller queues are empty, or 1000 × 1 ms have passed (`NetworkServer.cpp:1432-1434`, `4050-4137`). A client that sets the flag and never answers delays every profile load by at least one second. |
| 1000 | RGBCONTROLLER_RESIZEZONE | C→S | 0 | `i32 zone_idx, i32 new_size` (exactly 8 bytes). Handled inline on the listener thread. **No range check against `leds_min`/`leds_max` anywhere** (`NetworkServer.cpp:2832-2890`, `RGBController.cpp:2274-2288`): a negative or oversized value is stored as the zone's LED count. The client must enforce `leds_min ≤ new_size ≤ leds_max`. Silently ignored (still an OK result) unless the zone has flag bit 0 or bit 1. The new size is persisted to `Configuration.json` (section 2.2). **Pass 2, why the range check is mandatory:** the value is stored into an `unsigned int`, so `-1` becomes 4 294 967 295. For the ASUS mainboard the next step is `SetupZones()`, which appends one LED object per count (`RGBController_AsusAuraUSB.cpp:275-297`, `303-309`); an out-of-range size makes the server allocate until it fails. A resize also clears and re-creates the colour buffer, so all stored colours of that controller become black until the next update (`RGBController_AsusAuraUSB.cpp:212-213`, `300`). |
| 1050 | UPDATELEDS | C→S | 0 | `u32 data_size, u16 n, n × RGBColor`. |
| 1051 | UPDATEZONELEDS | C→S | 0 | `u32 data_size, u32 zone_idx, u16 n, n × RGBColor`. See the overflow warning in 3.6. |
| 1052 | UPDATESINGLELED | C→S | 0 | `i32 led_idx, RGBColor` (exactly 8 bytes, otherwise rejected: `NetworkServer.cpp:3297-3302`). |
| 1100 | SETCUSTOMMODE | C→S | 0 | Empty. |
| 1101 | UPDATEMODE | C→S | 0 | `u32 data_size, i32 mode_idx, Mode Data` (3.5.2). |
| 1102 | SAVEMODE | C→S | 3 | Same as 1101. Asks the device to persist the mode in its own flash (for the ASUS mainboard it also writes the shutdown effect and a commit: `RGBController_AsusAuraMainboard.cpp` `DeviceSaveMode`). **RigControl must never send it** unless the user explicitly asks to save to the device. |
| 1103 | UPDATEZONEMODE | C→S | 6 | `u32 data_size, i32 zone_idx, i32 mode_idx, Mode Data`. `mode_idx = -1` means "zone follows the device mode" and then no Mode Data is parsed. `data_size` must equal `pkt_size` with no legacy relaxation (`NetworkServer.cpp:3456-3566`). **Pass 2:** the bound check is `mode_idx > modes.size()` and has no lower bound, so `mode_idx == modes.size()` and any `mode_idx < -1` pass it, change nothing, and still trigger a device `UpdateZoneMode` with an OK result (`NetworkServer.cpp:3518-3565`). Validate `-1 ≤ mode_idx < zone mode count` on the client. No device on the development PC is known to have per-zone modes. |
| 1150 | SIGNALUPDATE | S→C | 6 | `u32 data_size (includes itself), u32 reason, …` (reason 0 = UPDATELEDS → `u16 n, n × RGBColor`; other reasons → full Device Data). Header `pkt_dev_id` = unique id. Sent only to clients that set the `SUPPORTS_RGBCONTROLLER` client flag. Sent from a per-client send thread; pending reason-0 frames for the same controller are coalesced (only the newest is sent), and other reasons are dropped oldest-first when the per-client queue is full (1024 entries: `NetworkServer.h:41`, `NetworkServer.cpp:4182-4390`). Treat it as a hint, not a complete event log. **Pass 2:** it is sent to every flagged client including the one whose own packet caused the update, so a client receives an echo of its own UPDATELEDS/UPDATEMODE. |

`data_size` rule: in 1050, 1051, 1101 and 1102, `data_size` **must equal `pkt_size`**, which means it includes its own 4 bytes. Otherwise the server returns `ERROR_INVALID_DATA` and does nothing (`NetworkServer.cpp:3129-3139`, `3200-3210`, `3392-3397`). The only relaxation is `Server.legacy_workaround` for protocol ≤4 clients.

### 3.4 Connection handshake

The sequence OpenRGB's own client uses is `NetworkClient.cpp:1763-1937`. Recommended for RigControl:

1. TCP connect to 127.0.0.1:6742. Disable Nagle (`NoDelay = true`).
2. Send `REQUEST_PROTOCOL_VERSION` with `client_max` (RigControl: 5, or 6 if section 4 option B is implemented).
3. Read the reply (id 40). If none arrives within 1000 ms, assume protocol 0 and refuse to continue (RigControl needs ≥3). `negotiated = min(client_max, server_max)`.
4. If negotiated ≥6, expect and consume: `SET_SERVER_NAME` (51) after the version reply, then an ACK for packet 40.
5. Send `SET_CLIENT_NAME` "RigControl".
6. If negotiated ≥6: send `SET_CLIENT_FLAGS`. **Pass 2 (changed):** use `SUPPORTS_RGBCONTROLLER | REQUEST_LOCAL_CLIENT` = `0x00010001`. The earlier value `0x00010005` also set `SUPPORTS_PROFILEMANAGER`, which obliges the client to answer packet 159 (table 3.3) and subscribes it to 157/158/160; RigControl does not use OpenRGB profiles, and profile list/load/save/delete requests do not need that flag (save/delete need only local-client status: `NetworkServer.cpp:2277`, `2417`). If the flag is ever set, the reader must reply to every 159 with an empty 159. Then read `SET_SERVER_FLAGS` (53), followed by the ACK for 52. Bit 16 set means local-client status was granted, which the server allows only for loopback connections (`NetworkServer.cpp:1033-1046`, `3741-3746`). The server's own capability bits are 0-6, so the expected value from this clone is `0x0001007F` (`NetworkServer.cpp:164-170`).
7. Send `REQUEST_CONTROLLER_COUNT`.
   - ≤5: loop `i = 0..count-1`, sending `REQUEST_CONTROLLER_DATA` with `pkt_dev_id = i` and payload `u32 negotiated`. Wait for each reply before sending the next.
   - ≥6: loop over the returned unique ids with `pkt_dev_id = id`.
8. On `DEVICE_LIST_UPDATED` (100):
   - ≤5: discard all indices immediately and redo step 7.
   - ≥6: redo the count request, diff the ids, and fetch only new ones (`OpenRGBSDK.md:111-121`). **Pass 2:** ids are assigned from a counter that only increases while the server process lives, and a controller keeps its id only while the same controller object exists (`NetworkServer.cpp:685-714`). A rescan, or an unplug and re-plug, deletes and re-creates controllers, so they come back with new ids. Never persist ids, and never assume id == index.

Framing rule for the reader: **one reader loop** dispatches by `pkt_id`. Responses carry the same `pkt_id` as their request (`NetworkServer.cpp:3631`, `3693`, `3715`). Serialise requests that expect a reply (one outstanding request per `pkt_id`, guarded by a lock) so replies cannot be mis-matched. Unknown ids: skip `pkt_size` bytes and log them. Never throw from the reader.

ACK caveat (protocol 6): every packet handled on the listener thread is ACKed after its reply. **Corrected list:** the controller-queued packets are 1 (REQUEST_CONTROLLER_DATA), 1050, 1051, 1101, 1102 and 1103 (`NetworkServer.cpp:1488-1493`); they and the profile packets (150-156, 161) are ACKed by worker threads (`NetworkServer.cpp:1161-1165`, `1231-1233`). 1000, 1001, 1002, 1052 and 1100 are handled inline on the listener thread. If a queued packet's `pkt_dev_id` matches no controller, the server ACKs with status 0 (OK) and silently drops the packet (`NetworkServer.cpp:1528-1546`, `1589-1594`). Inline packets with a bad id get `ERROR_INVALID_ID` (4). **An OK ACK does not prove anything was applied**, and for UPDATELEDS/UPDATEMODE it is sent before the device write happens (1.3).

Ordering hazard: because queued and inline packets run on different threads, an inline packet (1000, 1052, 1100) sent right after a queued one (1050, 1051, 1101) for the same controller can execute **before** it. At ≥6 wait for the queued packet's ACK first. At ≤5 (no ACKs) do not mix them back to back: use only queued packets for normal colour/mode updates. **Pass 2:** the opposite order is safe. An inline packet is fully executed on the listener thread before the server reads the next packet, so "1100 then 1050" keeps its order.

**Pass 2, stuck-packet hazard (derived from source, not observed).** Each controller's worker thread waits on a condition variable without a predicate and without holding the lock the listener uses when it queues a packet (`NetworkServer.cpp:1092-1097`, `1530-1539`). If a packet is queued in the short gap between the worker finding its queue empty and the worker going back to sleep, or while a newly created worker has not started waiting yet, or while the worker is in its "controller list is being updated" sleep loop, the wake-up is lost. The packet then stays in the queue, with no reply and no ACK, until the **next** queued packet for the same controller arrives. The profile worker has the same pattern (`NetworkServer.cpp:1179-1182`, `1417-1426`). Client rules:
- Every request that expects a reply or an ACK has a timeout (1 s is ample on loopback).
- On timeout for a controller-queued packet, send one `REQUEST_CONTROLLER_DATA` for the same controller. It wakes the worker, which then processes the stuck packet first and the data request second. Do not resend the original write.
- Because of that, the reader must tolerate a late or duplicate reply to packet 1 and a late ACK. Match data replies by `pkt_dev_id` and discard ones nobody is waiting for.
- At ≤5 (no ACKs), finish every burst of writes to a controller with one `REQUEST_CONTROLLER_DATA` for it. This both flushes a possibly stuck write and gives the read-back needed to confirm `active_mode` and colours.

### 3.5 Controller data block (reply to id 1)

Payload = `u32 data_size` (includes itself: `NetworkServer.cpp:3665-3678`) followed by Device Data. Fields marked with a version appear only when the requested or negotiated version is ≥ that version. Fields marked "<6" appear only below 6.

#### 3.5.1 Device Data (`RGBController::GetDeviceDescriptionData`, `OpenRGBSDK.md:155-189`)

| # | Type | Field | Ver |
|---|---|---|---|
| 1 | i32 | device type (enum 3.7) | 0 |
| 2 | string16 | name | 0 |
| 3 | string16 | vendor | ≥1 |
| 4 | string16 | description | 0 |
| 5 | string16 | version | 0 |
| 6 | string16 | serial | 0 |
| 7 | string16 | location | 0 |
| 8 | u16 | num_modes | 0 |
| 9 | i32 | active_mode | 0 |
| 10 | Mode Data × num_modes | modes | 0 |
| 11 | u16 | num_zones | 0 |
| 12 | Zone Data × num_zones | zones | 0 |
| 13 | u16 | num_leds | 0 |
| 14 | LED Data × num_leds | leds | 0 |
| 15 | u16 | num_colors | 0 |
| 16 | RGBColor × num_colors | colors (current per-LED colours) | 0 |
| 17 | u16 + string16 × n | LED display names | ≥5 |
| 18 | u32 | controller flags | ≥5 |
| 19 | string16 | display name | ≥6 |
| 20 | **u32** len + char[len] | configuration (JSON) | ≥6 |

(`string16` = u16 length including NUL, then the bytes.)

#### 3.5.2 Mode Data (`RGBController.cpp:2900-3060` size/get, `3704-3806` set)

| Type | Field | Ver |
|---|---|---|
| string16 | name | 0 |
| i32 | value (opaque; **echo back unchanged**) | <6 only |
| u32 | flags (3.7). At <6 the server clears `DIR_DIAG` (bit 11) before sending (`RGBController.cpp:2976-2986`). | 0 |
| u32 | speed_min | 0 |
| u32 | speed_max | 0 |
| u32 | brightness_min | ≥3 |
| u32 | brightness_max | ≥3 |
| u32 | colors_min | 0 |
| u32 | colors_max | 0 |
| u32 | speed | 0 |
| u32 | brightness | ≥3 |
| u32 | direction | 0 |
| u32 | color_mode (0 none, 1 per-LED, 2 mode-specific, 3 random) | 0 |
| u16 + RGBColor × n | mode colours | 0 |

Down-level quirk: at <6 a diagonal direction (6-9) is sent as 0/LEFT (`RGBController.cpp:3041-3051`).

Server-side parse of Mode Data in UPDATEMODE/SAVEMODE uses the client's negotiated version, not a version in the payload (`NetworkServer.cpp:3237`, `RGBController.cpp:3704-3806`).

#### 3.5.3 Zone Data (`RGBController.cpp:3133-3400`, `OpenRGBSDK.md:216-237`)

| Type | Field | Ver |
|---|---|---|
| string16 | name | 0 |
| u32 | type (`zone_type` is `unsigned int`; 0 single, 1 linear, 2 matrix; ≥6 adds 3 linear-loop, 4 matrix-loop-x, 5 matrix-loop-y, 6 segmented). Below 6, the new types are mapped to linear or matrix (`RGBController.cpp:3184-3203`, `RGBControllerInterface.h:149-160`). | 0 |
| u32 | leds_min | 0 |
| u32 | leds_max | 0 |
| u32 | leds_count | 0 |
| u16 | matrix_len = byte length of the matrix block that follows (0 = none; otherwise `8 + 4·height·width`) | 0 |
| matrix block | if matrix_len > 0: `u32 height, u32 width, height×width × u32` (0xFFFFFFFF = no LED, by controller convention, for example `#define NA 0xFFFFFFFF` in `RGBController_Razer.h:16`; the serialiser just copies the map) | 0 |
| u16 + Segment × n | segments: string16 name, u32 type, u32 start_idx, u32 leds_count, [≥6: u16 matrix_len + matrix, u32 flags] (`RGBController.cpp:3080-3131`) | ≥4 |
| u32 | zone flags | ≥5 |
| i32 | zone active_mode | ≥6 |
| u16 + Mode Data × n | per-zone modes | ≥6 |
| string16 | zone display name | ≥6 |

Effects-only resizable zones are presented to ≤4 clients as fixed size 0 or 1 (`RGBController.cpp:3208-3230`).

#### 3.5.4 LED Data

`string16 name`, then `u32 value` (<6 only; ignore it).

**LED indexing convention (corrected):** the device `colors` array is zone 0's colours, then zone 1's, and so on, laid out by `RGBController::SetupColors()` (`RGBController.cpp:2343-2394`). A zone's first colour index (`zone.start_idx`, not sent on the wire) is the sum of the **effective** LED counts of all earlier zones, where the effective count is `leds_count`, except for zones with flag bit 0 (effects-only resizable), whose effective count is `min(leds_count, 1)` (`RGBController::LEDsInZone`, `RGBController.cpp:2326-2341`). At protocol ≥5 such a zone reports its real `leds_count` but owns only 0 or 1 colour slot. Assert `sum(effective counts) == num_colors` and log a mismatch. (No device on the development PC uses effects-only zones in the clone: only the MSI 185/761 mainboard controllers set that flag.)

### 3.6 Write semantics (what the server actually does)

| Packet | Server behaviour | Source |
|---|---|---|
| UPDATELEDS | Rejects `n > colors.size()`. Copies `colors[0..n-1]` and calls `UpdateLEDs()`. Send exactly `num_colors` entries. | `NetworkServer.cpp:3100-3167`, `RGBController.cpp:3617-3672` |
| UPDATEZONELEDS | Rejects `zone_idx >= zones` or `n > zone.leds_count`. Copies into the zone colours and calls `UpdateZoneLEDs(zone)` synchronously. **Server bug to avoid:** the bound is `leds_count`, but an effects-only zone (flag bit 0) owns only `min(leds_count,1)` colour slots, so `n > 1` for such a zone writes past the zone's slice of the colour buffer. The client must cap `n` at the effective count (3.5.4). | `NetworkServer.cpp:3356-3453`, `RGBController.cpp:2326-2394` |
| UPDATESINGLELED | Exactly 8 bytes. Rejects `led_idx >= leds.size()`. Sets the colour and calls `UpdateSingleLED` synchronously. Inline packet (ordering hazard, 3.4). | `NetworkServer.cpp:3276-3354` |
| SETCUSTOMMODE | Picks the first mode named `Direct`, then `Custom`, then `Static`, whose **current** `color_mode` is per-LED or mode-specific (not its flags). Calls `SetActiveMode()` (which itself calls `UpdateMode()`) and then `UpdateMode()` again. If no mode matches, nothing happens and the result is still OK. | `RGBController.cpp:1456-1522` |
| UPDATEMODE / SAVEMODE | Rejects `mode_idx` outside `0..modes-1` with INVALID_DATA (no update call). Otherwise parses the mode and calls `SetModeValuesFromMode(existing, sent)`. Only if that **passes** are `color_mode` and the colour list copied, plus direction / brightness / speed **only when the mode has the matching flag**, and `active_mode = mode_idx` set (directly, without `SetActiveMode`). **Then it calls `UpdateMode()` or `SaveMode()` regardless and returns OK.** A rejected mode is silent and re-sends the old active mode to the device. **Pass 2:** if the Mode Data is truncated the parser returns an error and the result is INVALID_DATA with no device call, but `SetModeValuesFromMode` has already run on the partly parsed mode (`NetworkServer.cpp:3237-3258`), so the server's stored mode and `active_mode` can change without the device being updated. Never send a short packet. Trailing bytes after the Mode Data are not checked. | `NetworkServer.cpp:3169-3274`, `RGBController.cpp:4820-4874` |
| RESIZEZONE | See table 3.3: no range check, persisted to `Configuration.json`, signals CONFIGUREZONE (SIGNALUPDATE reason 3). No DEVICE_LIST_UPDATED is sent, so a protocol ≤5 client must re-request that controller's data itself. | `NetworkServer.cpp:2832-2890`, `RGBController.cpp:2200-2288` |

`SetModeValuesFromMode` validation (`RGBController.cpp:4820-4874`). All of these must hold:
- `name` equals the existing mode name.
- If the mode has `HAS_BRIGHTNESS`: `brightness_min` and `brightness_max` equal the existing values, and `brightness` lies in range. The range may be inverted (min > max).
- `color_mode` is allowed by the flags: NONE when there are no colour flags; PER_LED needs `HAS_PER_LED_COLOR`; MODE_SPECIFIC needs `HAS_MODE_SPECIFIC_COLOR`; RANDOM needs `HAS_RANDOM_COLOR`.
- If `HAS_MODE_SPECIFIC_COLOR`: `colors_min` and `colors_max` are unchanged and `colors_min ≤ n ≤ colors_max`. **Pass 2:** this applies whenever the flag is set, whatever `color_mode` is being sent (for example RANDOM on a mode that has both flags).
- Direction: 0 when there are no direction flags; otherwise left/right, up/down or horizontal/vertical, as allowed by `HAS_DIRECTION_LR`, `HAS_DIRECTION_UD` or `HAS_DIRECTION_HV`. **Pass 2:** `HAS_DIRECTION_DIAG` is not part of this test and no branch accepts 6-9, so a diagonal direction can never be set through the SDK; and a mode that has only the DIAG flag must be sent with direction 0.
- If `HAS_SPEED`: `speed_min` and `speed_max` are unchanged and `speed` lies in range. The range may be inverted.

**Pass 2, what is and is not looked at.** The sent `value` and `flags` are parsed and then ignored: validation uses the server's own flags and nothing copies them. Limits are compared only for features the mode has. When the mode lacks `HAS_MODE_SPECIFIC_COLOR`, the sent colour list is copied into the server's mode **without any length check**.

**Client rule:** build UpdateMode by taking the mode exactly as last received, changing only `speed`, `brightness`, `direction`, `color_mode` and `colors`, and validating locally with the same rules. After sending, re-read controller data (protocol ≤5) or wait for SIGNALUPDATE (protocol 6) and confirm `active_mode`. **Pass 2 additions:**
- Do not echo blindly where the rules above are stricter than the received data: if the mode has no LR/UD/HV flag send `direction = 0`, and if it has no colour flag send `color_mode = 0`, even if the server reported something else.
- Keep the colour list the same length as received unless the mode has `HAS_MODE_SPECIFIC_COLOR` and the new length is inside `colors_min..colors_max`. Never send an empty list with `color_mode = 2`: controller code reads `colors[0]` without a check in that case (for example `RGBController_AsusAuraUSB.cpp:352-357`), so a mode whose `colors_min` is 0 could crash the server.
- A silent rejection looks exactly like success on the wire (OK ACK, device re-sent the old mode). The read-back is the only way to detect it.

Setting "all LEDs one colour" robustly:
1. If a mode named `Direct` exists with `HAS_PER_LED_COLOR`, send `UPDATEMODE` with it active, then `UPDATELEDS` with `num_colors` copies of the colour (**corrected** from `num_leds`: the server bound is `colors.size()`). Both are queued packets, so their order is kept, and the device thread always writes the mode before the LEDs when both are pending (`RGBController.cpp:2095-2151`). **Pass 2:** `SETCUSTOMMODE` may replace the `UPDATEMODE` here (inline before queued is safe, 3.4), but it is not equivalent: it picks Direct, then Custom, then Static by name and current `color_mode`, and does nothing at all if none qualifies. Prefer the explicit `UPDATEMODE`.
2. Otherwise, if a mode named `Static` with `HAS_MODE_SPECIFIC_COLOR` exists, send `UPDATEMODE` with `color_mode = 2` and `colors = [c]`.

### 3.7 Enums

From `RGBControllerInterface.h`:
- **Device types** (lines 182-206): 0 motherboard, 1 DRAM, 2 GPU, 3 cooler, 4 LED strip, 5 keyboard, 6 mouse, 7 mousemat, 8 headset, 9 headset stand, 10 gamepad, 11 light, 12 speaker, 13 virtual, 14 storage, 15 case, 16 microphone, 17 accessory, 18 keypad, 19 laptop, 20 monitor, 21 unknown.
- **Mode flags** (62-77): `HAS_SPEED` 1<<0, `DIR_LR` 1<<1, `DIR_UD` 1<<2, `DIR_HV` 1<<3, `HAS_BRIGHTNESS` 1<<4, `PER_LED_COLOR` 1<<5, `MODE_SPECIFIC_COLOR` 1<<6, `RANDOM_COLOR` 1<<7, `MANUAL_SAVE` 1<<8, `AUTOMATIC_SAVE` 1<<9, `REQUIRES_ENTIRE_DEVICE` 1<<10, `DIR_DIAG` 1<<11.
- **Directions** (82-94): 0 L, 1 R, 2 U, 3 D, 4 H, 5 V, 6-9 diagonals (≥6 only).
- **Zone flags** (126-144): bit 0 effects-only resizable, bit 1 resizable, … bit 24 geometry may change.
- **Controller flags** (219-233): bit 0 local, bit 1 remote, bit 2 virtual, bit 3 hidden, bit 8 reset-before-update, …
- **SIGNALUPDATE reasons** (41-57): 0 UPDATELEDS, 1 UPDATEMODE, 2 SAVEMODE, 3 CONFIGUREZONE, 4 CLEARSEGMENTS, 5 ADDSEGMENT, 6 HIDDEN, 7 UNHIDDEN, 8 SETDEVICESPECIFICCONFIGURATION, 9 SETDEVICESPECIFICZONECONFIGURATION, 10 CONFIGUREDEVICE, 11 DEVICE_CHANGED.

ACK status codes (`NetworkProtocol.h:62-70`): 0 OK, 1 generic, 2 unsupported, 3 not allowed, 4 invalid id, 5 invalid data.

### 3.8 Client and server flags (protocol 6)

Source: `NetworkProtocol.h:78-100`.
- Client flags: bit0 RGBController, bit1 LogManager, bit2 ProfileManager, bit3 PluginManager, bit4 SettingsManager, bit16 request local client.
- Server flags: bit0-4 the same, bit5 detection, bit6 device info, bit16 local client granted.

### 3.9 Profiles

- List (150), load (152): available at protocol ≥2.
- Save (151) and delete (153) need local-client status, so they need protocol ≥6 and a loopback connection. At ≤5 they are rejected with NOT_ALLOWED, which such clients never see.
- Active profile (156), upload/download JSON (154/155): protocol 6.
- After load, re-read all controllers. `OpenRGBSDK.md:564` says the load "will not actually update the controllers" and that the client should then call `UpdateMode()` on all of them. **Pass 2: the code disagrees, and the code wins.** In the clone, `LoadProfile` → `LoadProfileWithOptions(name, false, true)` → for every matching controller sets the modes and `active_mode`, calls `UpdateMode()`, copies the colours and calls `UpdateLEDs()`, and applies zone modes with `UpdateZoneMode()` (`ProfileManager.cpp:469-485`, `1442-1535`, `1652-1655`). **LOAD_PROFILE (152) writes to hardware by itself.** It returns INVALID_DATA when the profile is missing or empty (`NetworkServer.cpp:2391-2398`, `ProfileManager.cpp:1582-1587`). **UNVERIFIED** for the released 1.1 binary.
- **Pass 2:** before loading, the server notifies flagged clients with packet 159 and waits up to about one second (table 3.3).
- **Recommendation:** RigControl keeps its own lighting presets (JSON in RigControl's settings) and replays them with UpdateMode/UpdateLEDs. It does not depend on OpenRGB profiles.

### 3.10 Example packets (hex, little-endian)

| Purpose | Bytes |
|---|---|
| Request protocol version, client max 5 | `4F 52 47 42  00 00 00 00  28 00 00 00  04 00 00 00  05 00 00 00` |
| Server reply (server max 6) | `4F 52 47 42  00 00 00 00  28 00 00 00  04 00 00 00  06 00 00 00` |
| Set client name "RigControl" | `4F 52 47 42  00 00 00 00  32 00 00 00  0B 00 00 00  52 69 67 43 6F 6E 74 72 6F 6C 00` |
| Request controller count | `4F 52 47 42  00 00 00 00  00 00 00 00  00 00 00 00` |
| Count reply at ≤5 (7 controllers) | `4F 52 47 42  00 00 00 00  00 00 00 00  04 00 00 00  07 00 00 00` |
| Request controller data, index 2, version 5 | `4F 52 47 42  02 00 00 00  01 00 00 00  04 00 00 00  05 00 00 00` |
| UpdateLEDs dev 0: LED0 red, LED1 green (data_size 14) | `4F 52 47 42  00 00 00 00  1A 04 00 00  0E 00 00 00  0E 00 00 00  02 00  FF 00 00 00  00 FF 00 00` |
| UpdateZoneLEDs dev 1, zone 0, 3 × blue (data_size 22) | `4F 52 47 42  01 00 00 00  1B 04 00 00  16 00 00 00  16 00 00 00  00 00 00 00  03 00  00 00 FF 00  00 00 FF 00  00 00 FF 00` |
| UpdateSingleLED dev 0, LED 5, white | `4F 52 47 42  00 00 00 00  1C 04 00 00  08 00 00 00  05 00 00 00  FF FF FF 00` |
| SetCustomMode dev 0 | `4F 52 47 42  00 00 00 00  4C 04 00 00  00 00 00 00` |
| ResizeZone dev 0, zone 1 → 60 LEDs | `4F 52 47 42  00 00 00 00  E8 03 00 00  08 00 00 00  01 00 00 00  3C 00 00 00` |
| LoadProfile "Night" | `4F 52 47 42  00 00 00 00  98 00 00 00  06 00 00 00  4E 69 67 68 74 00` |
| DeviceListUpdated (from server) | `4F 52 47 42  00 00 00 00  64 00 00 00  00 00 00 00` |
| ACK at v6 for UpdateLEDs dev 0, OK | `4F 52 47 42  00 00 00 00  0A 00 00 00  08 00 00 00  1A 04 00 00  00 00 00 00` |
| **Pass 2 (added):** request protocol version, client max 6 | `4F 52 47 42  00 00 00 00  28 00 00 00  04 00 00 00  06 00 00 00` |
| **Pass 2 (added):** count reply at v6, 3 controllers with ids 0, 1, 2 | `4F 52 47 42  00 00 00 00  00 00 00 00  10 00 00 00  03 00 00 00  00 00 00 00  01 00 00 00  02 00 00 00` |
| **Pass 2 (added):** set client flags `0x00010001` | `4F 52 47 42  00 00 00 00  34 00 00 00  04 00 00 00  01 00 01 00` |
| **Pass 2 (added):** server flags reply, local client granted (`0x0001007F`) | `4F 52 47 42  00 00 00 00  35 00 00 00  04 00 00 00  7F 00 01 00` |
| **Pass 2 (added):** detection complete (from server, v6) | `4F 52 47 42  00 00 00 00  67 00 00 00  00 00 00 00` |
| **Pass 2 (added):** profile-about-to-load, and the client's answer (identical bytes) | `4F 52 47 42  00 00 00 00  9F 00 00 00  00 00 00 00` |

All rows above were regenerated by script in pass 2 and matched byte for byte, except the UpdateMode example below, which was wrong.

UpdateMode example (protocol 5, dev 3, mode_idx 1). The mode is a Razer-style `Static` (flags `0x50` = mode-specific colour + brightness, per `RGBController_Razer.cpp:74-85`). **Every value except brightness and colour must be echoed from the last controller data. The value 0x01 and brightness_max 255 shown here are illustrative only.**

**Pass 2 (corrected):** the size was given as 67 (`0x43`), which left out the 4 colour bytes. The body below is 4 + 4 + 9 + 4 + 4 + 8 + 8 + 8 + 4 + 4 + 4 + 4 + 2 + 4 = **71 bytes (`0x47`)**. If this body is sent with 67 in the size fields, the server parses a truncated mode (INVALID_DATA) and then reads the 4 left-over bytes as a bad magic, which closes the connection.

```
Header : 4F 52 47 42 | 03 00 00 00 | 4D 04 00 00 | 47 00 00 00        (id 1101, size 71)
Body   : 47 00 00 00                     data_size = 71 (= pkt_size)
         01 00 00 00                     mode_idx = 1
         07 00 53 74 61 74 69 63 00      name "Static"
         01 00 00 00                     value (echo; omitted at v6)
         50 00 00 00                     flags (echo)
         00 00 00 00  00 00 00 00        speed_min, speed_max (echo)
         00 00 00 00  FF 00 00 00        brightness_min, brightness_max (echo)
         01 00 00 00  01 00 00 00        colors_min, colors_max (echo)
         00 00 00 00                     speed
         80 00 00 00                     brightness = 128
         00 00 00 00                     direction
         02 00 00 00                     color_mode = MODE_SPECIFIC
         01 00  00 FF 00 00              1 colour: green
```

Whole packet, 87 bytes:

```
4F 52 47 42 03 00 00 00 4D 04 00 00 47 00 00 00
47 00 00 00 01 00 00 00 07 00 53 74 61 74 69 63 00
01 00 00 00 50 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 FF 00 00 00 01 00 00 00 01 00 00 00
00 00 00 00 80 00 00 00 00 00 00 00 02 00 00 00
01 00 00 FF 00 00
```

At protocol 6 the same packet has no `value` field: size **67 (`0x43`)** (**Pass 2:** corrected from 63 / `0x3F`), both size fields are `43 00 00 00`, and `pkt_dev_id` is the unique ID.

---

## 4. Client library: recommendation

### 4.1 Verdict

**Write a hand-rolled C# client (option B). Use OpenRGB.NET 3.1.1 only for a throw-away prototype (option A).**

Findings about OpenRGB.NET 3.1.1 (read from GitHub HEAD = 3.1.1, commit `442b448`, files `OpenRGBClient.cs`, `OpenRgbConnection.cs`, `Models/*.cs`, `Writables/UpdateLedsArg.cs`):

| # | Finding | Impact on RigControl |
|---|---|---|
| 1 | `MaxProtocolNumber = 4`; constructor throws if more is requested. | No zone flags, no effects-only zones, diagonal directions lost. Profile save/delete are always rejected by OpenRGB 1.x (needs v6 local client) and fail silently. |
| 2 | `UpdateMode(int deviceId, int modeId, uint? speed = null, Direction? direction = null, Color[]? colors = default)` re-fetches the device and has **no brightness parameter**. `Mode.SetBrightness` exists but the fetched copy is discarded. | **Mode brightness cannot be set** through the public API. |
| 3 | The client constructor calls `new OpenRgbConnection(DeviceListUpdated)` before anyone can subscribe. The client event has no custom accessors, so the connection keeps the null delegate. | `DeviceListUpdated` will most likely never fire. Poll instead. **UNVERIFIED by test.** Subscribe and unplug a USB device to confirm. |
| 4 | Protocol-version negotiation waits with `BlockingCollection.Take(token)` and no timeout. | `Connect()` can hang forever against a silent server. Wrap it in a task with a timeout. |
| 5 | No locking around send/receive. Responses are queued per command id. | Not thread-safe. Callers must serialise all calls. |
| 6 | `UpdateMode` bounds check is `modeId > Modes.Length` (off by one). | Validate the index yourself. |
| 7 | `UpdateLedsArg` writes `data_size = 6 + 4n`, which includes itself. | Matches the server rule (3.3). Good. |
| 8 | Not updated since 2024-07. | Will not follow OpenRGB 1.x protocol 6. |
| 9 | **Pass 2 (added).** The read loop looks every incoming packet id up in a dictionary built from its own `CommandId` enum and catches only `TaskCanceledException` (`OpenRgbConnection.cs:33-34`, `57-90`). | A packet id the library does not know ends the read loop with an unhandled exception, after which every request blocks forever. At negotiated version 4 the clone's server sends no such packet (51, 10, 101-103, 157-160 and 1150 are all gated on ≥6), so this is latent, but it is one more reason not to ship it. |
| 10 | **Pass 2 (added).** `SaveMode(int deviceId, int modeId)` is public (`OpenRGBClient.cs:230-240`) and sends packet 1102. | Writes to device flash. RigControl must never call it. |
| 11 | **Pass 2 (added).** `ResizeZone` sends the size unchecked (`OpenRGBClient.cs:117-120`). | The caller must enforce `LedsMin ≤ size ≤ LedsMax` (3.3, packet 1000). |

**Pass 2:** findings 1-7 were re-read in the GitHub sources at commit `442b448` and are confirmed as written. For finding 4 the detail is: `NegotiateProtocolVersion` sets `Socket.ReceiveTimeout = 1000` and catches `TimeoutException`, but the reply is awaited with `BlockingCollection.Take(token)` while the socket is read by an asynchronous loop that the receive timeout does not affect, so the timeout can never fire (`OpenRgbConnection.cs:120-154`).

Option B, a hand-rolled client of about 800 lines: a single `Socket` with a reader `Task`, a `SemaphoreSlim` per request id, and parsers for protocol 3-6 per section 3. Negotiate `min(server, 5)` in phase 1. Add protocol 6 (unique ids, ACK consumption, DETECTION_COMPLETE, SIGNALUPDATE, local-client profile save) in phase 2. Write unit tests by feeding captured server replies to the parser. Captures can be taken by connecting to the real server. The `Documentation/OpenRGB SDK Wireshark Dissector.lua` in the clone may help inspection, but it is reference data only and must not be executed from the clone.

### 4.2 OpenRGB.NET 3.1.1: exact calls (prototype only)

Namespace `OpenRGB.NET`. Types from the GitHub sources:
- `OpenRgbClient(string ip = "127.0.0.1", int port = 6742, string name = "OpenRGB.NET", bool autoConnect = true, int timeoutMs = 1000, uint protocolVersionNumber = 4)`, `IDisposable`.
- `Device`: `Index`, `Type`, `Name`, `Vendor`, `Description`, `Version`, `Serial`, `Location`, `ActiveModeIndex`, `Modes`, `Zones`, `Leds`, `Colors`, `ActiveMode`.
- `Zone`: `Index`, `DeviceIndex`, `Name`, `Type`, `LedCount` (uint), `LedsMin`, `LedsMax`, `MatrixMap`, `Segments`.
- `Mode`: `Name`, `Flags`, `SpeedMin`/`Max`, `BrightnessMin`/`Max`, `ColorMin`/`Max`, `ColorMode`, `Speed`, `Brightness`, `Direction`, `Colors`, `SupportsSpeed`/`Brightness`/`Direction`.
- `Color`: `readonly record struct Color(byte R = 0, byte G = 0, byte B = 0)`, 4 bytes.

```csharp
using OpenRGB.NET;

using var client = new OpenRgbClient("127.0.0.1", 6742, "RigControl", autoConnect: true, timeoutMs: 1000);

// List controllers
Device[] devices = client.GetAllControllerData();          // indices 0..n-1 (protocol 4)
foreach (var d in devices)
{
    Console.WriteLine($"{d.Index}: {d.Name} [{d.Type}] leds={d.Leds.Length}");
    foreach (var z in d.Zones)  Console.WriteLine($"  zone {z.Index} {z.Name} {z.Type} leds={z.LedCount}");
    foreach (var m in d.Modes)  Console.WriteLine($"  mode {m.Name} flags={m.Flags}");
}

// Every LED of device i to one colour
int i = 0;
client.SetCustomMode(i);                                   // Direct > Custom > Static (server side)
var red = new Color(255, 0, 0);
client.UpdateLeds(i, Enumerable.Repeat(red, devices[i].Colors.Length).ToArray());

// Mode by name with colours and speed (no brightness possible in 3.1.1)
int modeIdx = Array.FindIndex(devices[i].Modes, m => m.Name == "Breathing");
var mode = devices[i].Modes[modeIdx];
client.UpdateMode(i, modeIdx,
    speed: mode.SupportsSpeed ? (mode.SpeedMin + mode.SpeedMax) / 2 : null,
    direction: null,
    colors: mode.Colors.Length > 0 ? Enumerable.Repeat(new Color(0, 0, 255), mode.Colors.Length).ToArray() : null);
// colors.Length must equal the mode's current Colors.Length, otherwise it throws InvalidOperationException

// One zone
int zoneIdx = 1;
client.UpdateZoneLeds(i, zoneIdx, Enumerable.Repeat(new Color(0, 255, 0), (int)devices[i].Zones[zoneIdx].LedCount).ToArray());

// Addressable header sizing (ASUS ARGB headers start at 0 LEDs, see section 5)
// Pass 2: the server does not range-check; an out-of-range size can take the server down (3.3, packet 1000)
var zone = devices[i].Zones[zoneIdx];
int newSize = 60;
if (newSize >= zone.LedsMin && newSize <= zone.LedsMax)
    client.ResizeZone(i, zoneIdx, newSize);

// Device-list changes: poll client.GetControllerCount() every 2 s; see finding 3
```

---

## 5. Devices on the development PC

Live PnP facts (read-only, 2026-11-15):
- SMBus controller `PCI\VEN_8086&DEV_7A23&SUBSYS_88821043` (Intel Raptor Lake/Z790, ASUS subvendor).
- `0B05:19AF`: MI_00 bound to WINUSB, MI_02 HID.
- `1E71:300C`: MI_00 WINUSB "NZXT Kraken Elite", MI_01 HID.
- **One** `1E71:2011` "NZXT RGB & Fan Controller" and **two** `1E71:2021` "NZXT RGB Controller" (two USB serials).
- `1532:007D` and `1532:02A5`: MI_02 uses a Razer Inc. INF; other interfaces use Microsoft input.inf.
- The LG monitor appears only as `DISPLAY\GSM5C34` (no USB `VID_043E` device).

| Device | Detector in clone | Access path | Caveats |
|---|---|---|---|
| ASUS Aura USB mainboard `0B05:19AF` (Z790-A) | **Yes.** `REGISTER_HID_DETECTOR_PU("ASUS Aura Motherboard", …, AURA_MOTHERBOARD_3_PID, 0xFF72, 0x00A1)`, `Controllers/AsusAuraUSBController/AsusAuraUSBController/AsusAuraUSBControllerDetect.cpp:32,156` | USB HID (hidapi), user mode, no admin | The controller reads the firmware string and a config table at detection. **Pass 2 (corrected):** detection is not read-only and its failure path is not clean. The constructor sends three HID output reports: firmware request `EC 82`, config-table request `EC B0`, and then `SetGen1()` = `EC 52 53 00 01` (`AsusAuraUSBController.cpp:70-146`, `AsusAuraMainboardController.cpp:57-86`). If the config-table reply is wrong, the base constructor logs "Could not read config table" and executes `delete this` inside the constructor; nothing throws, so the `catch(std::runtime_error)` in the detector is dead code and the behaviour is undefined (`AsusAuraUSBController.cpp:111-115`, `…Detect.cpp:98-110`). A failed read can therefore crash the server instead of skipping the device. The controller's SDK name is `"ASUS " + <DMI board name>`, not "ASUS Aura Motherboard" (that is only the detector key) (`…Detect.cpp:100-101`). All modes except Direct carry `MANUAL_SAVE` (`RGBController_AsusAuraMainboard.cpp:34-42`). Zones: "Aura Mainboard" (fixed) plus **"Addressable RGB Header N" zones, which start at 0 LEDs** and are resizable to 0-500 (`RGBController_AsusAuraUSB.cpp:221-271`, `RGBController_AsusAuraUSB.h:17`). RigControl must ResizeZone each header to the real LED count, range-checked (3.3). **Pass 2 (resolved in source):** an SDK ResizeZone is persisted, in `<config>\Configuration.json` (not `OpenRGB.json`), and re-applied when the controller is next registered, provided type, description, version, serial and the zone's name/type/`leds_min`/`leds_max` still match (`NetworkServer.cpp:2882-2887`, `ProfileManager.cpp:904-1033`, `1365-1436`, `DetectionManager.cpp:489`). Still to be observed on the PC. **UNVERIFIED** that this board's HID collection matches usage page 0xFF72 / usage 0xA1. Confirm in OpenRGB's detection log. Modes include Direct, Off, Static, Breathing, Flashing, Spectrum Cycle, Rainbow, … (`RGBController_AsusAuraUSB.cpp:40-181`). |
| NZXT Kraken Elite `1E71:300C` | **No.** The clone's NZXT tables list 2001-2022, 3012 and 3014 only (`NZXTHue2Devices.h:24-44`, `NZXTHue2ControllerDetect.cpp:50-71`, `NZXTKrakenControllerDetect.cpp:18-40`). | n/a | No lighting control via OpenRGB. liquidctl lists 300C as "NZXT Kraken 2023 Elite (broken)" and says its ring is not supported (`ref/liquidctl/liquidctl/driver/kraken3.py:575-576`, `docs/kraken-x3-z3-guide.md:29`). The Hue2 code knows an accessory id `0x1E` "Kraken Elite Ring" (24 LEDs) on Hue2-family channels (`NZXTHue2Controller.cpp:308-310`). **UNVERIFIED** whether the development PC's ring is wired through one of the `2021` controllers. Check OpenRGB's debug log line `[NZXT Hue 2] … Device ID: 1E` after detection. |
| NZXT RGB & Fan Controller `1E71:2011` | **Yes.** `NZXTHue2ControllerDetect.cpp:53`; table `NZXTHue2Devices.cpp:328-343` (6 RGB channels, 3 fan channels, 64-byte packets, legacy protocol) | USB HID, no admin | LED counts are auto-detected from accessory ids per channel (`NZXTHue2Controller.cpp:~220-330`). The RGBController exposes lighting only. **Pass 2 (audited, resolved for the clone):** the only fan write, `NZXTHue2Controller::SendFan` (report `62 01 …`, `NZXTHue2Controller.cpp:141-168`), has no caller anywhere in the tree, and the fan-status read in `UpdateStatus()` is compiled out with `if(false)` (`:344-379`). The reports the controller does send start with `10` (firmware request), `20 03` (accessory list), `22`, `26`, `28` and `2A` (lighting) (`:183-189`, `533-602`, `783-937`). So the clone never commands the fans. Detection itself still opens the device and writes the firmware and accessory-list requests while CAM is using it (`:54-59`). **UNVERIFIED** for the released 1.1 binary. Conflicts with NZXT CAM (section 7). |
| NZXT RGB Controller `1E71:2021` (×2) | **Yes.** `NZXTHue2ControllerDetect.cpp:68`; table `NZXTHue2Devices.cpp:447-462` (3 RGB channels, 0 fan channels) | USB HID, no admin | Two controllers with the same name. Distinguish by `location` (HID path) or `serial`. Same CAM conflict. |
| Razer DeathAdder V2 Pro `1532:007D` (wireless dongle PID) | **Yes.** `RazerControllerDetect.cpp:385` (interface 0, usage page 0x01, usage 0x02); device `RazerDevices.cpp:6085-6112`: 1 zone "Logo", 1 LED, named "Razer DeathAdder V2 (Wireless)" | USB HID feature reports, no admin | Wireless: commands fail while the mouse sleeps or is disconnected (**UNVERIFIED** how OpenRGB reports this). Modes: Direct, Static, Breathing, … with brightness (`RGBController_Razer.cpp:57-96`). |
| Razer BlackWidow V4 75% `1532:02A5` | **Yes.** `RazerControllerDetect.cpp:276` (interface 3, page 0x01, usage 0x00); device `RazerDevices.cpp:1968-1995`: matrix zone 6×18 with the V4 Pro 75% layout | USB HID, no admin | The keyboard shares its layout table with the V4 Pro 75%. **UNVERIFIED** whether all keys map correctly. Synapse not installed (no Razer service found), but a Razer driver INF is bound to MI_02. |
| Team Group UD5-6000 DDR5 (ENE) ×2 | **Generic.** `REGISTER_I2C_DETECTOR("ENE SMBus DRAM", DetectENESMBusDRAMControllers)`, `ENESMBusControllerDetect.cpp:286`. It runs on buses matching `IF_DRAM_SMBUS`, which includes Intel Raptor Lake `0x7A23` (`pci_ids/pci_ids.h:61,1074-1086`). | SMBus via **PawnIO** kernel driver + `SmbusI801.bin`, **admin required** | Detection **writes** to the SMBus: it remaps ENE devices answering at 0x77 to free addresses from 0x70-0x77/0x4F/0x66/0x67/0x39-0x3D (`ENESMBusControllerDetect.cpp:41-59, 159-193`). Supported ENE firmware strings include `DIMM_LED-0103` "(T-Force Delta RGB)" (`ENESMBusController.cpp:113-122`). **UNVERIFIED** that UD5-6000 modules use a firmware version this code recognises. Confirm by running detection with PawnIO and checking the log. Without PawnIO and admin the DIMMs are simply absent. Armoury Crate (ASUS DRAM sync) may also access the bus (section 7). |
| Gigabyte RTX 4070 Ti, SUBSYS `1458:40CA` | **Yes.** `REGISTER_I2C_PCI_DETECTOR("Gigabyte GeForce RTX 4070 Ti Eagle OC", DetectGigabyteRGBFusion2GPUControllers, NVIDIA_VEN, NVIDIA_RTX4070TI_DEV, GIGABYTE_SUB_VEN, GIGABYTE_RTX4070TI_EAGLE_OC_SUB_DEV, 0x71)`, `GigabyteRGBFusion2GPUControllerDetect.cpp:153`; ids `pci_ids.h:124,172,727` | I2C over **NVAPI** (`NvAPI_I2CWriteEx/ReadEx`, `i2c_smbus/Windows/i2c_smbus_nvapi.cpp:93-204`). Goes through the NVIDIA driver; no extra kernel driver. | The detector probes by writing `0xAB` and expects `0xAB` back (`…Detect.cpp:21-70`). Modes: Direct, Pulse, Flash, Double Flash, Color Cycle, Gradient, Wave, Color Shift, Tricolor (`RGBController_GigabyteRGBFusion2GPU.cpp:35-139`). **UNVERIFIED** that NVAPI I2C works **without admin** on driver 621.24, and that this card responds. GIGABYTE Control Center is running and likely drives the same 0x71 controller (section 7). |
| Patriot Viper M2 SSD RGB | **No.** Patriot detectors cover DDR4 DRAM (`PatriotViperControllerDetect.cpp:80`, `PatriotViperSteelControllerDetect.cpp:78`) and the V550 mouse only. No M.2/SSD match in the clone (grep `VPR400`, `Viper M.2`, `RGB.*SSD`). | n/a | Not controllable via OpenRGB. |
| LG UltraGear+ monitor (`GSM5C34`) | **No for this unit.** The only LG detector is a USB HID detector for 27GN950-B `043E:9A8A` (`LGMonitorControllerDetect.cpp:19-48`). | (would be USB HID) | No LG USB device is present. The monitor exposes no USB lighting interface to Windows, so it is not controllable. |

**Identity across restarts:** never persist controller indices. Key devices by `(type, name, location, serial)` and re-map after every DEVICE_LIST_UPDATED. Two identical `NZXT RGB Controller` entries make the `location` and `serial` fields mandatory.

---

## 6. Privileges and kernel drivers (WinRing0, PawnIO, HVCI)

| Topic | Finding | Source |
|---|---|---|
| Driver used by this OpenRGB version for SMBus | **PawnIO only** (x64). `i2c_smbus_pawnio_detect` loads `SmbusI801.bin`, `SmbusPIIX4.bin`, `SmbusNCT6793.bin` and `SmbusIntelSkylakeIMC.bin` from the exe's folder. The project links `PawnIOLib` and copies those modules next to the exe. WinRing0 and InpOut32 are no longer used. | `i2c_smbus_pawnio.cpp:264-483`, `OpenRGB.pro:387-399, 454-463`, `Documentation/SMBusAccess.md:13-15` |
| Is PawnIO bundled? | **No.** PawnIO must be installed separately from `https://pawnio.eu` (installer `PawnIO_setup.exe`). The OpenRGB MSI only shows a dialog asking the user to install it. | `scripts/build-msi.ps1:173-187`; release notes 1.0 and 1.1 (Codeberg); pawnio.eu |
| Admin | **Required** for PawnIO SMBus access: "You must run the application as Administrator … OpenRGB may be installed as a background service". `pawnio_open` returns `E_ACCESSDENIED`, which is logged as "Permission Denied". | `SMBusAccess.md:14`, `i2c_smbus_pawnio.cpp:321-335`, MSI dialog text `build-msi.ps1:187,203,209` |
| Behaviour without PawnIO or admin | SMBus detection is skipped ("PawnIO is not loaded…" or "Permission Denied"). Everything else is still detected. | `i2c_smbus_pawnio.cpp:371-378` |
| WinRing0 | Microsoft Defender detects it as `VulnerableDriver:WinNT/Winring0` (CVE-2020-14979). OpenRGB replaced it in 1.0rc3, and the only WinRing0 build was "1.0rc2 (WinRing0)". **Do not use any WinRing0 OpenRGB build.** | Microsoft support page "Microsoft Defender Antivirus alert - VulnerableDriver:WinNT/Winring0" (web); Codeberg release notes 1.0rc3 |
| HVCI on the development PC | `HKLM\…\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled = 1`; `HKLM\…\CI\Config\VulnerableDriverBlocklistEnable = 1`. | Live read-only registry check |
| PawnIO under HVCI | **UNVERIFIED.** pawnio.eu says the official edition is "digitally signed" but says nothing about HVCI. To confirm: install PawnIO (user action), then `sc query PawnIO` shows RUNNING, and OpenRGB's log shows "PawnIO initialized successully" (sic, `i2c_smbus_pawnio.cpp:367`). If HVCI blocks it, Windows Security lists it under Memory integrity "incompatible drivers". | pawnio.eu; Microsoft HVCI docs |
| HID devices (ASUS Aura USB, NZXT, Razer) | No driver and no admin needed. hidapi runs in user mode. All these detectors are `REGISTER_HID_DETECTOR*`. | detector lines in section 5 |
| GPU via NVAPI | User mode through `nvapi64.dll`. No extra driver. **UNVERIFIED:** whether admin is needed. Test as a standard user. | `i2c_smbus_nvapi.cpp:177-204` |
| SMBus coordination | When `Drivers.shared_smbus_access` is true (the default), OpenRGB takes the named mutex `Global\Access_SMBUS.HTP.Method` around SMBus transfers. **UNVERIFIED:** whether Armoury Crate or GIGABYTE Control Center honour the same mutex. | `i2c_smbus_pawnio.h:22`, `i2c_smbus_pawnio.cpp:52-66, 139-172` |

**What works without admin (expected):** ASUS Aura USB mainboard and its ARGB headers, both NZXT RGB controllers, the NZXT RGB & Fan controller, the Razer mouse and keyboard, and probably the Gigabyte GPU. **What needs PawnIO and admin:** the two DDR5 DIMMs.

**Recommended privilege model:** RigControl itself runs **unelevated**. For DRAM lighting the user, as a one-time manual step, installs PawnIO and installs OpenRGB with the MSI **service** option. The elevated service then owns SMBus and RigControl connects to it on 127.0.0.1:6742. Without the service, RigControl launches its own unelevated OpenRGB child and accepts that the DIMMs are missing. Never auto-elevate silently.

---

## 7. Conflicts with vendor software on the development PC

Installed or running per the live checks: NZXT CAM 4.76.9 (running: `NZXT CAM`, `cam_helper`); Armoury Crate Service 6.7.6.0 and AURA lighting effect add-on 0.0.21 (no lighting-service process seen); GIGABYTE Control Center 26.10.21.01 (running: `GCC`); NVIDIA App 11.0.9.585. Razer Synapse is not installed (no Razer service), but a Razer driver INF is bound to the mouse and keyboard MI_02 interfaces.

General rule: lighting controllers keep whatever was written last. Two programs writing to the same device cause flicker, or the vendor app "wins" on its next refresh. OpenRGB has no lock-out mechanism for USB HID. Vendor software is closed source, so everything about its behaviour below is **UNVERIFIED** and must be observed on the PC.

| Software | Devices contended | Expected effect | Mitigation (RigControl must not stop CAM or any service itself) |
|---|---|---|---|
| NZXT CAM (must stay running) | `1E71:2011`, `1E71:2021` ×2, and `1E71:300C` (CAM uses 300C for LCD, pump and fans) | CAM re-sends lighting periodically and overrides OpenRGB. Concurrent HID writes may interleave. | Default: **exclude NZXT controllers from OpenRGB** by setting `Detectors.detectors."NZXT RGB & Fan Controller"` and `"NZXT RGB Controller"` to false in RigControl's OpenRGB.json. Let CAM own NZXT lighting unless the user opts in. If they opt in, the user sets CAM lighting to a static or off profile and accepts possible overrides. |
| Armoury Crate / Aura | `0B05:19AF` (HID) and DRAM over SMBus ("Aura sync" of ENE DIMMs) | Aura overrides the mainboard and ARGB headers. Concurrent SMBus access risks corrupted transfers. | The user disables Aura lighting control in Armoury Crate or uninstalls the AURA components. Keep `shared_smbus_access = true`. Do not run DRAM detection while Armoury Crate's lighting service is active. |
| GIGABYTE Control Center (RGB Fusion) | GPU RGB controller at I2C 0x71; possibly SMBus | GCC overrides the GPU colour. | The user disables RGB Fusion or GPU lighting in GCC. Otherwise disable the OpenRGB detector "Gigabyte GeForce RTX 4070 Ti Eagle OC". |
| Razer Synapse (if installed later) | `1532:007D`, `1532:02A5` | Synapse/Chroma re-applies effects. | Keep Synapse uninstalled, or let Synapse own Razer lighting and disable the Razer detectors. |
| NVIDIA App | None for this Gigabyte card (NVIDIA's own LED control targets Founders Edition cards). **UNVERIFIED.** | Low risk | None |
| Another OpenRGB, SignalRGB, or similar | All | Port 6742 conflict and device fights | Check for an existing listener before launching. If one is found, connect to it as a client instead of starting a second server. |

---

## 8. Licence implications

| Component | Licence | Source |
|---|---|---|
| OpenRGB | GPL-2.0-or-later (`LICENSE` is GPL v2; files carry `SPDX-License-Identifier: GPL-2.0-or-later`) | `LICENSE:1-2`, e.g. `NetworkProtocol.h:9` |
| OpenRGB.NET | MIT | nuget.org package page |
| PawnIO driver | GPL-2.0 with an exception for independent modules that talk only through its IOCTL interface. The official signed build is distributed by pawnio.eu. | github.com/namazso/PawnIO README/LICENSE (web) |

This is an engineering assessment, **not legal advice**:

1. **Launching a separately installed OpenRGB and talking to it over TCP (recommended):** two independent programs communicating through a socket protocol. RigControl's own licence is unaffected. Copyleft obligations attach only to whoever distributes OpenRGB, which here would be the OpenRGB project, not us.
2. **Shipping unmodified OpenRGB binaries next to RigControl** (installer or zip), still started as a separate process: this is the GPL's "mere aggregation" case. RigControl stays under its own licence, but **we** become a distributor of OpenRGB and must ship the GPL text, provide the corresponding source (or a written offer) for that exact OpenRGB build, and honour the licences of its bundled libraries (Qt LGPL, hidapi, libusb and others; **UNVERIFIED** list, check the release zip's licence files).
3. **Embedding OpenRGB inside our exe** (linking its C++ code, loading it as a DLL in our process, or porting its code into C#): this makes a combined or derived work. RigControl would then have to be distributed under GPL-compatible terms with full source. Even "embedded resource extracted and run as a separate process" is legally murky. **Avoid it.**
4. **Hand-writing the SDK client from this spec:** protocol facts (ids, field order) are not copyrightable expression. Do not copy OpenRGB source text into RigControl. This spec deliberately paraphrases.
5. RigControl is built for one PC. If it is never distributed to anyone, GPL distribution obligations do not arise at all. They only matter if RigControl and OpenRGB are ever shared.

---

## 9. Verification checklist for implementers (closes the UNVERIFIED items)

| # | Check | How |
|---|---|---|
| 1 | Released 1.1 server reports protocol 6 | Hand-rolled client logs the reply to packet 40. |
| 2 | Headless launch shows no window or console | Launch with the recommended line and observe. Check `dumpbin /headers` subsystem. |
| 3 | Time until detection is complete on the development PC | Log the timestamps of DEVICE_LIST_UPDATED events and DETECTION_COMPLETE (v6). |
| 4 | ASUS 19AF detected; header LED counts | Detection log; ResizeZone; observe the strips; check whether the size persists after restart. |
| 5 | Kraken Elite ring reachable through a 2021 controller? | OpenRGB debug log `[NZXT Hue 2] … Device ID: 1E`. |
| 6 | GPU at 0x71 detected without admin | Run unelevated and look for the GPU controller. |
| 7 | DDR5 ENE detected with PawnIO and admin/service | Requires a user-performed PawnIO install. Check the log for "PawnIO initialized" and the ENE firmware string. |
| 8 | PawnIO loads with HVCI on | `sc query PawnIO`; Windows Security → Memory integrity. |
| 9 | OpenRGB.NET `DeviceListUpdated` never fires | Unit test with a USB replug. |
| 10 | Vendor-app override behaviour (CAM, Aura, GCC) | Set a colour via the SDK and watch for 60 s with each app in its normal state. |
| 11 | LoadProfile applies to hardware by itself (1.1) | Load a profile without sending UpdateMode and observe. |
| 12 | MSI uninstall DisplayName, Publisher and service account | Read the uninstall key and `sc qc OpenRGB` after a user-performed install. |

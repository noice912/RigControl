# Sensors and motherboard fan control (LibreHardwareMonitorLib 0.9.6)

> **About this file.** These are research notes written while RigControl was being developed, before and during the
> first builds. They describe the protocol and what was observed on the one development PC at that time. Where they
> say how RigControl should behave, the code and [docs/API.md](../API.md) are what counts today.

Status: research spec, written 2026-11-15. Target: one PC (see section 0).
Independently re-verified 2026-11-15 against the IL of the shipped 0.9.6 DLL, the HEAD clone and the dependency packages. The "Verification log" at the end lists every correction and every confirmed item. Read section 4.4 pitfall **P7** before writing any fan-control code: 0.9.6 writes a stale duty (0 % by default) before the requested one.
Implementers do not have the reference source, so this document describes behaviour in prose and tables. Statements marked **UNVERIFIED** were not confirmed in source or on the development PC. Each one says what would confirm it. "(0.9.6 IL)" means the statement was read from the IL of the shipped binary, which is the code RigControl actually runs.

## Sources and conventions

| Short name | Location | Notes |
|---|---|---|
| PKG | `%USERPROFILE%/.nuget/packages/librehardwaremonitorlib/0.9.6` | The shipped binary. Its nuspec says it was built from commit `3d331e3370efb858411f19511373eff65a218701`. File version `0.9.6.0`, product version `0.9.6+3d331e3370efb858411f19511373eff65a218701`. |
| HEAD | `.../scratchpad/ref/LibreHardwareMonitor` (shallow clone; tip commit `9e2d677`, 2026-11-11) | This is not 0.9.6. The 0.9.6 commit is not in the shallow clone. Where 0.9.6 and HEAD differ, this spec says so (section 9). |
| LIB | `HEAD/LibreHardwareMonitorLib` | Every source path below is relative to LIB unless it says otherwise. |

How 0.9.6 facts were checked: string scans of `PKG/runtimes/win-x64/lib/net8.0/LibreHardwareMonitorLib.dll`, plus a metadata/IL read of the same DLL with System.Reflection.Metadata. The read was data-only: the DLL was never loaded or executed. When a behaviour is described from HEAD source and could not be confirmed in the 0.9.6 binary, the text says "(HEAD)". HEAD line numbers are given for orientation only; where HEAD and the 0.9.6 IL disagree, the 0.9.6 IL wins and the difference is listed in section 9.

Web access worked and was used only to cross-check (section 12). The local clones remain the primary source.

---

## 0. The development PC: observed state (read-only checks, 2026-11-15)

| Item | Observed value | How it was observed |
|---|---|---|
| SMBIOS board | `ASUSTeK COMPUTER INC.` / `ROG STRIX Z790-A GAMING WIFI II`, BIOS 2301 (09/18/2026) | `HKLM\HARDWARE\DESCRIPTION\System\BIOS` |
| HVCI (memory integrity) | Running | `Win32_DeviceGuard.SecurityServicesRunning` contains 2. `...\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled=1` |
| Vulnerable driver blocklist | Enabled | `HKLM\SYSTEM\CurrentControlSet\Control\CI\Config\VulnerableDriverBlocklistEnable=1` |
| PawnIO | **Not installed** | No `...\Uninstall\PawnIO` key in either registry view. No PawnIO service or driver file found. |
| WinRing0 | Not present | No WinRing0 service, driver file or loaded driver |
| Other low-level drivers | `Asusgio3` (`AsIO3.sys`, Armoury Crate) is registered as system-start but its state is **Stopped**. NZXT CAM ships `cam_driver_mb.sys`, and GIGABYTE Control Center ships `MyPortIO_x64.sys`. Neither shows up as a loaded system driver. | `Win32_SystemDriver`, folder listing |
| NVIDIA libs | `C:\Windows\System32\nvml.dll` and `nvapi64.dll` are present | file check |
| Board fan headers (ASUS spec page) | CPU_FAN, CPU_OPT, AIO_PUMP and 5 chassis headers (8 in total), plus 1 thermal-sensor header | rog.asus.com spec page (web) |

Why Asusgio3 is stopped is **UNVERIFIED**. A Code Integrity block is plausible, but the CodeIntegrity/Operational and System logs contained no event naming it. To confirm, run `sc query Asusgio3` after a reboot and check the CodeIntegrity log for events 3077 or 3033 naming AsIO3.

---

## 1. Ring-0 access in 0.9.6: PawnIO, not WinRing0

### 1.1 Facts

| Question | Answer | Evidence |
|---|---|---|
| Which kernel driver? | **PawnIO**. 0.9.6 contains no WinRing0 code or strings. | The 0.9.6 DLL contains the device path `\\?\GLOBALROOT\Device\PawnIO`, the type `LibreHardwareMonitor.PawnIo.PawnIo`, and no `WinRing0` string. HEAD has no WinRing0 code (`PawnIo/PawnIo.cs`). |
| Is the driver embedded in the NuGet package? | **No.** Only PawnIO *modules* are embedded, as compiled `.bin` scripts in manifest resources. The PawnIO kernel driver must already be installed. | Resource names in the 0.9.6 DLL: `LibreHardwareMonitor.Resources.PawnIo.{AMDFamily0F, AMDFamily10, AMDFamily17, IntelMSR, IsaBridgeEC, LpcACPIEC, LpcCrOSEC, LpcIO, RyzenSMU, SmbusI801, SmbusNCT6793, SmbusPIIX4}.bin`. The `PawnIO_setup.exe` installer is embedded only in the *GUI app* (`HEAD/LibreHardwareMonitor.Windows.Forms/UI/MainForm.cs`, `InstallPawnIO` / `ExtractPawnIO`), not in the library. |
| Is anything extracted to disk at runtime? | **No.** Modules are read from the manifest resource into memory and sent to the driver with an IOCTL. | `PawnIo/PawnIo.cs` `LoadModuleFromResource`: it opens the device, reads the resource stream into a byte array, then calls `DeviceIoControl(LoadBinary)`. |
| How does the library find the driver? | It opens `\\?\GLOBALROOT\Device\PawnIO` with read/write access and `OPEN_EXISTING`. It does not create or start a service. | `PawnIo/PawnIo.cs` `LoadModuleFromResource` |
| How does it know PawnIO is installed? | A static constructor reads `DisplayVersion` from `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO` (native view first, then the 64-bit view). Public API: `PawnIo.IsInstalled` and `PawnIo.Version`. | `PawnIo/PawnIo.cs` lines 23–54. These members are confirmed public in the 0.9.6 metadata. |
| Minimum PawnIO version | The GUI prompts for an update when the version is below `2.0.0.0`. Per its GitHub release notes, 0.9.6 updated its modules to PawnIO 2.2 and fixed compatibility with a newer PawnIO release (#2174, #2222). | `MainForm.cs` line 173. Release page (web). |
| Service name of the PawnIO driver | **UNVERIFIED.** The library never references a service name. | Confirm after installing with `Get-CimInstance Win32_SystemDriver \| ? PathName -match pawn`. |
| IOCTL codes | Device type `41394 << 16` (`0xA1B2_0000`). LoadBinary = `0xA1B2_0000 \| (0x821 << 2)` = **`0xA1B22084`** (this exact constant is in the 0.9.6 IL). Execute = `0xA1B2_0000 \| (0x841 << 2)` = **`0xA1B22104`**. The Execute input buffer is a 32-byte zero-padded ASCII function name (at most 31 characters are copied) followed by the `long[]` arguments; the output is `long[]`. | `PawnIo/PawnIo.cs` lines 16–19 and 158–162. For information only; RigControl must not call these itself. |

### 1.2 What happens when the driver is missing or cannot be opened

`LoadModuleFromResource` returns an object whose `IsLoaded` is false, and `Execute(...)` then **returns an all-zero array instead of throwing** (`PawnIo/PawnIo.cs` lines 75–76 and 98–121; same in 0.9.6 IL). `IntelMsr.ReadMsr` wraps `Execute` in a try/catch and returns **true** whenever nothing throws, so with no driver every MSR read "succeeds" with the value 0 (0.9.6 IL). Every hardware class therefore quietly reads zeros:

| Area | Behaviour with no PawnIO handle | Evidence |
|---|---|---|
| Super I/O (fans, voltages, board temps, fan control) | Chip detection reads ID 0, so **no SuperIO sub-hardware is created**. The motherboard node (`/motherboard`) exists but has no sub-hardware, no sensors and no controls. | `Hardware/Motherboard/Lpc/LpcPort.cs` (all I/O goes through `PawnIo.LpcIo`). `Lpc/LpcIO.cs` `Detect`. |
| Intel CPU temperatures | The thermal-status MSR reads 0. Its "valid" bit (`0x80000000`) is clear, so core and package temperature `Value` is **null**, not a wrong number. "Core Max", "Core Average" and the "Distance to TjMax" sensors exist but stay null. TjMax read from MSR becomes 0. | `Hardware/Cpu/IntelCpu.cs` lines 549–552, 588–605, 620–630; 0.9.6 IL of `IntelCpu.Update` |
| Intel CPU package/core power (RAPL) | **The power sensors are created and read a bogus `0` W** ("CPU Package", "CPU Cores", "CPU Memory", "CPU Platform"; power indexes 0, 1, 3, 4). The power-unit MSR (`0x606`) "succeeds" with 0, which gives an energy multiplier of `1 / (1 << 0) = 1`, which is non-zero, so the sensors are added; each update then computes `1 × (0 − 0) / Δt = 0`. These zeros are not null and must be suppressed by RigControl (section 8). | `IntelCpu.cs` lines 478–515 and 694–714; 0.9.6 IL of the `IntelCpu` constructor (`ldc.i4 0x606`, `brfalse` on the `ReadMsr` result, multiplier compared with 0) |
| CPU per-core clocks | **Null, not degraded.** The "P-Core #n"/"E-Core #n" clock sensors are activated in the constructor, but `MSR_PLATFORM_INFO` (`0xCE`) reads 0, so the TSC multiplier is 0 and the whole clock block in `Update` is skipped (`HasTimeStampCounter && multiplier > 0`). There is no TSC-only fallback on this path. "Bus Speed" never appears. The per-core VID voltage sensors are removed on the first update; the "CPU Core" voltage sensor is never created. | `IntelCpu.cs` lines 332–333, 443–450, 634–692, 732–755; 0.9.6 IL (`ldc.i4 0xCE`, gate on `_timeStampCounterMultiplier`) |
| DIMM temperatures (SPD hub over SMBus) | Not detected. `RAMSPDToolkitDriver.LoadModule` only returns a module when `IsLoaded` is true. The background retry task still runs (section 3.1) and finds nothing. | `RAMSPDToolkitDriver.cs` lines 44–50 |
| CPU load, RAM usage, NVIDIA GPU, network | Unaffected. These do not use PawnIO (section 2). | — |

The library raises no error or event when PawnIO is missing, and no public member says whether a module actually loaded (`PawnIo.IsLoaded` is an instance property on objects the library keeps private). RigControl has to check `PawnIo.IsInstalled` itself, treat "SuperIO sub-hardware exists" as the proof that the driver is usable, and decide which UI to show.

### 1.3 Consequences on the development PC

| Topic | Assessment |
|---|---|
| WinRing0 / Defender | Not relevant to 0.9.6, which contains no WinRing0. Microsoft Defender flags WinRing0 as `VulnerableDriver:WinNT/Winring0` (also reported as `HackTool:Win32/Winring0`, CVE-2020-14979) and quarantines it (web, section 12). **RigControl must never ship or load WinRing0**, for example from an older LHM, OpenHardwareMonitor or older OpenRGB build. |
| Vulnerable driver blocklist | Enabled here. It targets known-bad drivers. Whether any PawnIO release is on it is **UNVERIFIED**. As of this writing the major monitoring projects ship PawnIO, which suggests it is not blocked (web). To confirm, install PawnIO and check that CodeIntegrity/Operational has no 3077/3033 event for its `.sys`. |
| HVCI on | The PawnIO website says the official build is "digitally signed". Whether that signature and the driver code meet HVCI requirements is **UNVERIFIED**: no source states it. To confirm, install official PawnIO with HVCI on and check that a PawnIO driver shows State=Running in `Win32_SystemDriver` and that CodeIntegrity logs no block. The unsigned "unrestricted" edition will **not** load with HVCI on, so do not use it. |
| Installing PawnIO | This is a user decision and RigControl must not install it silently. Installing it adds a kernel driver. Third-party reports say some anti-cheat systems react to it (web, single forum source, **UNVERIFIED**). RigControl should show "Full sensors need PawnIO (https://pawnio.eu) and administrator rights" with a link. Installing it from RigControl's UI is optional and needs an explicit user action. |
| Current state | PawnIO is not installed, so today **only the non-driver sensors work**, even when RigControl runs elevated. |

---

## 2. Administrator requirement and unelevated behaviour

Opening `\Device\PawnIO` needs administrator rights. The library does not document this. OpenRGB's documentation states it: "You must run the application as Administrator in order for PawnIO to be able to access SMBus" (`ref/OpenRGB/Documentation/SMBusAccess.md` line 14, also `ref/OpenRGB/DetectionManager.cpp` line 55). The exact ACL on the PawnIO device object is **UNVERIFIED**. To confirm, install PawnIO and check from an unelevated process whether `PawnIo.IsInstalled == true` while the Super I/O node is still empty. The LHM README also says some sensors need admin and that its GUI uses `requireAdministrator` (`HEAD/README.md` line 108, `HEAD/LibreHardwareMonitor.Windows.Forms/Resources/app.manifest` line 10).

### 2.1 What works in each mode (0.9.6, the development PC)

| Data | Source inside LHM | Not elevated | Elevated, PawnIO missing | Elevated + PawnIO |
|---|---|---|---|---|
| CPU load (total and per thread) | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` (`Hardware/Cpu/CpuLoad.cs` line 65) | Yes | Yes | Yes |
| CPU core / package temperature, distance to TjMax | MSR via PawnIO `IntelMSR.bin` | **No** (null) | **No** (null) | Yes |
| CPU package / cores / DRAM power | RAPL MSRs via PawnIO | **No, but the sensors exist and read a bogus 0 W** (section 1.2) | Same: bogus 0 W | Yes |
| CPU effective clocks | MSR plus TSC | **No** (sensors exist, value null) | **No** (null) | Yes |
| Super I/O temperatures, voltages, fan RPM | PawnIO `LpcIO.bin` | **No** (no SuperIO node) | **No** | Yes |
| Motherboard fan control (`IControl`) | PawnIO `LpcIO.bin` | **No** | **No** | Yes |
| DIMM temperatures (DDR5 SPD hub) | PawnIO `SmbusI801.bin` via RAMSPDToolkit-NDD 1.4.2 | No | No | **UNVERIFIED**, probably yes. Confirm that `/memory/dimm/N` nodes appear. |
| RAM used / available / load | `GlobalMemoryStatusEx` (`Hardware/Memory/MemoryWindows.cs`) | Yes | Yes | Yes |
| NVIDIA GPU temperatures, load, clocks, fan RPM, power | NvAPI (`nvapi64.dll`) and NVML (`nvml.dll`), loaded with `LoadLibrary` | Yes (**UNVERIFIED** for every NVML field; confirm unelevated) | Yes | Yes |
| Network throughput and totals | .NET `NetworkInterface` statistics (`Hardware/Network/Network.cs`) | Yes | Yes | Yes |
| Storage SMART, temperature, life | DiskInfoToolkit 1.1.2 | **UNVERIFIED**. Physical-drive SMART normally needs admin. Confirm unelevated. | Yes (**UNVERIFIED**) | Yes |
| Storage free / total / used space | DiskInfoToolkit 1.1.2 (`Storage.TotalSize`, `TotalFreeSize`) | **UNVERIFIED** | Yes | Yes |
| Storage read/write activity and rate | 0.9.6 opens the disk's physical path with `CreateFile(access = 3, i.e. FILE_READ_DATA \| FILE_WRITE_DATA)` on every update and sends `IOCTL_DISK_PERFORMANCE` (`0x70020`) (0.9.6 IL of `StorageDevice.UpdatePerformanceSensors`) | **Expected not to work**: a read/write-data handle on `\\.\PhysicalDriveN` normally needs admin. **UNVERIFIED** at runtime; confirm unelevated. | Yes | Yes |

Global mutex note: `Mutexes.Open` tries to *create* `Global\...` mutexes with an Everyone:FullControl ACL. If that throws `UnauthorizedAccessException` it falls back to `OpenExisting`, and if that fails too it stores null. A null mutex makes every `Wait*` call return **true**, meaning no cross-process lock (`Hardware/Mutexes.cs` lines 19–55 and 119–136; same five names in the 0.9.6 IL). Creating a named *mutex* in the `Global\` namespace does **not** need `SeCreateGlobalPrivilege` (Windows requires that privilege only for global file-mapping objects and symbolic links), so an unelevated process normally creates these mutexes successfully. The `UnauthorizedAccessException` path is taken when another program already created the mutex with an ACL that excludes this user. An `AbandonedMutexException` is treated as "acquired". Mutex ownership belongs to the acquiring thread; LHM always acquires and releases inside one call, so this only matters if RigControl takes one of these mutexes itself.

---

## 3. API (0.9.6 public surface, confirmed from metadata)

### 3.1 `Computer`

| Member | Behaviour |
|---|---|
| `new Computer()` / `new Computer(ISettings)` | The default `ISettings` is a no-op: it never persists and always returns the default (`Hardware/Computer.cs` lines 711–728). See 4.6 for why that matters. |
| `IsCpuEnabled`, `IsGpuEnabled`, `IsMemoryEnabled`, `IsMotherboardEnabled`, `IsStorageEnabled`, `IsNetworkEnabled`, `IsControllerEnabled`, `IsPsuEnabled`, `IsPowerMonitorEnabled`, `IsBatteryEnabled` | Set them before `Open()`. Changing one after `Open()` adds or removes that group immediately (`Computer.cs` lines 96–311). |
| `Open()` | Idempotent. Reads SMBIOS, opens the global mutexes (Windows 8+), allocates the RDTSC/CPUID stub with `VirtualAlloc(PAGE_EXECUTE_READWRITE)` (`Hardware/OpCode.cs`), then creates the groups. **Hardware detection and PawnIO module loads happen here, synchronously.** It can take noticeable time: TSC frequency estimation busy-waits (up to 5 windows of 25 ms with the thread pinned to the first logical CPU), and storage enumeration runs here. With PawnIO usable, `Open()` also **writes** to the Super I/O configuration ports (section 4.1.1) and starts SMBus traffic to the DIMM SPD hubs (section 9). Never call it on the UI thread. |
| `Close()` | Removes every group, last first. Each removal calls the group's `Close()`, which restores motherboard and GPU fan controls (section 4.5). Then it frees the code stub and closes the mutexes (`Computer.cs` lines 653–672). Calling it when not open does nothing. |
| `Reset()` | Close and re-create all groups without re-opening SMBIOS. The LHM GUI calls it on resume from sleep (`HEAD/LibreHardwareMonitor.Windows.Forms/UI/MainForm.cs` lines 599–605). |
| `Hardware` | Returns a snapshot list of the top-level hardware from every group, taken under the internal lock. |
| `Accept(IVisitor)` / `Traverse(IVisitor)` | `Accept` calls `visitor.VisitComputer(this)`. `Traverse` calls `Accept` on every top-level hardware under the internal lock. |
| `HardwareAdded` / `HardwareRemoved` | Raised for groups added or removed at runtime, **and by two groups from threads RigControl does not own** (0.9.6 IL): (1) `MemoryGroup.AddDimms` invokes `HardwareAdded` for each DIMM. If the first SPD scan inside `Open()` finds nothing, a thread-pool task retries **up to 4 times, 2.5 s apart** (at about 2.5, 5, 7.5 and 10 s after `Open()`; `Hardware/Memory/MemoryGroup.cs` lines 125–143 and 193–195), so the event can fire on a thread-pool thread while the owner thread is in the middle of an update. `MemoryGroup` never raises `HardwareRemoved`. (2) `StorageGroup.OnStoragesChanged` raises both events when DiskInfoToolkit reports a disk arriving or leaving, on DiskInfoToolkit's thread. **Handlers must only post a "re-read `Computer.Hardware`" message to the owner thread** and must not touch the `IHardware` argument. As a belt-and-braces measure, also re-read `Computer.Hardware` about 15 s after `Open()`. |
| `GetReport()` | Full text dump. Use it for the diagnostics page. |
| `SMBios` | Throws if not open. |

`Computer` is **not `IDisposable`** (the metadata shows only the members above). Wrap it in your own `IDisposable` that calls `Close()`.

### 3.2 Group flags RigControl must use on the development PC

| Flag | Value | Reason |
|---|---|---|
| `IsCpuEnabled` | true | |
| `IsGpuEnabled` | true | NVIDIA via NvAPI/NVML. It also creates `AmdGpuGroup`, which looks for `atiadlxx.dll` (absent here), and `IntelGpuGroup` (the 13900KF has no iGPU). Harmless. **Warning:** the NVIDIA node also exposes fan `IControl` objects ("GPU Fan" control sensors under `/gpu-nvidia/0/control/<n>`). Calling `SetSoftware` on one changes the graphics-card fan through `NvAPI_GPU_ClientFanCoolersSetControl` / `NvAPI_GPU_SetCoolerLevels` (0.9.6 IL), with no PawnIO and possibly no elevation needed. The sensors module must never call `SetSoftware`/`SetDefault` on a control whose `Sensor.Hardware.HardwareType` is not `SuperIO`. GPU fan control belongs to the NVIDIA module (`nvidia.md`). The 12VHPWR I2C reads in this class are gated on ASUS Astral subsystem IDs and do not run for the Gigabyte card. |
| `IsMemoryEnabled` | true | |
| `IsMotherboardEnabled` | true | |
| `IsStorageEnabled` | true | |
| `IsNetworkEnabled` | true | |
| **`IsControllerEnabled`** | **false (mandatory)** | This flag adds eight groups in 0.9.6: T-Balancer (FTDI, `Ftd2xx.dll`), Heatmaster (serial ports), AquaComputer, AeroCool, **NZXT**, **Razer**, ARCTIC and MSI. The NZXT group enumerates every HID device with VID `0x1E71` and asks each one for its product string. For the listed PIDs, which include the Kraken Elite (`0x300C`, handled by `KrakenV3`), it opens a HID stream, **writes** a firmware request (`0x10 0x01`) on open and a status request (`0x74 0x01`) on every update, and can write pump/fan targets (`Hardware/Controller/Nzxt/NzxtGroup.cs` lines 22–60, `KrakenV3.cs` lines 11–75 and 206; same PIDs and bytes in the 0.9.6 IL). The `0x2011` and `0x2021` controllers fall in the "unknown PID" branch and are only enumerated. That conflicts with NZXT CAM and with RigControl's own NZXT module (see `nzxt-kraken-elite.md`). The Razer group enumerates HID devices with VID `0x1532`. |
| `IsPsuEnabled` | false | Corsair/MSI PSU HID probing; there is no such PSU. |
| `IsPowerMonitorEnabled` | false | Probes serial ports for a Thermal Grizzly WireView Pro 2 (`WireViewPro2.TryFindDevices`, `System.IO.Ports`; 0.9.6 IL). Not needed. (`Ftd2xx.dll` belongs to the T-Balancer controller group, not to this flag.) |
| `IsBatteryEnabled` | false | Desktop PC. |

### 3.3 Update pattern (IVisitor)

`IVisitor` has `VisitComputer`, `VisitHardware`, `VisitSensor` and `VisitParameter`. The canonical updater (`HEAD/README.md` "UpdateVisitor"):

| Method | Body |
|---|---|
| `VisitComputer(c)` | `c.Traverse(this)` |
| `VisitHardware(h)` | `h.Update()`, then `sub.Accept(this)` for each `sub` in `h.SubHardware` |
| `VisitSensor`, `VisitParameter` | empty |

Recurse into `SubHardware`. `Motherboard.Update()` does nothing, and the Super I/O chip is a **sub-hardware** of the motherboard (`Hardware/Motherboard/Motherboard.cs` lines 177–178 and 82–94). `SensorVisitor(SensorEventHandler)` is a ready-made visitor that calls a delegate for every sensor.

### 3.4 Tree, types and units

`IHardware`: `Name` (settable), `Identifier`, `HardwareType`, `Parent`, `SubHardware[]`, `Sensors[]`, `Properties`, `Update()`, `GetReport()`, `SensorAdded`/`SensorRemoved`.

`HardwareType` (0.9.6 metadata, same in HEAD): Motherboard=0, SuperIO=1, Cpu=2, Memory=3, GpuNvidia=4, GpuAmd=5, GpuIntel=6, Storage=7, Network=8, Cooler=9, EmbeddedController=10, Psu=11, Battery=12, PowerMonitor=13.

`ISensor`: `Name` (settable), `Identifier`, `Index`, `SensorType`, `Value` (`float?`, where null means no reading), `Min`, `Max`, `Values` (history), `ValuesTimeWindow`, `Parameters`, `Control` (`IControl` or null), `IsDefaultHidden`, `Hardware`, `ResetMin/ResetMax/ClearValues`.

`SensorType` **as shipped in 0.9.6** (from metadata; HEAD differs, see section 9):

| Value | Name | Unit |
|---|---|---|
| 0 | Voltage | V |
| 1 | Current | A |
| 2 | Power | W |
| 3 | Clock | MHz |
| 4 | Temperature | °C |
| 5 | Load | % |
| 6 | Frequency | Hz |
| 7 | Fan | RPM |
| 8 | Flow | L/h |
| 9 | Control | % (current duty) |
| 10 | Level | % |
| 11 | Factor | dimensionless |
| 12 | Data | **Sensor-specific in 0.9.6.** GiB (2^30 bytes) for memory and network totals: `MemoryWindows.Update` and `Network.Update` divide by 1073741824 (0.9.6 IL). **Decimal GB (10^9 bytes)** for storage "Free Space" and "Total Space": `BlackSharp.Core ... ByteToGigabyte` divides by 1000 three times (0.9.6 IL). Storage "Data Read"/"Data Written" unit is **UNVERIFIED** (read the value against Windows on the development PC). HEAD stores bytes. |
| 13 | SmallData | **MiB** in 0.9.6 (0.9.6 IL): "GPU Memory Free/Used/Total" are NvAPI kilobytes divided by 1024; "D3D Dedicated/Shared Memory Used" are bytes divided by 1024 twice. This value does not exist in HEAD. |
| 14 | Throughput | **B/s** in 0.9.6 (0.9.6 IL: network "Upload Speed"/"Download Speed" are `Δbytes / Δseconds` with no further scaling; same as HEAD `Network.cs` lines 81–82). Also used for storage "Read Rate"/"Write Rate" and "GPU PCIe Rx/Tx". |
| 15 | TimeSpan | s |
| 16 | Timing | ns |
| 17 | Energy | mWh |
| 18 | Noise | dBA |
| 19 | Conductivity | µS/cm |
| 20 | Humidity | % |

Rules: persist and compare `SensorType` by **name**, never by number. Read `Data` sensors through a per-sensor unit table that is checked against the LHM version in use.

`Values` history: by default every sensor keeps one day of 4-sample averages in a `List` (`Hardware/Sensor.cs` lines 26 and 107–128, HEAD). RigControl keeps its own history, so it must set `ValuesTimeWindow = TimeSpan.Zero` on every sensor, including sensors added later. That bounds memory use and avoids a non-thread-safe list.

### 3.5 Identifiers (stable keys)

`Identifier` is a `/`-joined path (`Hardware/Identifier.cs`). **Every path segment is passed through `Uri.EscapeDataString`** (0.9.6 IL), so characters such as `{`, `}` and spaces appear percent-encoded. A sensor's identifier is `<hardware id>/<sensortype lowercase>/<index>` (`Hardware/Sensor.cs` line 82; 0.9.6 IL). A control's identifier is `<sensor id>/control` (`Hardware/Control.cs` line 28; 0.9.6 IL).

| Hardware | Identifier pattern | Stability |
|---|---|---|
| Intel CPU | `/intelcpu/0` (`Cpu/GenericCpu.cs` lines 133–142) | Stable |
| NVIDIA GPU | `/gpu-nvidia/<adapterIndex>` (`Gpu/NvidiaGpu.cs` line 106) | Stable while there is only one GPU |
| Intel CPU sensors | `/intelcpu/0/load/0` "CPU Total", `/intelcpu/0/load/1` "CPU Core Max", `/intelcpu/0/load/<thread + 2>` per thread; temperatures and powers as in section 1.2 | Stable (HEAD `GenericCpu.cs` lines 59–75; indexes not re-read from the 0.9.6 IL, confirm with `GetReport()`) |
| RAM | `/ram` (hardware "Total Memory"): `/ram/data/0` "Memory Used", `/ram/data/1` "Memory Available", `/ram/load/0` "Memory". `/vram` (hardware "Virtual Memory"): `/vram/data/2` "Memory Used", `/vram/data/3` "Memory Available", `/vram/load/1` "Memory". | Stable. Confirmed in the 0.9.6 IL of the `TotalMemory` and `VirtualMemory` constructors. |
| DIMM | `/memory/dimm/<index>` | Depends on SMBus scan order |
| Motherboard | `/motherboard` | Stable |
| Super I/O | `/lpc/<chip lowercase>/<n>`, for example `/lpc/nct6798d/0` (`Motherboard/SuperIOHardware.cs` line 33) | Stable if the chip is detected the same way each time |
| Fan RPM | `/lpc/nct6798d/0/fan/<i>` | |
| Fan duty / control sensor | `/lpc/nct6798d/0/control/<i>`. The `IControl` is `.../control/<i>/control`. | |
| Storage | **0.9.6: `/<nvme\|ssd\|hdd>/<DriveNumber>`**, for example `/nvme/0` (0.9.6 IL: `StorageGroup.GetID` returns `nvme` if `IsNVMe`, else `ssd` if `IsSSD`, else `hdd`; the second segment is DiskInfoToolkit's `Storage.DriveNumber`). HEAD uses a different scheme (`/<ata\|scsi\|nvme\|usb\|...>/<id>`, `Storage/StorageDevice.cs` lines 227–274). | Changes if Windows renumbers the disks, and changes when LHM is upgraded |
| Network | `/nic/<escaped NetworkInterface.Id>`. On Windows the id is a braced GUID, so the identifier reads `/nic/%7BXXXXXXXX-...%7D`. Sensors (0.9.6 IL): `data/2` "Data Uploaded", `data/3` "Data Downloaded", `throughput/7` "Upload Speed", `throughput/8` "Download Speed", `load/1` "Network Utilization". | Stable per adapter |

Sensor indexes are fixed per board configuration, so identifiers stay stable across runs **as long as the LHM version and board-support code are unchanged**. Upgrading LHM can rename or re-index sensors: for example, a future release could add an entry for this board model. RigControl should store user mappings (for example "Front intake = `/lpc/nct6798d/0/control/0`") together with the LHM version, and show a re-mapping prompt when the version changes.

---

## 4. Motherboard fan control

### 4.1 Detection path for this board

| Step | Result on the development PC | Evidence |
|---|---|---|
| Manufacturer | `Manufacturer.ASUS`. SMBIOS starts with "ASUSTeK". | `Motherboard/Identification.cs` line 31 |
| Model | **`Model.Unknown`**. "ROG STRIX Z790-A GAMING WIFI II" is in neither 0.9.6 (string scan of the DLL: Z790 entries are -E, -E II, -I, Maximus Hero/Dark Hero/Formula) nor HEAD (`Identification.cs` lines 628–679). Matching is exact (`Equals`, OrdinalIgnoreCase). | |
| Super I/O chip | Expected to be a Nuvoton NCT6798D or NCT6799D (chip ID `0xD4…`/`0xD8…`, `Lpc/LpcIO.cs` lines 374–443). **UNVERIFIED for this board.** LHM marks the sibling ROG STRIX Z790-E GAMING WIFI II as NCT6798D (`SuperIOHardware.cs` line 4908). Web search found no confirmation. Confirm with `GetReport()` while elevated with PawnIO (section "LpcIO" / "Chip Id"), or with HWiNFO. | |
| Board-specific config | NCT6798D and NCT6799D both go through `GetNuvotonConfigurationD`. Under `Manufacturer.ASUS` with an unknown model, the **ASUS `default:` branch** is used (`SuperIOHardware.cs` lines 413–420 and 5663–5697). | |
| ASUS embedded controller (EC) | **Not created.** `EmbeddedController.Create(Model, …)` only covers listed models (`Lpc/EC/EmbeddedController.cs` lines 309–357 list Z790 Hero, Dark Hero, Strix -I/-E/-E II and Formula, not -A II). So the T_SENSOR header, the EC VRM temperature and similar are **unavailable**. | `Motherboard.cs` line 76 |
| ASUS WMI (ASUSHW) | Not used by LHM. The 0.9.6 DLL has no ASUSHW or ASUS WMI strings; its only WMI query is IPMI. | string scan |

### 4.2 Sensors and controls the ASUS default branch exposes

| Kind | Index → name (as LHM labels it) |
|---|---|
| Voltages | 0 "Vcore", 1 "Voltage #2" (hidden), 2 "AVCC", 3 "+3.3V", 4–6 "Voltage #5–#7" (hidden), 7 "+3V Standby", 8 "CMOS Battery", 9 "CPU Termination", 10–14 "Voltage #11–#15" (hidden) |
| Temperatures | 0 "CPU Core", 1–6 "Temperature #1"–"#6" |
| Fans (RPM) | "Fan #1"…"Fan #7", indexes 0–6. NCT6798D/6799D have 7 tach channels (`Lpc/Nct677X.cs` lines 231–237). |
| Controls (%) | "Fan #1"…"Fan #7", indexes 0–6 (7 PWM channels) |

The labels are generic and some are **wrong for this board**. On the Z790-E II, LHM reads CPU Package from index 0, Motherboard from 2, Chipset from 12, PCH from 13 and CPU from 22 (`SuperIOHardware.cs` lines 4910–4914). The default branch exposes only temperature indexes 0–6, so Chipset, PCH and the index-22 CPU reading are **not exposed at all** for this board. Voltage scaling in the default branch is also generic. Use the CPU node (`/intelcpu/0`) for CPU temperature, not Super I/O index 0.

### 4.3 Likely header mapping (UNVERIFIED)

The board has 8 fan headers but the chip has 7 PWM channels, so at least one header cannot be controlled independently. On many ASUS boards CPU_OPT follows CPU_FAN (**UNVERIFIED**). By analogy with the Z790-E II table (`SuperIOHardware.cs` lines 4916–4932):

| LHM control index | Z790-E II label in LHM | Assumed Z790-A II header (UNVERIFIED) |
|---|---|---|
| 0 | Chassis Fan #1 | CHA_FAN1 |
| 1 | CPU Fan | CPU_FAN (CPU_OPT may follow it) |
| 2 | Chassis Fan #2 | CHA_FAN2 |
| 3 | Chassis Fan #3 | CHA_FAN3 |
| 4 | Chassis Fan #4 | CHA_FAN4 |
| 5 | Chassis Fan #5 | CHA_FAN5 |
| 6 | AIO Pump | AIO_PUMP |

**Required:** do not hard-code this table. Ship a **user-driven header identification step**. It shows live RPM for each of the 7 fan sensors, lets the user name each one, and only after explicit consent briefly nudges one control (for example 100% for 5 s, then restore) so the user can see which fan responds. Store the names keyed by identifier plus LHM version.

Kraken Elite note: the Kraken's pump and radiator fans are powered and controlled over USB by the Kraken itself, not by a motherboard PWM header (see `nzxt-kraken-elite.md`). If its tach lead is plugged into CPU_FAN or AIO_PUMP, that header probably shows pump RPM only, and changing its PWM probably does nothing to the pump. Both points are **UNVERIFIED**; confirm from the physical wiring. RigControl must still apply a high floor to whichever control the user labels "pump".

### 4.4 Control semantics (`IControl`, `Hardware/Control.cs`; `SuperIOHardware.cs` lines 69–125; `Lpc/Nct677X.cs` lines 696–775)

| API | Effect on NCT679x |
|---|---|
| `ControlMode` | `Undefined` (0), `Software` (1), `Default` (2). It starts as **Undefined**: nothing is written until RigControl acts. |
| `SetSoftware(float percent)` | Stores the value and sets the mode to Software. The first time per channel per process, it **saves the current mode register and PWM register** to memory. It then writes the mode register `0x?02` (bank per channel: `0x102, 0x202, 0x302, 0x802, 0x902, 0xA02, 0xB02`) to **0 (manual)** and the PWM command register (`0x109, 0x209, 0x309, 0x809, 0x909, 0xA09, 0xB09`) to `(byte)(percent * 2.55f)`. |
| `SetDefault()` | Sets the mode to Default, which writes the saved mode and PWM bytes back. If `SetSoftware` was never called on that channel in this process, nothing is written. |
| `SoftwareValue`, `MinSoftwareValue`, `MaxSoftwareValue` | Last requested value. Min = **0**, Max = **100** for Super I/O (`SuperIOHardware.cs` line 77). |
| `Sensor` (type Control) | The read-back value is the **actual output duty**, read from `FAN_PWM_OUT_REG` (`0x001, 0x003, 0x011, 0x013, 0x015, 0xA09, 0xB09` for NCT6797D/6798D/6799D) and divided by 2.55 (`Nct677X.cs` lines 148–150 and 1066–1079). It shows the BIOS curve output too when the channel is not in Software mode. |

Pitfalls RigControl must code around:

| # | Pitfall | Required handling |
|---|---|---|
| P1 | **No clamping.** `percent * 2.55` is cast straight to `byte`. A value above 100 or below 0 produces an undefined or wrapped byte. | Clamp in RigControl to `[floor, 100]` before calling. |
| P2 | **Minimum 0% is allowed.** 0 PWM can stop fans. | Enforce RigControl floors. These are policy, not an LHM fact: suggested minimum 20% for chassis headers, 30% for CPU_FAN, 100% (or "never software-control") for any header labelled pump. |
| P3 | **Writes can be silently dropped.** `SetControl` waits only **10 ms** for `Global\Access_ISABUS.HTP.Method`; on timeout it returns without writing (`Nct677X.cs` line 707, HEAD). The mode and value properties have already changed, so LHM believes the write happened. | After each `SetSoftware`, check on the next update that the control sensor's value is within about ±2% of the request. If not, re-issue (see P4). |
| P4 | **No re-write on the same value.** The change events only fire when the mode or value actually changes (`Control.cs` lines 54–85). Calling `SetSoftware(v)` again with the same `v` does nothing. | To force a re-write, call `SetSoftware(v + 0.01f)` (it maps to the same byte) or `SetDefault()` then `SetSoftware(v)`. |
| P5 | **Chip ID gate.** `SetControl` is a no-op if the vendor-ID register does not read Nuvoton (`Nct677X.cs` lines 698–699 and 1349–1352). | Covered by P3 verification. |
| P6 | **Fighting other software.** Armoury Crate (Fan Xpert / AI Cooling), BIOS Q-Fan and any other tool that writes the Super I/O will overwrite RigControl's values, or the reverse. Whether Armoury Crate uses the same ISA mutex is **UNVERIFIED**. | While RigControl fan control is enabled, tell the user to leave Armoury Crate fan profiles idle. Detect drift (P3 check fails repeatedly) and show "another program is controlling fans". |

### 4.5 Restore behaviour, crashes and sleep

| Event | What happens | Evidence |
|---|---|---|
| `Computer.Close()` or `Reset()` | `SuperIOHardware.Close` calls `SetControl(i, null)` for every control, restoring the saved BIOS mode and PWM for every channel this process changed. | `SuperIOHardware.cs` lines 6331–6340 |
| Clean exit without `Close()` | Nothing is restored. | — |
| **Crash, kill (Task Manager / TerminateProcess), BSOD-free hang** | **Nothing is restored.** The NCT chip keeps manual mode and the last PWM byte. BIOS SmartFan control **does not resume on its own**. The saved "initial" bytes lived only in the dead process's memory. | `Nct677X.cs` lines 79–81 and 1458–1497 (arrays held in memory) |
| **Next RigControl start after a crash** | The new process's first `SetSoftware` saves the **current (manual) registers** as "initial". `SetDefault()` and `Close()` will then "restore" manual mode, not the BIOS curve. Pressing "Default" will not recover BIOS control. | Same as above |
| Reboot / cold boot | BIOS POST is expected to re-program the Super I/O fan mode and curve. **UNVERIFIED.** To confirm: set a channel to 30%, kill the process, reboot, and check that the duty follows the BIOS curve again. | — |
| Sleep (S3) / resume | Whether firmware re-initialises the Super I/O on resume is **UNVERIFIED**. LHM's GUI calls `Computer.Reset()` on `PowerModes.Resume` (`MainForm.cs` lines 599–605). | — |

Required RigControl behaviour:
1. Call `Computer.Close()` on: app exit, `SystemEvents.SessionEnding`/`SessionEnded`, `AppDomain.ProcessExit`, `AppDomain.UnhandledException` (best effort) and console Ctrl+C. The LHM GUI does this on SessionEnded (`MainForm.cs` lines 559–565).
2. Before the first `SetSoftware` of a session, write a marker file (for example `%LocalAppData%\RigControl\fan-override.active`). Delete it only after a successful `Close()`. If the marker exists at startup, warn: "RigControl did not shut down cleanly while controlling fans. Motherboard fans may be stuck at a fixed speed. Restart Windows to restore BIOS fan curves." While the marker exists, disable the "Return to BIOS control" button, because it cannot work (see above).
3. On `PowerModes.Resume`: call `Computer.Reset()`, then re-apply RigControl's own desired control state.
4. Keep floors (P2) high enough that "stuck at last value" is safe.

### 4.6 `ISettings` persistence trap

`Control` reads its saved mode and value from `ISettings` in its constructor. `SuperIOHardware` then **applies them immediately at `Open()`**: Software mode is re-written, and Default mode triggers a restore (`Control.cs` lines 33–44, `SuperIOHardware.cs` lines 101–111). `Sensor` and `Hardware` names are also stored there. **Use the default no-op settings (`new Computer()`)** and keep fan state in RigControl's own model. Otherwise fans change during `Open()`, before RigControl's safety checks run.

---

## 5. Threading, update interval, disposal

| Topic | Guidance | Evidence |
|---|---|---|
| Thread model | `Computer` locks its group list and `Traverse`. Individual `Hardware.Update()`, sensor values, `Values` lists and `IControl` setters are **not synchronised** (HEAD). `SetSoftware` runs the bus write synchronously on the calling thread. | `Computer.cs` lines 39, 83–92, 411–424 |
| Rule | Make **one dedicated worker thread** own the `Computer`: `Open`, every update tick, every `SetSoftware`/`SetDefault`, `Reset` and `Close`. The UI posts commands to a queue and reads immutable snapshots (identifier, value, min, max) that the worker publishes after each tick. Never touch `ISensor` objects from the UI thread. | Design rule |
| Interval | The LHM GUI offers 250 ms to 10 s and defaults to **1 s** (radio index 2, `MainForm.cs` lines 411–444). Use 1 s. Each tick reads MSRs on every logical CPU (32 on the 13900KF), switching thread affinity per read (`IntelMsr.ReadMsr` with `GroupAffinity`), and takes the ISA bus mutex. | |
| Storage | SMART refresh is throttled inside LHM (`StorageDevice.SmartUpdateCycleCount`). There is no need to update storage faster than about 5 s. | `MainForm.cs` lines 449–466 |
| Disposal | `Computer` has no `Dispose`. Call `Close()` exactly once, on the owning thread. After `Close()`, drop every `IHardware`/`ISensor` reference. | §3.1 |
| Re-entrancy | Do not call `Reset()` or toggle `Is*Enabled` while an update tick is running on another thread. | `Computer.cs` lines 415–423 (comment about collection-modified) |

---

## 6. Single-file publish pitfalls

| Topic | Finding | Action |
|---|---|---|
| Package layout | The **implementation DLLs exist only under `runtimes/win-x64|win-x86|win-arm64/lib/<tfm>`**, and `ref/<tfm>` holds reference assemblies only (`PKG` listing). TFMs: net472, netstandard2.0, net8.0, net9.0, net10.0. | Set `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`. Single-file needs a RID anyway. Without one, the RID-specific asset is resolved through `runtimes/` in deps.json. |
| Embedded resources | The PawnIO `.bin` modules are manifest resources in `LibreHardwareMonitorLib.dll` and are read with `GetManifestResourceStream`. That works inside a single-file bundle. | None |
| Embedded driver | None: the PawnIO driver is not in the package. Nothing is extracted. | Document the PawnIO prerequisite |
| Native files | `Mono.Posix.NETStandard 1.0.0` (dependency for net8/9/10) carries native `MonoPosixHelper.dll`/`libMonoPosixHelper.dll` for win-x64. LHM uses Mono.Posix only on its Unix code path (`OpCode.cs`, IsUnix branch). Single-file leaves native files next to the exe unless `IncludeNativeLibrariesForSelfExtract=true`. | Either accept the loose DLLs or set `IncludeNativeLibrariesForSelfExtract=true`. Whether excluding them entirely is safe is **UNVERIFIED**; test a publish with them removed. |
| `Assembly.Location` | The 0.9.6 DLL has no `get_Location`, `CodeBase` or `BaseDirectory` references (string scan). | No single-file location bug expected |
| Trimming / AOT | The 0.9.6 assembly carries no `IsTrimmable` or `IsAotCompatible` metadata (string scan). HEAD declares itself trimmable but suppresses IL2026, IL3050, IL2075 and IL2070 for System.Management WMI and OpCode (`LibreHardwareMonitorLib.csproj`). `OpCode` generates executable code at runtime. | **`PublishTrimmed=false`, no Native AOT.** |
| `System.Management` (WMI) | Used for IPMI detection (`SELECT * FROM Microsoft_IPMI`). Works in single-file on .NET 8+ (**UNVERIFIED** on the development PC). | Test the published exe |
| Defender / SmartScreen | `OpCode` allocates `PAGE_EXECUTE_READWRITE` memory. Some EDR products flag this. Defender behaviour is **UNVERIFIED**. | Sign the exe. Test with Defender real-time protection on. |
| Platform | x64 only on the development PC. Do not publish AnyCPU/x86. | `PlatformTarget=x64` |

---

## 7. Non-driver fallbacks for unelevated mode

These are standard Windows and NVIDIA APIs. Their behaviour was **not verified against the reference source**, because LHM does not use most of them. Each needs a smoke test on the development PC.

| Metric | Preferred unelevated source | Notes |
|---|---|---|
| CPU total load | Use LHM `/intelcpu/0/load/0` ("CPU Total"), or `PerformanceCounter("Processor Information", "% Processor Utility", "_Total")` | "% Processor Utility" matches Task Manager. "% Processor Time" does not on Windows 10/11. Performance counters need `System.Diagnostics.PerformanceCounter`. The first `NextValue()` returns 0. |
| CPU temperature | **None reliable.** `MSAcpi_ThermalZoneTemperature` (root\wmi) needs admin and on desktop boards usually reports a fixed ACPI value. | Show "needs administrator + PawnIO" |
| RAM | `GlobalMemoryStatusEx` (LHM `/ram` already does this) | Unit trap: 0.9.6 reports Used/Available in GiB |
| GPU temp, load, fan, power, VRAM | LHM NVIDIA node, or NVML directly (`nvmlDeviceGetTemperature`, `nvmlDeviceGetUtilizationRates`, `nvmlDeviceGetFanSpeed`, `nvmlDeviceGetPowerUsage`, `nvmlDeviceGetMemoryInfo`) from `C:\Windows\System32\nvml.dll` | NvAPIWrapper.Net 0.8.1.101 is also restored in the NuGet cache (see the GPU spec). |
| GPU load (vendor-neutral) | `PerformanceCounter("GPU Engine", "Utilization Percentage", <instance>*engtype_3D)`, summed | Instance names change as processes come and go, so re-enumerate. |
| Disks | `PerformanceCounter("PhysicalDisk", "% Disk Time"/"Disk Read Bytes/sec"/"Disk Write Bytes/sec", "<n> C:")`; `DriveInfo` for free space | SMART temperature unelevated is **UNVERIFIED**. `MSFT_StorageReliabilityCounter` (root\Microsoft\Windows\Storage) can give NVMe temperature but usually needs admin. |
| Network | `NetworkInterface.GetIPv4Statistics()` deltas (what LHM does), or `PerformanceCounter("Network Interface", "Bytes Received/sec")` | Skip virtual adapters (NordVPN creates TAP/WireGuard adapters) unless the user selects them. |
| Fans / board temps / voltages | **None.** There is no unelevated or WMI path on this board: ASUS WMI ASUSHW is not used, and its presence here is **UNVERIFIED**. | Show "needs administrator + PawnIO" |

---

## 8. Recommended run mode

**Recommendation: ship `asInvoker` and offer a "Restart as administrator" action. Do not use an always-admin (`requireAdministrator`) manifest.**

| Criterion | asInvoker + restart-elevated | requireAdministrator |
|---|---|---|
| Works with PawnIO missing (today's state) | Yes. CPU load, RAM, GPU and network work. Elevation would add nothing until PawnIO is installed. | A UAC prompt every launch for no gain |
| Start at logon | Run key or Startup folder works silently | Elevated apps cannot auto-start from the Run key without a prompt; needs a Task Scheduler "highest privileges" task |
| UIPI side effects | None | Elevated window blocks drag-drop from Explorer and some IPC with unelevated apps |
| Full sensors and fan control | After the user clicks "Restart as administrator" (`ProcessStartInfo { Verb = "runas", UseShellExecute = true }` plus a flag such as `--elevated`). The single-instance mutex hands off: the elevated instance waits for the old one to exit. | Always, once PawnIO is installed |
| Safety | Fan control is only possible in an explicit elevated session | Fan control is always possible |

Implementation requirements:
1. At startup, compute `IsElevated` and `PawnIo.IsInstalled` / `PawnIo.Version`. Show a status chip: "Basic sensors", "Admin, PawnIO missing" or "Full sensors".
2. In basic mode, still create `Computer` with the section 3.2 flags. LHM handles the missing driver by returning null values or omitting nodes; it does not throw (section 1.2). Hide null sensors rather than showing 0.
3. Show fan-control UI only when `IsElevated && PawnIo.IsInstalled && the SuperIO node exists`.
4. Optional later: a persistent "start elevated at logon" option using a scheduled task with "Run with highest privileges", created only on explicit user action.

---

## 9. 0.9.6 vs HEAD differences that matter

| Area | 0.9.6 (shipped) | HEAD clone | Impact |
|---|---|---|---|
| `SensorType` | Includes `SmallData = 13`; Throughput = 14 … Humidity = 20 | `SmallData` removed; Throughput = 13 … | Never persist enum numbers |
| `Data` units | GiB for memory and network totals (IL constant 1073741824) | Bytes | Version-specific unit table |
| PawnIO modules | 12 modules; **no `Nvidia.bin`, no `SmbusIntelSkylakeIMC.bin`** | Adds both | HEAD-only GPU hot-spot/memory temps via PawnIO (`Gpu/NvidiaGpu.cs` lines 170–200). 0.9.6 does have NvAPI-based "GPU Hot Spot" and "GPU Memory Junction" strings. |
| Dependencies | DiskInfoToolkit 1.1.2, RAMSPDToolkit-NDD 1.4.2, System.Management 10.0.2, HidSharp 2.6.4 | DiskInfoToolkit 2.2.2, RAMSPDToolkit-NDD 1.6.4, System.Management 11.0.0 | Storage and DIMM behaviour may differ from HEAD source |
| TFMs | net472, netstandard2.0, net8.0, net9.0, net10.0 | net472, netstandard2.0, net10.0, net11.0 | |
| Z790-A II support | None (Model.Unknown) | None | Generic ASUS labels |
| Global mutex names | `Access_ISABUS.HTP.Method`, `Access_PCI`, `Access_EC`, `RazerReadWriteGuardMutex`, `Access_USB_Sensors` (all present as strings) | Same (`Hardware/Mutexes.cs`) | RAMSPDToolkit separately uses `Access_SMBUS.HTP.Method` (string in its DLL), the same name OpenRGB uses (`ref/OpenRGB/i2c_smbus/Windows/i2c_smbus_pawnio.h` line 22). RigControl's own SMBus code (RAM RGB) must take that mutex too. |
| Exact `SetControl` timeouts / restore code | Not readable without decompiling | 10 ms ISA wait; restore as described | Treat section 4.4 P3 as applying to 0.9.6 too (**UNVERIFIED**) |

---

## 10. Verification checklist (run on the development PC before shipping)

| # | Check | Expected |
|---|---|---|
| V1 | Unelevated `GetReport()` | CPU load, NVIDIA GPU, RAM and network present. No SuperIO node. CPU temperatures null. |
| V2 | Elevated, PawnIO missing | Same as V1 |
| V3 | Install official PawnIO (user decision), check driver state and CodeIntegrity log | Driver running under HVCI, no block events |
| V4 | Elevated + PawnIO `GetReport()` | `LpcIO` section shows the chip (NCT6798D or NCT6799D) and a SuperIO node with 7 fans and 7 controls. CPU core and package temperatures not null. DIMM nodes appear within about 15 s. |
| V5 | Unelevated + PawnIO installed | Confirms whether the device needs admin (section 2) |
| V6 | Header identification wizard | Mapping table in section 4.3 confirmed or corrected |
| V7 | Set one chassis channel to 40%, kill the process, read the duty after 10 s, then reboot | Documents crash and reboot behaviour (section 4.5) |
| V8 | Sleep/resume with a channel in Software mode | Documents resume behaviour |
| V9 | NZXT CAM keeps running with no errors throughout | Confirms `IsControllerEnabled=false` is respected |
| V10 | Single-file publish (`PublishSingleFile`, `win-x64`, no trimming), run V1 and V4 | Same results as the framework-dependent build |

## 11. Hard rules for RigControl (summary)

1. `IsControllerEnabled = false` always on the development PC.
2. Use the default no-op `ISettings`; keep fan state in RigControl's own model.
3. One owner thread for every LHM call; publish snapshots to the UI.
4. Clamp every fan value to `[floor, 100]`; verify by read-back; re-issue with a nudge.
5. Call `Close()` on every exit path; use a crash marker file; `Reset()` and re-apply on resume.
6. Never ship WinRing0; never install PawnIO without explicit user action.
7. Default `asInvoker`; full sensors only when elevated with PawnIO installed.
8. Persist enum names, not numbers; keep a unit table per LHM version.
9. Set `ValuesTimeWindow = TimeSpan.Zero` on all sensors.

## 12. Web sources consulted (cross-checks only)

- LibreHardwareMonitor v0.9.6 release notes: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6 (PawnIO modules 2.2, #2174; new PawnIO installer fix, #2222).
- PawnIO home page: https://pawnio.eu/ (official edition signed, unrestricted edition unsigned). Source: https://github.com/namazso/PawnIO (GPL-2.0; README gives no ACL or HVCI details).
- Microsoft, "Microsoft Defender Antivirus alert - VulnerableDriver:WinNT/Winring0": https://support.microsoft.com/windows/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0-eb057830-d77b-41a2-9a34-015a5d203c42
- Neowin on Defender flagging WinRing0: https://www.neowin.net/news/windows-1110-is-flagging-winring0-on-your-pc-monitoring-fan-control-apps-heres-why/
- ASUS ROG Strix Z790-A Gaming WiFi II specifications (fan headers): https://rog.asus.com/us/motherboards/rog-strix/rog-strix-z790-a-gaming-wifi-ii/spec/
- Searches for "PawnIO + HVCI" and "Z790-A II Super I/O chip" returned nothing authoritative; both remain UNVERIFIED above.

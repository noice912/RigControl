# NVIDIA GPU control spec (RigControl)

> **About this file.** These are research notes written while RigControl was being developed, before and during the
> first builds. They describe the protocol and what was observed on the one development PC at that time. Where they
> say how RigControl should behave, the code and [docs/API.md](../API.md) are what counts today.

Scope: replace day-to-day use of NVIDIA App and NVIDIA Control Panel on **one** PC from C# (.NET 8, `net8.0-windows`, x64 — the project's target per the RigControl brief; an earlier draft said .NET 10, which was wrong), using NvAPIWrapper.Net 0.8.1.101, `nvml.dll`, and Win32 display APIs.

Status: research spec, **adversarially re-verified on 2026-11-15** (see "Verification log" at the end of this file for every correction and every confirmed item). Nothing in it was executed against the GPU: no NVAPI or NVML function was called by the author or by the verifier. Every write path below is **untested on the development PC** until the bring-up checklist (section 11) has been run.

---

## 0. Conventions, sources, and confidence

### 0.1 Source abbreviations

| Tag | Meaning |
|---|---|
| `NW/...` | NvAPIWrapper source clone, path relative to `ref/NvAPIWrapper/NvAPIWrapper/`. HEAD `2a41eb4d` ("new release", 2020-12-09). `NvAPIWrapper.csproj` declares `<Version>0.8.1.101</Version>`, so this clone **is** the 0.8.1.101 source. |
| `PKG` | Restored package `%USERPROFILE%/.nuget/packages/nvapiwrapper.net/0.8.1.101`. It contains `lib/net45` and `lib/netstandard2.0` only. The nuspec title reads "NvAPIWrapper (for NVAPI 410)" and the package has no dependencies. |
| `LHM/...` | LibreHardwareMonitor clone, path relative to `ref/LibreHardwareMonitor/`. HEAD `9e2d677c` (2026-11-11). |
| `HDR-H` | `NvApiDriverSettings.h` from github.com/NVIDIA/nvapi (main). Downloaded **raw** with curl on 2026-11-15 by the verifier (77,808 bytes). The header carries no version string, only "Copyright (c) 2019-2026". It declares `TOTAL_SETTING_NUM = 128`. |
| `NPI-CSN` | `nvidiaProfileInspector/CustomSettingNames.xml` from github.com/Orbmu2k/nvidiaProfileInspector (master = `2994194abe61f680…`, confirmed through the GitHub commits API). Downloaded raw on 2026-11-15 (385,605 bytes, 117 `CustomSetting` entries) and parsed with an XML parser. |
| `NVML-DOC` | docs.nvidia.com/deploy/nvml-api (Device Queries / Device Commands pages), read by the original author through a summarising tool. **Superseded by `NVML-H`**: every NVML statement in this file now cites the raw header instead. |
| `MS-DOC` | learn.microsoft.com Win32 reference (original author). **Superseded by `WINSDK`** for every constant, prototype and struct in section 5.2. |
| `NVML-H` | `nvml.h` from github.com/NVIDIA/go-nvml (main, `gen/nvml/nvml.h`, `NVML_API_VERSION 13`, 880,585 bytes), downloaded raw with curl on 2026-11-15 and grepped verbatim. Line numbers in this file refer to that download. |
| `NVAPI-H` | `nvapi.h` (1,393,432 bytes) and `nvapi_lite_common.h` (43,711 bytes) from github.com/NVIDIA/nvapi (main), downloaded raw on 2026-11-15. |
| `NVAPI-IF` | `nvapi_interface.h` from github.com/NVIDIA/nvapi (main), downloaded raw on 2026-11-15. NVIDIA's own table of **public** function name → 32-bit interface ID. A function that is absent from this table is private/undocumented. |
| `NPI-SRC` | NPI source at commit `2994194a` (`app.manifest`, `Native/NVAPI/NvapiDrsWrapper.cs`, `Common/DrsSettingsServiceBase.cs`, `Common/DrsDecrypterService.cs`, `Common/DrsImportService.cs`, `UI/ViewModels/ProfileExportFileNames.cs`), downloaded raw on 2026-11-15. |
| `WINSDK` | Windows SDK headers installed on the development PC: `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\WinUser.h` and `wingdi.h`, read from disk. |
| `LOCAL` | Read-only inspection of the development PC on 2026-11-15 by the original author (PnP, WMI, services, registry, file ACLs). The verifier re-did only the file-level checks: file listings of `System32` and of the DRS folder, and a byte-level parse of the PE export table of `nvml.dll` (never loaded). The PnP/WMI/service/ACL rows in 0.4 were **not** re-read by the verifier. |

### 0.2 Confidence markers

- No marker means the fact was read directly in the cited local source (`NW`, `LHM`, `PKG`, `WINSDK`) or in a raw-downloaded header (`NVML-H`, `NVAPI-H`, `NVAPI-IF`, `HDR-H`, `NPI-CSN`, `NPI-SRC`).
- **[WEB]** means the fact comes only from a web page that the verifier did **not** re-read raw (an NVIDIA forum thread, an NPI GitHub issue). Treat it as hearsay. Every hex ID and value that used to be marked [WEB] has since been re-checked against the raw files and is no longer marked.
- **UNVERIFIED** means it was not confirmed in any source. Each one says what would confirm it. Everything about how driver 621.24 *behaves* at run time is UNVERIFIED by construction, because nothing was executed.

### 0.3 Web access

Web access worked. GitHub raw files, NVIDIA docs, the NVIDIA driver lookup endpoints, and Microsoft Learn were all reachable.

### 0.4 Facts about the development PC (2026-11-15)

| Item | Value | How it was read |
|---|---|---|
| GPU PnP | `PCI\VEN_10DE&DEV_2782&SUBSYS_40CA1458&REV_A1`, "NVIDIA GeForce RTX 4070 Ti", status OK | `Get-PnpDevice -Class Display` |
| Driver | WMI `DriverVersion` = `32.0.16.2124`. Uninstall entry "NVIDIA Graphics Driver 621.24". | `Win32_VideoController`, Uninstall key |
| `nvapi64.dll` | `C:\Windows\System32\nvapi64.dll`, file version 32.0.16.2124 | file version info |
| `nvml.dll` | `C:\Windows\System32\nvml.dll`, 8.17.16.2124, 439 exports | PE export table, parsed as bytes and never loaded |
| `nvidia-smi.exe` | `C:\Windows\System32\nvidia-smi.exe` (not run) | file listing |
| `C:\Program Files\NVIDIA Corporation\NVSMI` | **does not exist** | `Test-Path` |
| Monitor | `DISPLAY\GSM5C34\...` "Generic Monitor (LG ULTRAGEAR+)". Desktop 2560x1440 at 239 Hz (WMI rounding, probably 239.9x or 240). | PnP, WMI |
| NVIDIA services | `NVDisplay.ContainerLocalSystem` **Stopped, Disabled**. `NvContainerLocalSystem` **Stopped, Disabled**. `FvSvc` Stopped, Disabled. No NVIDIA user processes are running. | `Get-Service`, `Get-Process` |
| NVIDIA App | 11.0.9.585 installed, plus ShadowPlay 11.0.9.0, FrameView SDK, Virtual Audio, NvDLISR | Uninstall keys |
| DRS database | `C:\ProgramData\NVIDIA Corporation\Drs\nvdrsdb0.bin` / `nvdrsdb1.bin` (1,944,844 bytes), `nvdrssel.bin`, `nvdrswr.lk`. Folder ACL: Everyone FullControl, Users ReadAndExecute+Write. | `Get-Acl` |

Consequences:
- The **NVIDIA Display Container service is disabled**, so NVIDIA Control Panel and most NVIDIA App features do not work right now. RigControl must not try to start, enable, or depend on that service. Whether NVAPI DRS and NVML work without it is **UNVERIFIED**; section 11 has a test. The expected answer is that they work, because NVAPI and NVML are user-mode DLLs that talk to the kernel driver. No reference source states this either way (LHM's code never checks for an NVIDIA service, which is weak evidence only).
- The DRS folder is live: `nvAppTimestamps` in it had a modification time inside the verification session (2026-11-15 13:04) although no NVIDIA service is running, so some driver component in ordinary processes writes there. This supports the "never hold a DRS session open" rule in 4.2.
- HVCI has no effect on anything in this spec. NVAPI and NVML are user-mode and load no driver. **Do not** use LHM's `PawnIo/Nvidia.cs` / `Resources/PawnIo/Nvidia.bin` path, which is a kernel-driver route.

---

## 1. Library loading, init and unload, enumeration, identity

### 1.1 How NvAPIWrapper reaches NVAPI

`NW/Native/Helpers/DelegateFactory.cs`:
- `[DllImport("nvapi64", EntryPoint="nvapi_QueryInterface", CallingConvention=Cdecl)]` (L71-73). The 32-bit variant imports `nvapi`.
- `NvAPI_QueryInterface(uint)` (L54-65) **throws `NVIDIANotSupportedException`** with "32bit process running in a 64bit environment can't access NVIDIA API." when the OS is 64-bit and the process is not (L56-60). **RigControl must build as x64.** Set `<PlatformTarget>x64</PlatformTarget>` and do not use AnyCPU with Prefer32Bit.
- `GetDelegate<T>()` (L15-52) resolves each function by 32-bit ID (`NW/Native/Helpers/FunctionId.cs`) and caches it in a dictionary under `lock`. If the driver returns a null pointer for an ID, it **throws `NVIDIANotSupportedException("Function identification number is invalid or not supported.")`** (L51). That is the error you get when a newer driver drops a private function.
- The delegates in `NW/Native/Delegates/*.cs` carry no `[UnmanagedFunctionPointer]` attribute (grep over the whole wrapper finds none), so they use the platform default. LHM marks its NVAPI delegates `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` (`LHM/LibreHardwareMonitorLib/Interop/NvApi.cs` L108 onward). On x64 there is only one calling convention, so the difference is harmless.
- `DelegateFactory` is `internal`. RigControl cannot use it to reach a function the wrapper does not wrap. For those (for example `NvAPI_Disp_GetVRRInfo`, 5.1) declare your own `[DllImport("nvapi64", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr QueryInterface(uint id);` and `Marshal.GetDelegateForFunctionPointer`.
- The wrapper imports `nvapi64` by bare name, and that cannot be redirected. The real DLL is `C:\Windows\System32\nvapi64.dll` (LOCAL file listing). Never ship a file called `nvapi64.dll` beside the exe.

Package targets: `netstandard2.0` and `net45` (PKG `lib/`). A .NET 8 app binds to `netstandard2.0`. Neither target pulls in any dependency. The package DLL (dated 2020-12-09, same day as the `NW` HEAD commit) contains the member names this spec relies on, including `GetClientFanCoolersControl`, `SetClientFanCoolersControl`, `PrivateFanCoolersControlV1` and `RefreshRateInMillihertz` (byte search of `NvAPIWrapper.dll`). The wrapper marshals with reflection and `Marshal.PtrToStructure`, so it is **not** trim- or NativeAOT-safe; `RigControl.App.csproj` already sets `PublishTrimmed=false`, keep it that way.

### 1.2 Init and unload

| API | Signature (namespace `NvAPIWrapper`) | Source |
|---|---|---|
| Initialise | `public static void NVIDIA.Initialize()` | `NW/NVIDIA.cs` L60, calls `GeneralApi.Initialize()` (`NW/Native/GeneralApi.cs` L140). Throws `NVIDIAApiException` when status is not `Ok`. Doc comment: "Initializes the NvAPI library (if not already initialized) but always increments the ref-counter." |
| Unload | `public static void NVIDIA.Unload()` | `NW/NVIDIA.cs` L76, calls `GeneralApi.Unload()` (L185). Doc comment: decrements the ref-counter and unloads at zero. |
| Restart driver | `public static void NVIDIA.RestartDisplayDriver()` | `NW/NVIDIA.cs` L68, marked "PRIVATE". **Never call it.** It resets the display stack. |

Rules: call `Initialize()` exactly once at startup and `Unload()` exactly once at shutdown, on the NVAPI worker thread (section 9). **Never call any other wrapper member before `Initialize()` has returned.** The two sources disagree about what happens if you do:
- Current NVAPI-H (`nvapi.h` L70-72): "It is now mandatory to call NvAPI_Initialize before calling any other NvAPI." The documented status for that case is `NVAPI_API_NOT_INITIALIZED` (-4), which the wrapper would turn into `NVIDIAApiException(Status.ApiNotInitialized)`.
- The wrapper's 2020 doc comment (`GeneralApi.cs` L166-170, copied from the NVAPI of that era) says the opposite: it is "not mandatory", and the first call implicitly increments the ref-counter, so a later `Initialize()` + single `Unload()` would leave the library loaded.

Which one driver 621.24 does is **UNVERIFIED**. Either way the rule above avoids the problem.

Shutdown order: dispose every `DriverSettingsSession` **before** `Unload()`. NVAPI-H (`nvapi.h` L93-117) documents two failure modes for `NvAPI_Unload`: `NVAPI_ERROR` (-1) "if NvAPI_Unload is called after NvAPI_XXX which locks a resource", and `NVAPI_API_IN_USE` (-209) "Atleast an API is still being called". (An earlier draft of this spec attached -209 to the resource-lock case. That was wrong.) Whether an open DRS session counts as a locked resource is not stated: UNVERIFIED. The wrapper turns any non-`Ok` status into an `NVIDIAApiException` (`GeneralApi.Unload`, `GeneralApi.cs` L185-193). Catch and log it at shutdown; do not retry in a loop.

### 1.3 Driver version and branch

| API | Signature | Source |
|---|---|---|
| Driver version | `public static uint NVIDIA.DriverVersion { get; }` | `NW/NVIDIA.cs` L36, calls `GeneralApi.GetDriverAndBranchVersion(out _)` (`GeneralApi.cs` L60), function ID `NvAPI_SYS_GetDriverAndBranchVersion = 0x2926AAAD`. |
| Branch string | `public static string NVIDIA.DriverBranchVersion { get; }` | `NW/NVIDIA.cs` L23 |
| NVAPI interface string | `public static string NVIDIA.InterfaceVersionString { get; }` | `NW/NVIDIA.cs` L44 |

Formatting: the uint packs the version as `major*100 + minor`, so "621.24" arrives as `62124`. Display it as `$"{v/100}.{v%100:00}"`. NVIDIA does not document the encoding (NVAPI-H `nvapi_lite_common.h` L432-449 only says "Contains the driver version after successful return"), and the wrapper does not either. Two independent consumers decode it exactly this way, which is good enough to ship:
- NPI-SRC `Common/DrsSettingsServiceBase.cs` L26-28: `version = (float)(sysDrvVersion / 100f)` on the result of the same `SYS_GetDriverAndBranchVersion` call, then formats it with `"0.00"`.
- `LHM/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs` L1066-1068: `DriverVersion / 100`, `"."`, `(DriverVersion % 100).ToString("00")` (on `NvAPI_GetDisplayDriverVersion`, the older sibling call).

The value on the development PC (expected 62124) is still **UNVERIFIED** until logged once.

Cross-check without NVAPI (optional): `nvmlSystemGetDriverVersion(char* version, unsigned int length)` (NVML-H L4696, buffer `NVML_SYSTEM_DRIVER_VERSION_BUFFER_SIZE = 80`, L4533) returns the version as text. The export is present in the development PC's `nvml.dll`. The exact text on 621.24 is UNVERIFIED.

Fallback without NVAPI or NVML: read `Win32_VideoController.DriverVersion` (`32.0.16.2124` per LOCAL, original author), take the **last digit of the third field** plus the **fourth field** (`6` + `2124` = `62124`), and format it as 621.24. Left-pad the fourth field to 4 digits before concatenating (a value such as `...16.612` must become `0612`); the padding rule is an inference and is UNVERIFIED. The rule itself is a long-standing community convention and is **UNVERIFIED** against NVIDIA documentation.

The branch string format (for example `r620_xx`) is **UNVERIFIED**. Display it raw. The driver-lookup JSON in 6.2 reports `"Release": "620"` for 621.24, which hints at the branch number but proves nothing about the string.

### 1.4 Physical GPUs and identity

Namespace `NvAPIWrapper.GPU`, class `PhysicalGPU` (`NW/GPU/PhysicalGPU.cs`).

| Need | Member | Line | Notes |
|---|---|---|---|
| Enumerate | `public static PhysicalGPU[] GetPhysicalGPUs()` | L300 | wraps `NvAPI_EnumPhysicalGPUs 0xE5AC921F` |
| Name | `public string FullName { get; }` | L167 | `NvAPI_GPU_GetFullName 0xCEEE8E9F`. Expect "NVIDIA GeForce RTX 4070 Ti" (UNVERIFIED exact text). |
| PCI IDs | `BusInformation.PCIIdentifiers` returns `PCIIdentifiers { uint DeviceId; ushort VendorId; uint SubSystemId; uint RevisionId; ushort ExternalDeviceId }` | L120 / `NW/GPU/PCIIdentifiers.cs` L32-52 | Use it to match the development PC's card (VEN 10DE, DEV 2782, SUBSYS 40CA1458). The wrapper's constructor (`PCIIdentifiers.cs` L11-27) derives `VendorId = low 16 bits of DeviceId` and, when the driver reports no external id, `ExternalDeviceId = high 16 bits of DeviceId`. So the expected raw values are `DeviceId = 0x278210DE`, `SubSystemId = 0x40CA1458`, `RevisionId = 0xA1`; `ToString()` (L109-112) formats `PCI\VEN_{VendorId:X}&DEV_{ExternalDeviceId:X}&SUBSYS_{SubSystemId:X}&REV_{RevisionId:X}`. Match on `VendorId == 0x10DE && ExternalDeviceId == 0x2782 && SubSystemId == 0x40CA1458`. The packing is the wrapper's assumption; the actual values on this driver are UNVERIFIED until logged once. |
| Bus / PCIe lanes | `BusInformation.BusId`, `.BusSlot`, `.CurrentPCIeLanes`, `.BusType` | `NW/GPU/GPUBusInformation.cs` L38-62 | |
| VRAM | `MemoryInformation.DedicatedVideoMemoryInkB`, `.AvailableDedicatedVideoMemoryInkB`, `.CurrentAvailableDedicatedVideoMemoryInkB`, `.SharedSystemMemoryInkB` (all `uint`, kB) | `NW/GPU/GPUMemoryInformation.cs` L118-145 | wraps `NvAPI_GPU_GetMemoryInfo 0x07F9B368` (ID confirmed in NVAPI-IF). **NVAPI-H marks this function deprecated "in release 520", replaced by `NvAPI_GPU_GetMemoryInfoEx 0xC0599498`** (`nvapi_lite_common.h` L519-538, L600), which the wrapper does not wrap (LHM binds both, `NvApi.cs` L88-89; note LHM's "GetMemoryInfo" ID `0x774AA982` is a different, non-public function). Whether the deprecated call still answers on 621.24 is UNVERIFIED, and each property makes its own driver call. **Use NVML `nvmlDeviceGetMemoryInfo` (bytes) for both total and used VRAM**; keep this row as a fallback only (VRAM used = Dedicated minus CurrentAvailable). |
| Bus id for the contract's `gpus[].busId` | `BusInformation.BusId` (int) | `NW/GPU/GPUBusInformation.cs` L38 | LHM builds the NVML PCI bus id from the same value as `$"0000:{busId:X2}:00.0"` (`NvidiaGpu.cs` L88). |
| VRAM type/vendor | `MemoryInformation.RAMType`, `.RAMMaker`, `.RAMBusWidth` | L87-103 | private APIs, may throw |
| BIOS | `Bios` returns `VideoBIOS` | L77 | |
| Arch | `ArchitectInformation.ShortName`, `.NumberOfCores`, ... | `NW/GPU/GPUArchitectInformation.cs` | |

`WMI Win32_VideoController.AdapterRAM` reports 4293918720, a 32-bit overflow. **Never use it for VRAM.**

---

## 2. Live statistics

Strategy: use **NVML for power (watts), utilisation, memory, fan percent, fan RPM, and GPU temperature**, because those calls are documented and versioned. Use **NvAPIWrapper for clocks, the fan RPM fallback, and anything NVML does not expose**. Poll from one background thread at 1 Hz.

Public vs private (checked against NVAPI-IF): of the NVAPI functions in 2.1 only `GetThermalSettings 0xE3640A56`, `GetDynamicPstatesInfoEx 0x60DED2ED`, `GetAllClockFrequencies 0xDCB616C3` and `GetTachReading 0x5F608315` are public. `GetUsages`, `GetCurrentFanSpeedLevel`, `ClientPowerTopologyGetStatus`, `ClientPowerPolicies*`, every cooler function, and (section 5) DVC and hue are **absent from NVIDIA's public table**, so NVIDIA may remove them in any driver.

**Never call these wrapper members** (they write to hardware or to the display stack and are outside this spec): `PhysicalGPU.WriteI2C`, `PhysicalGPU.ReadI2C`, `PhysicalGPU.WriteEDIDData` (`PhysicalGPU.cs` L445-490), `NVIDIA.RestartDisplayDriver`, `GPUUsageInformation.EnableDynamicPerformanceStates` (L94), and the raw `GPUApi` setters `ClientPowerPoliciesSetStatus`, `SetThermalPoliciesStatus`, `SetCoolerPolicyTable`, `RestoreCoolerPolicyTable`, `EnableDynamicPStates`, `EnableOverclockedPStates`, `SetClockBoostLock`, `SetClockBoostTable`, `SetCoreVoltageBoostPercent`, `SetPerformanceStates20`, `SetEDID`, `SetIllumination`, `SetECCConfiguration`, `ResetECCErrorInfo` (the complete list of state-changing `public static` methods in `NW/Native/GPUApi*.cs`, apart from the three cooler setters discussed in section 3).

### 2.1 NvAPIWrapper live data

| Metric | Member | Unit | Source |
|---|---|---|---|
| Temperature | `ThermalInformation.ThermalSensors` returns `IEnumerable<GPUThermalSensor>`. Each sensor has `CurrentTemperature`, `Target` (`ThermalSettingsTarget`), `Controller`, `DefaultMinimumTemperature`, `DefaultMaximumTemperature`. Pick `Target == ThermalSettingsTarget.GPU` (1). | °C | `NW/GPU/GPUThermalInformation.cs` L33, `GPUThermalSensor.cs` L24-39. `GPUApi.GetThermalSettings` (`NW/Native/GPUApi.Thermal.cs` L204) tries each struct version and throws `NVIDIANotSupportedException` if none is accepted. |
| Hot-spot / memory junction | **not exposed** by the wrapper. LHM uses private `NvAPI_GPU_GetThermalSensors 0x65FE3AAD` (`LHM/LibreHardwareMonitorLib/Interop/NvApi.cs` L100). | | Out of scope. UNVERIFIED for this card. |
| Usage | `UsageInformation.GPU?.Percentage`, `.FrameBuffer`, `.VideoEngine`, `.BusInterface` (`GPUUsageDomainStatus { UtilizationDomain Domain; int Percentage }`) | % | `NW/GPU/GPUUsageInformation.cs` L21-89. Tries `GetDynamicPerformanceStatesInfoEx` (0x60DED2ED) first, then falls back to private `GetUsages` (0x189A1FDF) (L58-79). Each property can return **null**. |
| Clocks | `CurrentClockFrequencies` returns `IClockFrequencies` with `.GraphicsClock`, `.MemoryClock`, `.VideoDecodingClock`, `.ProcessorClock` (each a `ClockDomainInfo { bool IsPresent; uint Frequency }`) | **kHz** (NVAPI-H `nvapi.h` L5492: "Clock frequency (kHz)"; the wrapper's `ToString` also prints kHz). Divide by 1000 for the contract's MHz. | `PhysicalGPU.cs` L138, `NW/Native/Interfaces/GPU/IClockFrequencies.cs`, `NW/Native/GPU/Structures/ClockDomainInfo.cs`. Also `BaseClockFrequencies` (L69) and `BoostClockFrequencies` (L112). Check `IsPresent`. |
| Fan RPM | `CoolerInformation.CurrentFanSpeedInRPM` (int) | RPM | `NW/GPU/GPUCoolerInformation.cs` L126. Tries `NvAPI_GPU_GetTachReading 0x5F608315` first. Any exception from that is caught (L134), and the catch block then evaluates `Coolers.FirstOrDefault(c => c.Target == CoolerTarget.All)?.CurrentFanSpeedInRPM ?? 0` (L136-137) **outside any try**. So the getter returns 0 only when the fallback finds no cooler; if enumerating `Coolers` throws (for example `NotSupported` at L119, or a rethrown status from F4), **the exception propagates**. (`Coolers` is a C# iterator: the property getter itself never throws, the exception surfaces when the sequence is enumerated.) On the ClientFanCoolers path every `GPUCooler` has `Target = All` (`GPUCooler.cs` L52), so the fallback reports the **first** cooler only. |
| Fan % | `CoolerInformation.CurrentFanSpeedLevel` (int) | % | L145. Uses private `GetCurrentFanSpeedLevel 0xBD71F0C9`, then falls back to `Coolers` in the same way (L153-156): 0 when no cooler matches, otherwise the first cooler's level, and it **can throw** when `Coolers` throws. |
| Power | `PowerTopologyInformation.PowerTopologyEntries` returns `GPUPowerTopologyStatus { PowerTopologyDomain Domain; uint PowerUsageInPCM; float PowerUsageInPercent }` | **per cent mille (PCM), not watts** (`PowerUsageInPercent = PCM/1000f`, L31-34). What the 100 % reference is (TDP, default limit, ...) is not stated in the wrapper: UNVERIFIED. | `NW/GPU/GPUPowerTopologyStatus.cs` L21-35. Private `ClientPowerTopologyGetStatus 0xEDCF624E`. Use NVML for watts. |
| Power limit (read only) | `PerformanceControl.PowerLimitInformation` (`GPUPowerLimitInfo` with Min/Default/MaximumPowerInPCM) and `.PowerLimitPolicies` (`PowerTargetInPCM`) | PCM | `NW/GPU/GPUPerformanceControl.cs` L77-100. **No setter exists**: L93 reads `// TODO: GPUApi.ClientPowerPoliciesSetStatus();`. A raw `GPUApi.ClientPowerPoliciesSetStatus` exists (`NW/Native/GPUApi.PowerPolicies.cs` L70). The C# method is `public static`, but it wraps a **private, undocumented NVAPI** function. **Do not use it. Use NVML instead.** |
| Throttle reason | `PerformanceControl.CurrentPerformanceDecreaseReason`, `.CurrentActiveLimit` | flags | L21-29 |

### 2.2 NVML (nvml.dll) P/Invoke

Location: `C:\Windows\System32\nvml.dll` (LOCAL file listing, 1,555,688 bytes). LHM loads `nvml.dll` with the default search order first, then `%ProgramW6432%\NVIDIA Corporation\NVSMI\nvml.dll` (`LHM/.../Interop/NvidiaML.cs` L176-186). On the development PC the second path does not exist. **Load by full path `Environment.SystemDirectory + "\\nvml.dll"`** with `NativeLibrary.Load` to avoid DLL search-order hijack, then bind every function with `NativeLibrary.GetExport` + `Marshal.GetDelegateForFunctionPointer` (preferred: a missing export then becomes a per-function "not available" instead of an `EntryPointNotFoundException` at first use). The `[DllImport("nvml.dll")]` lines below show the signatures; if you keep them as `DllImport`, register a `NativeLibrary.SetDllImportResolver` that returns the pre-loaded handle. LHM resolves `nvmlInit_v2` and `nvmlDeviceGetHandleByIndex_v2` first and falls back to the unversioned names (L205-217, L226-236). That fallback is unnecessary here: all `_v2` names are exported by the development PC's DLL.

All of the export names below **are present** in the development PC's `nvml.dll` (PE export table parsed from the file bytes by the verifier on 2026-11-15, DLL never loaded: machine `0x8664`, optional-header magic `0x20B` = PE32+, **439 named exports**; all 32 names used in this file were looked up individually). Every signature below was checked verbatim against NVML-H. On Windows `DECLDIR` is only `__declspec(dllimport)` (NVML-H L83-95), so the functions use the platform default; on x64 that is the single x64 convention, and `[DllImport]`'s default is fine.

LHM itself binds only seven NVML functions on Windows (init, shutdown, handle by index, handle by PCI bus id, power usage, PCIe throughput, PCI info: `NvidiaML.cs` L205-262). It is **not** evidence for any other NVML function in this section.

```
// All functions return nvmlReturn_t (int). 0 = NVML_SUCCESS.
// nvmlDevice_t is an opaque pointer -> IntPtr.
[DllImport("nvml.dll")] static extern int nvmlInit_v2();                                   // NVML-H L4429; LHM NvidiaML.cs L205 (Windows GetProcAddress), L416-417 (Linux DllImport)
[DllImport("nvml.dll")] static extern int nvmlShutdown();                                  // NVML-H L4479; LHM L220, L422
[DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);            // NVML-H L5075
[DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device); // NVML-H L5143 (unsigned int); LHM L226/L445 declares index as int
[DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByPciBusId_v2([MarshalAs(UnmanagedType.LPStr)] string busId, out IntPtr device); // NVML-H L5260; LHM L250 (GetProcAddress), L428 (Linux DllImport), L447 (delegate).
        // Accepted formats, all hex (NVML-H L5241-5244): "domain:bus:device.function", "domain:bus:device", "bus:device.function". LHM passes $"0000:{busId:X2}:00.0" (NvidiaGpu.cs L88).
[DllImport("nvml.dll")] static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length); // NVML-H L5286; buffer 96 bytes = NVML_DEVICE_NAME_V2_BUFFER_SIZE (NVML-H L4548; the old NVML_DEVICE_NAME_BUFFER_SIZE is 64, L4543)
[DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);  // NVML-H L7060 "in milliwatts", "For Fermi or newer"; LHM L238/L449 (out int)
[DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);           // NVML-H L6993
[DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMw, out uint maxMw); // NVML-H L7014
[DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);    // NVML-H L7033
[DllImport("nvml.dll")] static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);            // NVML-H L7139
[DllImport("nvml.dll")] static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint milliwatts);              // NVML-H L9838, ADMIN (Device Commands group)
[DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);         // NVML-H L7695
[DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory mem);                     // NVML-H L7261, bytes
[DllImport("nvml.dll")] static extern int nvmlDeviceGetNumFans(IntPtr device, out uint numFans);                          // NVML-H L6455
[DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint percent);            // NVML-H L6344. "intended" speed, percent of max; "may exceed 100% in certain cases" (L6322-6330) -> clamp to 0..100 for the contract
[DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo info);             // NVML-H L6368. RPM, also the "intended" speed. Set info.Version and info.Fan before the call.
[DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType /*0 = NVML_TEMPERATURE_GPU*/, out uint celsius); // NVML-H L6460: marked DEPRECATED(13.0), see below
[DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperatureV(IntPtr device, ref NvmlTemperature temp);             // NVML-H L6517, the documented replacement. Set temp.Version and temp.SensorType before the call.
[DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);             // NVML-H L6131, MHz
[DllImport("nvml.dll")] static extern IntPtr nvmlErrorString(int result);   // NVML-H L4500: `const char* nvmlErrorString(nvmlReturn_t)`; marshal with Marshal.PtrToStringAnsi, never free.

[StructLayout(LayoutKind.Sequential)] struct NvmlUtilization { public uint Gpu; public uint Memory; }          // percent; NVML-H L245-249
[StructLayout(LayoutKind.Sequential)] struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; } // bytes, v1 struct; NVML-H L255-261 (v1 `used` = reserved + allocated)
[StructLayout(LayoutKind.Sequential)] struct NvmlFanSpeedInfo { public uint Version; public uint Fan; public uint Speed; }   // NVML-H L1413-1418; 12 bytes; Version = 0x0100000C
[StructLayout(LayoutKind.Sequential)] struct NvmlTemperature { public uint Version; public int SensorType; public int Temperature; } // NVML-H L6489-6494; 12 bytes; Version = 0x0100000C
```

Constants (all confirmed verbatim in NVML-H):
- `NvmlUtilization { gpu, memory }` and `NvmlMemory` v1 `{ total, free, used }` field orders: NVML-H L245-261.
- **NVML struct version words use a different formula from NVAPI**: `NVML_STRUCT_VERSION(data, ver) = sizeof(nvml<data>_v<ver>_t) | (ver << 24)` (NVML-H L141-142). NVAPI uses `sizeof | (ver << 16)` (NVAPI-H `nvapi_lite_common.h` L248). Do not mix them up. Computed by the verifier: `nvmlFanSpeedInfo_v1` = 12 | (1 << 24) = **`0x0100000C`**; `nvmlTemperature_v1` = 12 | (1 << 24) = **`0x0100000C`**; `nvmlMemory_v2` = 40 | (2 << 24) = **`0x02000028`**. A wrong version word returns `ArgumentVersionMismatch` (25).
- `NVML_TEMPERATURE_GPU = 0` (L1179), `NVML_TEMPERATURE_GPU_MAX = 1` "Temperature from the hottest part of the GPU die" (L1181; whether 621.24 on this card supports it is UNVERIFIED). Clock types `NVML_CLOCK_GRAPHICS = 0, NVML_CLOCK_SM = 1, NVML_CLOCK_MEM = 2, NVML_CLOCK_VIDEO = 3` (L1328-1331).
- `nvmlDeviceGetTemperature` is marked `DEPRECATED(13.0)` in NVML-H (L6460, "Use nvmlDeviceGetTemperatureV instead"). Both exports are present in the development PC's `nvml.dll`. Order: call `nvmlDeviceGetTemperatureV` with `{ Version = 0x0100000C, SensorType = 0 }`; on `FunctionNotFound` (13), `NotSupported` (3), `ArgumentVersionMismatch` (25) or `Deprecated` (26) fall back to `nvmlDeviceGetTemperature`, then to NvAPIWrapper thermal sensors. `Temperature` is a signed int in °C.
- `nvmlMemory_v2_t` layout (L268-275): `uint version; ulong total; ulong reserved; ulong free; ulong used` (4 bytes of padding after `version` on x64, 40 bytes in total). Still prefer v1, which needs no version word.
- `nvmlDeviceGetPowerUsage` (L7036-7060): milliwatts, "power averaged over 1 sec interval" on Ampere or newer; "only available if power management mode is supported". Divide by 1000 for the contract's watts.
- NVML is documented thread-safe (NVML-H L44: "NVML is thread-safe so it is safe to make simultaneous NVML calls from multiple threads"), and `nvmlInit_v2` is reference counted (L4421-4424); one `nvmlShutdown` is needed per init, and extra shutdown calls report no error (L4470-4472).

NVML return codes (NVML-H L1512-1546, complete list): 0 Success, 1 Uninitialized, 2 InvalidArgument, 3 NotSupported, **4 NoPermission**, 5 AlreadyInitialized (deprecated), 6 NotFound, 7 InsufficientSize, 8 InsufficientPower, 9 DriverNotLoaded, 10 Timeout, 11 IrqIssue, 12 LibraryNotFound, 13 FunctionNotFound, 14 CorruptedInfoRom, 15 GpuIsLost, 16 ResetRequired, 17 OperatingSystem, 18 LibRmVersionMismatch, 19 InUse, 20 Memory, 21 NoData, 22 VgpuEccNotSupported, 23 InsufficientResources, 24 FreqNotSupported, 25 ArgumentVersionMismatch, 26 Deprecated, 27 NotReady, 28 GpuNotFound, 29 InvalidState, 30 ResetTypeNotSupported, 999 Unknown. LHM's enum (`NvidiaML.cs` L36-137) is a subset: 0-4, 6-19 and 999 only. Treat any unlisted code as "unknown error" and log the number.

### 2.3 Which stats need admin

| Call | Admin? | Source |
|---|---|---|
| All NVML `nvmlDeviceGet*` queries above | **Expected no, UNVERIFIED.** The query functions sit in the "Device Queries" group (NVML-H L5046-8805), and none of the query functions listed in 2.2 has an admin note in its own doc block (the only "Requires root/admin" notes inside that group belong to `Set*` functions and to `nvmlDeviceGetSramEccErrorStatus`). But `nvmlInit_v2` and the handle functions can themselves return `NoPermission` (L4419, L5255). An earlier draft cited LHM as proof that these work unelevated. **That was wrong: the LHM application requires administrator** (`LHM/LibreHardwareMonitor.Windows.Forms/Resources/app.manifest` L10, `requireAdministrator`), and it binds only seven NVML functions anyway. Bring-up step 2 settles it. | NVML-H |
| `nvmlDeviceSetPowerManagementLimit` | **Yes**: "Requires root/admin permissions." The limit is in mW and "is not persistent across reboots or driver unloads". Supported "For Kepler or newer fully supported devices". Range check: `nvmlDeviceGetPowerManagementLimitConstraints`; out of range returns `InvalidArgument` (2). | NVML-H L9813-9838 (verbatim) |
| Every function in the NVML "Device Commands" group (NVML-H L9365-9960, which contains the power-limit L9838, fan-speed L9921, default-fan-speed L9768 and fan-policy L9790 setters) | **Yes**: "Each of these requires root/admin access. Non-admin users will see an NVML_ERROR_NO_PERMISSION error code when invoking any of these methods." (4) | NVML-H L9365-9368 (verbatim) |
| NvAPIWrapper sensor reads | **Expected no, UNVERIFIED.** Not documented by NVIDIA, and LHM is not evidence (it runs elevated). `NVAPI_INVALID_USER_PRIVILEGE` (-137) is the status to expect if a call does need admin. | |

Power-limit UI: read `Constraints` (min, max) and `DefaultLimit`, show the slider in watts (mW / 1000), and write with `Set` only when the app is elevated (section 8). **Re-apply at every RigControl start**, because the limit does not persist. Whether GeForce cards on Windows accept `Set` at all is **UNVERIFIED**; the header lists `NotSupported` (3) "if the device does not support this feature". Test it once in bring-up. Clamp the requested value to `[min, max]` from `Constraints` before calling `Set`, and never call `Set` when `Constraints` itself failed.

---

## 3. GPU fan control

### 3.1 Two NVAPI cooler families inside NvAPIWrapper

| Family | Function IDs (`NW/Native/Helpers/FunctionId.cs`) | Wrapper (`NW/Native/GPUApi.Thermal.cs`) | Era |
|---|---|---|---|
| Legacy "cooler settings/levels" | `GetCoolerSettings 0xDA141340`, `SetCoolerLevels 0x891FA0AE`, `RestoreCoolerSettings 0x8F6ED0FB`, policy table `0x518A32C` / `0x987947CD` / `0xD8C4FE63` | `GetCoolerSettings` L58, `SetCoolerLevels` L291, `RestoreCoolerSettings` L266 | Pre-Turing. The doc comment in `GPUCoolerInformation` mentions "Pascal Only Policy Table" (L21-23). |
| "ClientFanCoolers" | `GetInfo 0xFB85B01E`, `GetStatus 0x35AED5E8`, `GetControl 0x814B209F`, `SetControl 0xA58971A5` (`FunctionId.cs` L695-698) | `GetClientFanCoolersInfo` L372, `GetClientFanCoolersStatus` L392, `GetClientFanCoolersControl` L413, `SetClientFanCoolersControl` L433 | Turing and newer (UNVERIFIED cut-off). LHM uses the same IDs for **GetStatus** (`LHM/.../Interop/NvApi.cs` L95), **GetControl** (L97) and **SetControl** (L98). LHM does **not** bind `GetInfo 0xFB85B01E` at all. |

All of these are **private, undocumented NVAPI** ("[PRIVATE]" in the doc comments, and none of the ten IDs above appears in NVAPI-IF). NVIDIA can change or remove them in any driver.

Struct layouts (`NW/Native/GPU/Structures/PrivateFanCoolers*V1.cs`) match LHM's independent definitions (`LHM/.../Interop/NvApi.cs` L283-305, L507-532):
- Control V1: `Version, uint unknown, uint count, uint[8] reserved, Entry[32]`. Each entry: `uint CoolerId, uint Level, FanCoolersControlMode Mode (Auto=0, Manual=1), uint[8] reserved`.
- Status V1: `Version, uint count, uint[8] reserved, Entry[32]`. Each entry: `CoolerId, CurrentRPM, CurrentMinimumLevel, CurrentMaximumLevel, CurrentLevel, uint[8] reserved`. (LHM declares the 32 reserved bytes as four `ulong`s; with `Pack = 8` they start at offset 8, so the layout is byte-identical.)
- Info V1 (wrapper only, `PrivateFanCoolersInfoV1.cs` L9-51): `Version, uint unknown, uint count, uint[8] reserved, Entry[32]`. Each entry: `uint CoolerId, uint unknown3, uint unknown4, uint MaximumRPM, uint[8] reserved`. Two of the four entry fields are unidentified, which is one more reason to expect F4.
- Both projects use `StructLayout(Sequential, Pack = 8)` with only 4-byte fields, so there is no padding (LHM's status struct has 8-byte fields, but they start at offset 8 and the total, 1704, is a multiple of 8). Version words, recomputed by the second verifier from the rule `Marshal.SizeOf(T) | (version << 16)` (`NW/Native/General/Structures/StructureVersion.cs` L23; LHM `MAKE_NVAPI_VERSION`, `NvApi.cs` L277-280; NVAPI-H `nvapi_lite_common.h` L248):

  | Struct | Header bytes | Entry bytes | Total = header + 32 × entry | Version word (V1) |
  |---|---|---|---|---|
  | Control | 4 + 4 + 4 + 32 = 44 | 4 + 4 + 4 + 32 = 44 | 44 + 1408 = **1452** = `0x5AC` | **`0x000105AC`** |
  | Status | 4 + 4 + 32 = 40 | 5 × 4 + 32 = 52 | 40 + 1664 = **1704** = `0x6A8` | **`0x000106A8`** |
  | Info | 4 + 4 + 4 + 32 = 44 | 4 × 4 + 32 = 48 | 44 + 1536 = **1580** = `0x62C` | **`0x0001062C`** |

  These are derived numbers, not literals in either source. A unit test should assert `Marshal.SizeOf` of the three wrapper structs equals 1452 / 1704 / 1580 (this needs no driver).
- Byte offsets inside a control struct, for a fake-driver unit test: `Version` @0, `UnknownUInt` @4, `Count` @8, reserved @12..43, entry *i* @ 44 + 44·*i* with `CoolerId` @+0, `Level` @+4, `ControlMode` @+8, reserved @+12..+43. Example: one cooler, id 1, Manual at 60 % → bytes 0-3 `AC 05 01 00`, 8-11 `01 00 00 00`, 44-47 `01 00 00 00`, 48-51 `3C 00 00 00`, 52-55 `01 00 00 00`, everything else zero (bytes 4-7 must be copied from the struct that `GetControl` returned).
- `PrivateFanCoolersControlV1(entries, unknownUInt)` throws `ArgumentException` for a null/empty array or more than 32 entries, **sorts the entries by `CoolerId`**, and copies only `entries.Length` of them (`PrivateFanCoolersControlV1.cs` L36-56). `FanCoolersControlEntries` / `FanCoolersStatusEntries` return only the first `count` entries (`...ControlV1.cs` L26-29, `...StatusV1.cs` L24-27).

Agreement between two independent projects raises confidence. It is still **UNVERIFIED for driver 621.24**.

### 3.2 What the high-level wrapper does (`NW/GPU/GPUCoolerInformation.cs`)

- `Coolers` (L29-121):
  1. It calls legacy `GetCoolerSettings`. If that throws `NVIDIAApiException` with `Status.NotSupported`, it falls through. **Any other status is rethrown** (L39-45).
  2. It then calls `GetClientFanCoolersStatus`, `...Info`, and `...Control`. Again only `NotSupported` falls through (L87-99).
  3. If neither path works, it throws `NVIDIAApiException(Status.NotSupported)` (L119).
- `SetCoolerSettings(int coolerId, CoolerPolicy policy, int newLevel)` (L229), `SetCoolerSettings(int coolerId, int newLevel)` (L335, sets Manual), and `SetCoolerSettings(int coolerId, CoolerPolicy policy)` (L283): each tries legacy `SetCoolerLevels` first. On `NotSupported` it reads `GetClientFanCoolersControl`, rewrites the matching entry, and calls `SetClientFanCoolersControl`. The rewrite **keeps the original `UnknownUInt`** (L272).
- `RestoreCoolerSettingsToDefault()` / `(params int[] coolerIds)` (L174 / L183): legacy `RestoreCoolerSettings` first. On `NotSupported`, every listed cooler is set to `FanCoolersControlMode.Auto` through `SetClientFanCoolersControl` (L206-219).
- `CoolerPolicy` (`NW/Native/GPU/CoolerPolicy.cs`): `None=0, Manual=1, Performance=2, TemperatureDiscrete=4, TemperatureContinuous=8, Silent=16`. LHM's equivalent has an extra `Auto = 32` (`LHM/.../Interop/NvApi.cs` L189-197), which NvAPIWrapper lacks.

**Defects found in 0.8.1.101 (by reading the source):**

| # | Defect | Where | Effect |
|---|---|---|---|
| F1 | `SetCoolerSettings(id, CoolerPolicy.Manual)` without a level, on the ClientFanCoolers path, calls `new FanCoolersControlEntry(id, Manual)`, which **throws `ArgumentException`** "Manual control mode is not valid when no level value is provided." | `GPUCoolerInformation.cs` L319-321, `PrivateFanCoolersControlV1.cs` L77-87 | Never call the 2-argument policy overload with Manual. |
| F2 | On the ClientFanCoolers path, `GPUCooler.CoolerId` comes from the driver's `statusEntry.CoolerId` (L40 of `GPUCooler.cs`), and `SetCoolerSettings` validates the id against that same value. The ids are therefore **driver cooler ids, not 0-based indexes**. That they are 1-based on RTX 40 is **UNVERIFIED**. LHM uses `CoolerId` as the sensor **index** (`NvidiaGpu.cs` L272, L301) but names the sensors "GPU Fan 1..n" from the loop position (L270, L296). | `GPUCooler.cs` L30-55, `GPUCoolerInformation.cs` L231 | Always take the ids from `Coolers`. Never assume 0..n-1. |
| F3 | Default min/max on the ClientFanCoolers path are copied from the *current* min/max (`GPUCooler.cs` L42-45), and `DefaultPolicy` is always `None`. | | Do not show "default" values in the UI as real defaults. |
| F4 | `Coolers` requires **Status, Info AND Control** to all succeed. If the private `Info` struct V1 no longer matches the driver, the call returns `IncompatibleStructureVersion` (-9), which is **rethrown** instead of falling back, so the whole property throws. LHM never calls `Info`. | `GPUCoolerInformation.cs` L87-99 | This is a likely failure mode on new drivers. **UNVERIFIED** on 621.24. |
| F5 | `CurrentFanSpeedInRPM` and `CurrentFanSpeedLevel` catch every exception from their first call, then run a `Coolers` fallback that is **not** guarded. They return 0 when no cooler matches and **rethrow whatever `Coolers` throws**. On the ClientFanCoolers path the fallback reports only the first cooler. | L130-139, L149-157 | 0 RPM is ambiguous (a 4070 Ti with zero-RPM idle legitimately reports 0), and the getters are **not** exception-safe. Wrap them, or read `GPUApi.GetClientFanCoolersStatus` directly. |
| F6 | Each set call is a read-modify-write of the whole control table with no locking. | | Serialise all fan writes on one thread. |
| F7 | The legacy `SetCoolerLevels` is tried **first** on every set. On RTX 40 it should return `NotSupported`. If it instead returns another error (for example `InvalidArgument` or `IncompatibleStructureVersion`), the wrapper **rethrows and never reaches ClientFanCoolers**. Worse, if a driver *accepted* the legacy call, the wrapper would return without touching ClientFanCoolers, and RigControl would have issued a private write it never intended. | L236-257 | **UNVERIFIED** which status 621.24 returns. Never use the wrapper's high-level setters; call `GPUApi.Get/SetClientFanCoolersControl` directly (section 3.4). |
| F8 | On the ClientFanCoolers path `Coolers` pairs the Info, Status and Control tables **by array position** (L103-112), and the `GPUCooler` constructor throws `ArgumentException("Passed arguments are meant to be for different coolers.")` when the three entries at one position carry different cooler ids (`GPUCooler.cs` L35-38). | `GPUCoolerInformation.cs` L101-114 | A driver that orders the three tables differently makes `Coolers` throw `ArgumentException`, which is neither `NVIDIAApiException` nor `NVIDIANotSupportedException`. Catch `Exception` around any enumeration of `Coolers`. |
| F9 | Every high-level setter and both restore overloads start by enumerating `Coolers` (L176, L185, L231, L285), so F4 and F8 also break *restoring automatic control* through the wrapper. | | The restore path must not depend on `Coolers`. Use section 3.4 step 3. |

LHM's approach (maintained, 2026): for fan *sensors* it probes `ClientFanCoolersGetStatus` **first** and falls back to `GetTachReading`; for fan *controls* it probes `ClientFanCoolersGetControl` first and uses the legacy `GetCoolerSettings` only when that fails (`LHM/.../Hardware/Gpu/NvidiaGpu.cs` L261-337). Its *set* path still tries `SetCoolerLevels` first and then ClientFanCoolers (L1600-1625). Its restore path does the same with `Policy=Auto(32)` first, then Mode=Auto, Level=0 (L1627-1649). It tries to restore default fan control on `Close()` (L1651-1668), **but it passes the loop position `i` (0-based) to `RestoreDefaultFanBehavior` (L1663), while the ClientFanCoolers branch matches on `CoolerId`** (L1639). When cooler ids are not 0..n-1, LHM's close-time restore misses coolers. Do not copy that pattern: always restore by the ids read from `GetClientFanCoolersStatus`/`Control`. LHM also ignores the return status of every `SetControl` call (L1624, L1648).

### 3.3 Which path works on RTX 4070 Ti + 621.24?

**UNVERIFIED.** Expected: legacy `GetCoolerSettings` returns `NotSupported` and the ClientFanCoolers family works, based on LHM's ordering and the wrapper's TODO. To confirm, run the read-only probe in section 11 (step 3). Gigabyte 4070 Ti boards usually have 2 or 3 fans on 1 or 2 controllers; the count is **UNVERIFIED**.

NVML alternative (documented, versioned; every quote below is verbatim from NVML-H):
- `nvmlDeviceSetFanSpeed_v2(nvmlDevice_t device, unsigned int fan, unsigned int speed)` (L9899-9921): `speed` is "The target speed of the fan [0-100] in % of max speed"; `fan` is "The index of the fan, starting at zero". "WARNING: This function changes the fan control policy to manual. It means that YOU have to monitor the temperature and adjust the fan speed accordingly. If you set the fan speed too low you can burn your GPU! Use nvmlDeviceSetDefaultFanSpeed_v2 to restore default control policy." Supported "For all cuda-capable discrete products with fans that are Maxwell or Newer." Out-of-range speed or a bad fan index returns `InvalidArgument` (2).
- `nvmlDeviceSetDefaultFanSpeed_v2(nvmlDevice_t device, unsigned int fan)` (L9753-9768): "Sets the speed of the fan control policy to default." [WEB, not re-read by the verifier] An NVIDIA forum thread reports that it **did not resume the automatic curve** after a manual set ("nvmldevicesetdefaultfanspeed-v2-does-not-resume-fan-speed-algorithm"). Whether that applies to 621.24 is **UNVERIFIED**.
- `nvmlDeviceGetMinMaxFanSpeed(nvmlDevice_t device, unsigned int* minSpeed, unsigned int* maxSpeed)` (L6398-6415) "Retrieves the min and max fan speed that user can set for the GPU fan."
- `nvmlDeviceGetFanControlPolicy_v2(nvmlDevice_t device, unsigned int fan, nvmlFanControlPolicy_t* policy)` (L6417-6437) and `nvmlDeviceSetFanControlPolicy(nvmlDevice_t device, unsigned int fan, nvmlFanControlPolicy_t policy)` (L9770-9791, "Requires privileged user."). `nvmlFanControlPolicy_t` is `unsigned int`; values `NVML_FAN_POLICY_TEMPERATURE_CONTINOUS_SW = 0` (automatic), `NVML_FAN_POLICY_MANUAL = 1` (L1804-1807; the "CONTINOUS" spelling is NVIDIA's).
- `nvmlDeviceGetTargetFanSpeed(nvmlDevice_t device, unsigned int fan, unsigned int* targetSpeed)` (L6370-6396) returns the speed the driver is aiming for; `nvmlDeviceGetFanSpeed_v2` and `nvmlDeviceGetFanSpeedRPM` are documented as the "intended" speed too, **not a tachometer reading**: "If the fan is physically blocked and unable to spin, the output will not match the actual fan speed."
- The three setters are inside the "Device Commands" group (L9365-9960), so they **require admin** and return `NoPermission` (4) otherwise. The getters are in the Device Queries group.
- All exports are present in the development PC's `nvml.dll` (verifier's PE parse). Whether they work on a GeForce card under Windows is **UNVERIFIED**; NVML's set functions have historically been datacenter-focused (general knowledge, no source).

### 3.4 Recommended implementation, if fan control is shipped at all

1. Read-only first. Fan count from `nvmlDeviceGetNumFans`, percent from `nvmlDeviceGetFanSpeed_v2`, RPM from `nvmlDeviceGetFanSpeedRPM` (all three documented, all "intended" values). If the RPM call returns `FunctionNotFound`/`NotSupported`/`ArgumentVersionMismatch`, use `GPUApi.GetClientFanCoolersStatus(handle).FanCoolersStatusEntries[i].CurrentRPM` (private; this one is a real reading per LHM's use of it), and after that the public `GPUApi.GetTachReading(handle)` (first fan only). Display only. The contract's `SensorSnapshot.gpu.fanPercent` / `fanRpm` are single values: report fan 0, or the maximum over all fans, and say which in a note.
2. Manual set, preferred order:
   1. NVML `nvmlDeviceSetFanSpeed_v2` per fan (admin), after clamping to `GetMinMaxFanSpeed`.
   2. If NVML returns NotSupported, call `GPUApi.GetClientFanCoolersControl(handle)` directly, build a new `PrivateFanCoolersControlV1(entries, control.UnknownUInt)` in which every entry for the target cooler is `new FanCoolersControlEntry(coolerId, FanCoolersControlMode.Manual, level)`, then call `GPUApi.SetClientFanCoolersControl(handle, newControl)`. This **bypasses** defects F1, F4, and F7.
3. Restore automatic control:
   - NVML: `nvmlDeviceSetDefaultFanSpeed_v2` per fan, then **re-read** `nvmlDeviceGetFanControlPolicy_v2` to check it.
   - NVAPI: read `GetClientFanCoolersControl`, build a new `PrivateFanCoolersControlV1` in which **every** entry is `new FanCoolersControlEntry(entry.CoolerId, FanCoolersControlMode.Auto)` (the 2-argument constructor sets level 0 and is legal for Auto), keep `UnknownUInt`, call `SetClientFanCoolersControl`, then re-read `GetClientFanCoolersControl` and check that every `ControlMode == Auto`. Do **not** use `GPUCoolerInformation.RestoreCoolerSettingsToDefault` (defects F4, F8, F9).
   - Restore whichever path RigControl used to set. If the path is unknown (crash recovery), run both.
4. Safety rules (mandatory):
   - Floor: never write below `max(30, CurrentMinimumLevel)` % unless the GPU temperature is under 50 °C. The 30 % / 50 °C numbers are this spec's own conservative choice, not from any source. **Manual mode disables zero-RPM and thermal fan ramping** (NVML-H L9902-9904: "YOU have to monitor the temperature and adjust the fan speed accordingly. If you set the fan speed too low you can burn your GPU!").
   - Watchdog: while in manual mode, if the GPU temperature is at or above 80 °C, or any read fails twice in a row, immediately restore Auto.
   - Restore Auto on: app exit, `AppDomain.ProcessExit`, `SystemEvents.SessionEnding`, `PowerModeChanged` (suspend), and unhandled exceptions. Assume a crash **leaves the fan in manual mode**; ClientFanCoolers state persistence after process death is **UNVERIFIED**. At startup, if any cooler reads Manual and RigControl did not set it in this session, show a banner and offer "Restore automatic".
   - Never write from more than one thread or process. Check whether another tool (MSI Afterburner, GIGABYTE Control Center/AORUS Engine, NVIDIA App performance tuning) is running and refuse if one is. GIGABYTE Control Center is installed on the development PC (task brief).

### 3.5 Recommendation

**Do not ship GPU fan *control* in v1. Ship fan *monitoring* only.**

Reasons:
- The only NVAPI path for RTX 40 is private and undocumented, and is unverified on 621.24.
- The documented NVML path needs admin, is unverified on GeForce/Windows, and has a reported restore bug.
- A stuck-manual fan on a 285 W-class card is a hardware risk.

Revisit after the section 11 probe succeeds on this exact driver. Re-run the probe after every driver update, gating on `NVIDIA.DriverVersion`.

---

## 4. Driver 3D settings (DRS, the "Profile Inspector" approach)

### 4.1 API surface (namespace `NvAPIWrapper.DRS`)

| Step | Signature | Source |
|---|---|---|
| Create session and load from the driver store | `public static DriverSettingsSession DriverSettingsSession.CreateAndLoad()` | `NW/DRS/DriverSettingsSession.cs` L105. Calls `NvAPI_DRS_CreateSession 0x0694D52E`, then `NvAPI_DRS_LoadSettings 0x375DBD6B`. |
| Load from a file (backup restore) | `CreateAndLoad(string fileName)` | L118 (`NvAPI_DRS_LoadSettingsFromFile 0xD3EDE889`) |
| Release | `void Dispose()` (`IDisposable`). There is also a finalizer. | L95, L231 (`NvAPI_DRS_DestroySession 0xDAD9CFF8`) |
| Base profile | `DriverSettingsProfile BaseProfile { get; }`, null if none | L26 (`GetBaseProfile 0xDA8466A0`) |
| Current global profile | `DriverSettingsProfile CurrentGlobalProfile { get; set; }`. The setter takes a profile and uses its `Name`. | L44 (`GetCurrentGlobalProfile 0x617BFF9F`, `SetCurrentGlobalProfile 0x1C89C5DF`) |
| Enumerate profiles | `IEnumerable<DriverSettingsProfile> Profiles { get; }`, `int NumberOfProfiles { get; }` | L89, L81 |
| Find profile by exe | `DriverSettingsProfile FindApplicationProfile(string applicationName)` returns **null** if not found. `ProfileApplication FindApplication(string)` returns application and profile. Pass a **full path** for the best match (doc comment). | L161, L138. `DRSApi.FindApplicationByName` maps `ExecutableNotFound` (-166) to null (`NW/Native/DRSApi.cs` L462-467). |
| Find profile by name | `DriverSettingsProfile FindProfileByName(string profileName)` | L178. **Throws `NVIDIAApiException(ProfileNotFound = -163)`** when the profile is missing, because `DRSApi.FindProfileByName` throws on any non-Ok status (`DRSApi.cs` L489-503). The null check in the wrapper is only reached for a null handle. Wrap it in try/catch. |
| Save | `void Save()` / `void Save(string fileName)` | L201, L210 (`SaveSettings 0xFCBC7E14`, `SaveSettingsToFile 0x2BE25DF8`) |
| Reset everything | `void RestoreDefaults()` | L193. **Never expose this without a confirm dialog and a prior `Save(backupFile)`.** |

`DriverSettingsProfile` (`NW/DRS/DriverSettingsProfile.cs`):

| Need | Member | Line |
|---|---|---|
| Name / predefined / valid | `string Name`, `bool IsPredefined`, `bool IsValid` | L109, L81, L101 |
| Apps in profile | `IEnumerable<ProfileApplication> Applications` | L24 |
| Settings in profile | `IEnumerable<ProfileSetting> Settings` | L174 |
| Read a setting | `ProfileSetting GetSetting(uint settingId)` / `GetSetting(KnownSettingId)` returns **null** on `SettingNotFound` | L313, L338. `DRSApi.GetSetting` L655-678 |
| Write a DWORD | `void SetSetting(uint settingId, uint value)` (Integer) | L476 |
| Write a string | `SetSetting(uint, string)` (UnicodeString) | L456 |
| Write binary | `SetSetting(uint, byte[])` | L466 |
| Explicit type | `SetSetting(uint, DRSSettingType, object)` | L437 |
| Restore one setting | `void RestoreSettingToDefault(uint settingId)` / `(KnownSettingId)` | L369, L385 (`RestoreProfileDefaultSetting 0x53F0381E`) |
| Delete a user setting | `void DeleteSetting(uint)` | L260 (`DeleteProfileSetting 0xE4A26362`) |
| Restore whole profile | `void RestoreDefaults()`. For a **non-predefined** profile this **deletes it** and invalidates the handle (L356-362). | L347 |
| Create profile | `static DriverSettingsProfile CreateProfile(DriverSettingsSession session, string profileName, DRSGPUSupport? gpuSupport = null)` | L196 (`CreateProfile 0xCC176068`) |
| Delete profile | `void Delete()` | L227 |

`ProfileSetting` (`NW/DRS/ProfileSetting.cs`): `uint SettingId`, `object CurrentValue` (uint, string, or byte[]), `bool IsCurrentValuePredefined`, `object PredefinedValue`, `DRSSettingType SettingType`, `DRSSettingLocation SettingLocation`, `SettingInfo SettingInfo`.
- `DRSSettingType` (`NW/Native/DRS/DRSSettingType.cs`): `Integer=0, Binary=1, String=2, UnicodeString=3`.
- `DRSSettingLocation`: `CurrentProfile=0, GlobalProfile=1, BaseProfile=2, DefaultProfile=3`.
- `SettingLocation` tells you where the effective value came from. Use it to show "inherited from Global" in the UI.

`ProfileApplication.CreateApplication(DriverSettingsProfile profile, string applicationName, string friendlyName = null, string launcherName = null, string[] fileInFolders = null, bool isMetro = false, string commandLine = null)` (`NW/DRS/ProfileApplication.cs` L235). It builds a `DRSApplicationV4` first. If the driver rejects V4 with `IncompatibleStructureVersion`, `DRSApi.CreateApplication` throws `NVIDIANotSupportedException` (`DRSApi.cs` L39-42), but that throw happens **outside** the version-fallback loop (L285), so **V3, V2, and V1 are never tried**. Acceptable, because current drivers accept V4 (UNVERIFIED for 621.24). `applicationName` is the exe file name, for example `game.exe`. Duplicates fail with `ExecutableAlreadyInUse (-167)`.

`DRSGPUSupport` (`NW/Native/DRS/Structures/DRSGPUSupport.cs`): `bool IsGeForceSupported, IsQuadroSupported, IsNVSSupported`. `CreateProfile` defaults to `new DRSGPUSupport()` (all false). Whether an all-false profile applies on GeForce is **UNVERIFIED**. Pass `IsGeForceSupported = true` explicitly; confirm the property is settable by checking `DRSGPUSupport.cs`.

### 4.2 Required usage pattern

```
// On the NVAPI worker thread. NVIDIA.Initialize() already called.
using var s = DriverSettingsSession.CreateAndLoad();      // fresh snapshot every time
s.Save(backupPath);                                        // first-run and pre-write backup (.nip-like binary)
var global = s.CurrentGlobalProfile;                       // "global" = the CPL's Global tab
var setting = global.GetSetting(0x00A879CFu);              // null => not set here
global.SetSetting(0x00A879CFu, 0x08416747u);               // VSync Force off
s.Save();                                                  // persist; nothing applies until Save()
// Per-app:
var p = s.FindApplicationProfile(@"C:\Games\x\game.exe")
        ?? CreateProfileWithApp(s, "RigControl - game", "game.exe");
p.SetSetting(...); s.Save();
```

- Always use **`CurrentGlobalProfile`** for "global" changes. Use `BaseProfile` only for read-only display, because the Base and Global profile semantics differ (UNVERIFIED in detail).
- Never keep a session open across UI interactions. Create, load, modify, save, and dispose in one unit, so changes made meanwhile by NVIDIA App or NPI are not overwritten with stale data.
- **Restore a setting to default** with `profile.RestoreSettingToDefault(id)` then `Save()`. For a setting RigControl added to a user-created profile, `DeleteSetting(id)` then `Save()` has the same effect.
- Running games pick up changes on their next launch. Live-apply behaviour is **UNVERIFIED** per setting.
- Admin: NVIDIA Profile Inspector's manifest declares `requireAdministrator` (NPI `app.manifest`). The DRS database files on the development PC grant Everyone FullControl (LOCAL ACL), so `Save()` *may* succeed unelevated. **UNVERIFIED.** Treat writes as admin-required (section 8), and handle `InvalidUserPrivilege (-137)` and `AccessDenied (-175)`.

### 4.3 Setting IDs and values

Columns: **ID** (hex DWORD), **KnownSettingId** (wrapper enum member, `NW/DRS/KnownSettingId.cs`, with line), and **Values** (wrapper enum in `NW/DRS/SettingValues/<Name>.cs` and/or HDR-H / NPI-CSN). "Agree" means the wrapper and HDR-H [WEB] have the same values.

| UI name | ID | KnownSettingId (line) | Values | Confidence |
|---|---|---|---|---|
| Vertical sync | `0x00A879CF` | `VSyncMode` (L544) | `VSyncMode`: Passive (use 3D app setting) `0x60925292` = default, ForceOff `0x08416747`, ForceOn `0x47814940`, FlipInterval2 (1/2 refresh) `0x32610244`, FlipInterval3 `0x71271021`, FlipInterval4 `0x13245256`, Virtual (**Fast Sync**) `0x18888888` | Source + HDR-H agree. NPI-CSN names Virtual as "Fast Sync". |
| Vsync tear control (Adaptive) | `0x005A375C` | `VSyncTearControl` (L550) | Disable (Standard) `0x96861077`, Enable (Adaptive) `0x99941284` | Source + HDR-H agree |
| Max frame rate (current CPL / NVIDIA App "Max Frame Rate") | `0x10835002` | **not in wrapper** | Off `0x0`. Otherwise fps as a plain integer; HDR-H max `0x3FF` (1023). NPI-CSN lists 20..1000 fps. | [WEB] HDR-H (`FRL_FPS_ID`) + NPI-CSN ("Frame Rate Limiter V3") agree |
| Legacy frame rate limiter (v1) | `0x10834FEE` | `PerformanceStateFrameRateLimiter` (L278) | Bit-field enum `PerformanceStateFrameRateLimiter.cs` (fps mask `0xFF`, flags up to `Enabled 0x80000000`). **Do not use for the "Max Frame Rate" UI.** `PS_FRAMERATE_LIMITER_ID` is absent from current HDR-H. | Source only, legacy |
| Background application max frame rate | `0x10835005` | **not in wrapper** | Off `0x0`; 5..60 fps as a plain integer (NPI-CSN "Frame Rate Limiter - Background Application"). Whether the CPL allows values above 60 is **UNVERIFIED**. | [WEB] NPI-CSN only. Not in HDR-H. |
| Low latency mode: Off / On | `0x007BA09E` (max pre-rendered frames) | `PreRenderLimit` (L488) | 0 = app-controlled (CPL "Off"), 1 = CPL "On". Range 0..0xFF (`PreRenderLimit.cs`). | Source + HDR-H agree on ID and range. The CPL label mapping is [WEB] NPI issue #101 and is **UNVERIFIED**. |
| Low latency mode: Ultra | `0x0005F543` ("Ultra Low Latency - CPL State": Off 0, On 1, Ultra 2) **and** `0x10835000` ("Ultra Low Latency - Enabled": Off 0, On 1) | **not in wrapper** | To reproduce the CPL "Ultra" state, **UNVERIFIED** recipe: CPL State = 2, Enabled = 1, and per NPI issue #101, PreRenderLimit = 0. Confirm by setting Ultra in a CPL that works, then reading all three IDs from `CurrentGlobalProfile`. Note: the CPL cannot run right now, so this needs NVIDIA App or a temporarily re-enabled service. Not reachable by RigControl. | [WEB] NPI-CSN only. HDR-H has **no** ultra or low-latency ID. |
| Power management mode | `0x1057EB71` | `PreferredPerformanceState` (L267) | Adaptive 0, PreferMaximum 1, DriverControlled 2, PreferConsistentPerformance 3, PreferMinimum 4, **OptimalPower 5 = default** (CPL "Normal" = 5; Normal label is UNVERIFIED) | Source + HDR-H agree. NPI-CSN shows only 0, 1, 5. |
| Texture filtering quality | `0x00CE2691` | `QualityEnhancements` (L523) | HighQuality `0xFFFFFFF6`, Quality `0x0` = default, Performance `0xA`, HighPerformance `0x14` | Source + HDR-H + NPI-CSN agree |
| Anisotropic filtering: mode | `0x10D2BB16` | `AnisotropicModeSelector` (L167) | Application 0 = default, User 1, Condition 2 | Source + HDR-H agree |
| Anisotropic filtering: level | `0x101E61A9` | `AnisotropicModeLevel` (L161) | NonePoint 0, NoneLinear 1 = default, 2/4/8/16 = 2x/4x/8x/16x (`0x10` max). To force 16x, set mode = 1 **and** level = 0x10. | Source + HDR-H. Per-level values from NPI-CSN [WEB]. |
| Antialiasing: mode | `0x107EFC5B` | `AntiAliasingModeSelector` (L150) | ApplicationControl 0 = default, Override 1, Enhance 2 | Source + HDR-H agree |
| Antialiasing: setting | `0x10D773D2` | `AntiAliasingModeMethod` (L139) | None 0, SS 4x `0x5`, MS 4x `0x10`, MS 8x `0x25`, MS 16x `0x1C`, ... (HDR-H `AA_MODE_METHOD_*`). The wrapper enum `AntiAliasingModeMethod.cs` is the full list. | Source + HDR-H |
| Antialiasing: transparency | `0x10FC2D9C` (multisampling), `0x10D48A85` (supersampling) | `AntiAliasingModeAlphaToCoverage` (L127), `AntiAliasingModeReplay` (L145) | See wrapper enums | Source |
| FXAA enable | `0x1074C972` | `FXAAEnable` (L234) | Off 0 = default, On 1 | Source + HDR-H agree |
| FXAA allowed (predefined, read-only meaning) | `0x1034CB89` | `FXAAAllow` (L229) | Disallowed 0, Allowed 1. Do not write. | Source + HDR-H |
| MFAA | `0x0098C1AC` | `MaxwellBSampleInterleave` (L482) | Off 0, On 1 | Source + NPI-CSN |
| G-SYNC global enable (feature) | `0x1094F157` | `VRRFeatureIndicator` (L366) | Disabled 0, Enabled 1 = default | Source + HDR-H agree |
| G-SYNC global mode | `0x1194F158` | `VRRMode` (L393) | Disabled 0, FullScreenOnly 1 = default, FullScreenAndWindowed 2 | Source + HDR-H agree. NPI-CSN calls it "GSYNC - Application Mode". |
| G-SYNC requested state | `0x1094F1F7` | `VRRRequestState` (L377) | Off 0, FullScreen 1, FullScreen+Windowed 2 (NPI-CSN "GSYNC - Global Mode") | Source ID. Values [WEB] NPI-CSN. |
| G-SYNC per-app override | `0x10A879CF` | `VRRApplicationOverride` (L382) | Allow 0 = default, ForceOff 1, DisAllow 2, ULMB 3, FixedRefresh 4 | Source + HDR-H agree |
| G-SYNC per-app requested | `0x10A879AC` | `VRRApplicationOverrideRequestState` (L387) | Same five values | Source + NPI-CSN |
| Vsync with VRR | `0x10A879CE` | `VSyncVRRControl` (L404) | Disable 0, Enable 1 = default, NotSupported `0x9F95128E` | Source + HDR-H agree |
| G-SYNC indicator overlay | `0x10029538` | not in wrapper (`VRROverlayIndicator 0x1095F16F` is in the wrapper, L372) | Off 0, On 1 | [WEB] NPI-CSN. Which ID the current CPL uses is **UNVERIFIED**. |
| Preferred refresh rate | `0x0064B541` | `RefreshRateOverride` (L529) | ApplicationControlled 0 = default, HighestAvailable 1. HDR-H also has `LOW_LATENCY_RR_MASK 0xFF0`. | Source + HDR-H agree |
| Threaded optimisation | `0x20C1221E` | `OpenGLThreadControl` (L104) | Default/Auto 0, Enable (On) 1, Disable (Off) 2 | Source + HDR-H + NPI-CSN agree. The wrapper name says OpenGL, but this is the CPL's single "Threaded optimization" control (NPI-CSN). That it also governs DX is **UNVERIFIED**. |
| Shader cache enable (legacy on/off) | `0x00198FFF` | `PerformanceStateShaderDiskCache` (L493) | Off 0, On 1 = default | Source + HDR-H |
| Shader cache size | `0x00AC8497` | **not in wrapper** | Value is in **MB**: Off 0, 128 `0x80`, 256 `0x100`, 512 `0x200`, 1GB `0x400`, 4GB `0x1000`, 5GB `0x1400`, 8GB `0x2000`, 10GB `0x2800`, 16GB `0x4000`, 100GB `0x19000`, Unlimited `0xFFFFFFFF`. HDR-H default `0x4000`. | [WEB] HDR-H (`PS_SHADERDISKCACHE_MAX_SIZE_ID`) + NPI-CSN agree. "MB" is inferred from the NPI labels. |
| Ambient occlusion: setting | `0x00667329` | `AmbientOcclusionMode` (L447) | Off 0 = default, Low/Performance 1, Medium/Quality 2, High 3 | Source + HDR-H agree |
| Ambient occlusion: usage (predefined) | `0x00664339` | `AmbientOcclusionModeActive` (L453) | Disabled 0, Enabled 1. Do not write. | Source + HDR-H |
| CUDA sysmem fallback policy | `0x10ECECC9` | **not in wrapper** | Driver default 0, Prefer no sysmem fallback 1, Prefer sysmem fallback 2 | [WEB] NPI-CSN only. Not in HDR-H. |
| CUDA GPUs | `0x10354FF8` | `CUDAExcludedGPUs` (L212) | String; `"none"` = all GPUs | Source + HDR-H |
| Image scaling (NIS) per app | `0x10444444` | `NvidiaQualityUpScaling` (L250) | Off 0 = default, On 1 | Source + HDR-H agree on ID. That this is the CPL "Image Scaling" per-app toggle is **UNVERIFIED**. |
| Image sharpening | `0x00598928` (enabled 0/1), `0x002ED8CD` (sharpen 0..100 → 0.00..1.00), `0x002ED8CE` (ignore film grain 0..100) | not in wrapper | | [WEB] NPI-CSN only. These belong to the older "Image Sharpening" feature. Whether NIS in 621.24 uses them is **UNVERIFIED**. |
| Image scaling (NIS) global enable and render resolutions | none known | none | The global NIS switch and its scaled-resolution list are **not DRS settings** in any source found. Community reports put them in the registry under the display driver key. **UNVERIFIED**, so not implementable. | |
| DSR factors / DSR smoothness / DLDSR | none | none | **Not in HDR-H, NPI-CSN, or the wrapper.** An NVIDIA developer-forum thread ("NVAPI to set Dynamic Super Resolution") found no NVAPI setting. Community guides edit per-monitor registry values. **UNVERIFIED, so do not implement.** Note: DSR is unavailable while the monitor uses **DSC**. The LG UltraGear+ at 1440p and ~240 Hz may use DSC (UNVERIFIED). | |

Not reproducible through DRS (no ID in any source): "RTX Video Super Resolution / HDR", "DLSS Override", and "Smooth Motion". These are NVIDIA App features (section 7). NPI-CSN does list DLSS-FG preset and mode IDs (`0x10E41DF1`, `0x10308298`, `0x10CF4125`, `0x10562D0F`), but they are known to need NVIDIA App's override allow-list. **UNVERIFIED, so do not ship.**

Discovery helpers in the wrapper (useful for a hidden "advanced" page):
- `SettingInfo.GetAvailableSetting()` (`NW/DRS/SettingInfo.cs` L222; `NvAPI_DRS_EnumAvailableSettingIds 0xF020614A`)
- `SettingInfo.FromId(uint)` (L191) with `.Name`, `.DefaultValue`, `.AvailableValues`
- `DRSApi.GetSettingNameFromId` / `GetSettingIdFromName` (`NW/Native/DRSApi.cs` L715 / L695)

Use these at bring-up to confirm every **[WEB]-only** ID above. A setting ID the driver does not know returns `SettingNotFound` or an empty name.

---

## 5. Displays

### 5.1 What the wrapper supports

| Feature | Wrapper API | Source | Notes |
|---|---|---|---|
| List NVIDIA displays | `NvAPIWrapper.Display.Display.GetDisplays()` returns `Display[]`. `Display.Name` is the GDI name, for example `\\.\DISPLAY1`. | `NW/Display/Display.cs` L147, L104 | "Display handles can get invalidated on a modeset" (L144). Re-enumerate after any mode change. |
| Display by GDI name | `new Display(string displayName)` | L31 | |
| Display IDs and connection | `PhysicalGPU.GetConnectedDisplayDevices(ConnectedIdsFlag)` / `GetDisplayDevices()` returns `DisplayDevice[]` with `DisplayId`, `ConnectionType`, `IsActive`, `IsConnected`, ... | `NW/GPU/PhysicalGPU.cs` L378, L397; `NW/Display/DisplayDevice.cs` L26-78, L430-474 | |
| Digital vibrance get/set | `Display.DigitalVibranceControl` returns `DVCInformation` with `int CurrentLevel { get; set; }`, `DefaultLevel`, `MinimumLevel`, `MaximumLevel`, and `double NormalizedLevel { get; set; }` (-1..1, 0 = default) | `NW/Display/Display.cs` L39; `NW/Display/DVCInformation.cs` L43-133 | Tries `GetDVCInfoEx 0x0E45002D` / `SetDVCLevelEx 0x4A82C2B1`, then falls back to legacy `GetDVCInfo 0x4085DE45` / `SetDVCLevel 0x172409B4`, and caches which one worked. The setter clamps and is a no-op when unchanged. The CPL shows 0-100 % with default 50 %; the mapping of raw levels to CPL % is **UNVERIFIED**. Read Min/Default/Max at runtime and map linearly. Persistence across reboot is **UNVERIFIED**: re-apply on startup. |
| Hue | `Display.HUEControl` returns `HUEInformation` with `int CurrentAngle { get; set; }`, `DefaultAngle` | `NW/Display/HUEInformation.cs` L36, L71 | |
| Colour depth / format / range | `DisplayDevice.CurrentColorData`, `DefaultColorData` return `ColorData { ColorDataFormat ColorFormat; ColorDataColorimetry Colorimetry; ColorDataDynamicRange? DynamicRange; ColorDataDepth? ColorDepth; ColorDataSelectionPolicy? SelectionPolicy; ColorDataDesktopDepth? DesktopColorDepth }`. Set with `DisplayDevice.SetColorData(ColorData)`. Check first with `bool IsColorDataSupported(ColorData)`. | `NW/Display/DisplayDevice.cs` L83, L113, L795, L715; `NW/Display/ColorData.cs` L20-63 | Tries `ColorDataV5`..`V1` (`NvAPI_Disp_ColorControl 0x92F9D80D`). Enums (`NW/Native/Display/*.cs`): `ColorDataFormat { RGB=0, YUV422, YUV444, YUV420, Default=0xFE, Auto=0xFF }`; `ColorDataDynamicRange { VESA=0 (Full), CEA=1 (Limited), Auto=2 }`; `ColorDataDepth { Default=0, BPC6=1, BPC8=2, BPC10=3, BPC12=4, BPC16=5 }`; `ColorDataSelectionPolicy { User=0, BestQuality=1 (=Default), Unknown=0xFF }`; `ColorDataColorimetry { RGB=0, YCC601, YCC709, ..., BT2020RGB, BT2020YCC, BT2020cYCC, Default=0xFE, Auto=0xFF }`. To mimic the CPL "Use NVIDIA colour settings", use `SelectionPolicy = User` together with explicit values. "Default colour settings" means `BestQuality`. |
| DP colour capability list | `DisplayDevice.DisplayPortColorCapabilities` returns `MonitorColorData[]` (DP only, otherwise null) | L140 | |
| HDR state | `DisplayDevice.HDRColorData` returns `HDRColorData` or **null when HDR is Off or NotSupported**. Fields: `ColorDataHDRMode HDRMode`, `MasteringDisplayData`, format, range, depth. | L394-425; `NW/Display/HDRColorData.cs` | `ColorDataHDRMode { Off=0, UHDA=2, EDR=3, SDR=4, UHDAPassthrough=5, UHDANB=6, DolbyVision=7 }`. `NvAPI_Disp_HdrColorControl 0x351DA224`. |
| HDR capability | `DisplayDevice.DriverHDRCapabilities` / `EffectiveHDRCapabilities` return `HDRCapabilitiesV1` with `IsST2084EOTFSupported`, `IsTraditionalHDRGammaSupported`, `IsEDRSupported`, `IsTraditionalSDRGammaSupported`, `DisplayData` | L156, L164 | |
| HDR on/off | `DisplayDevice.SetHDRColorData(HDRColorData)` exists (L869) | | **Do not use it to toggle Windows HDR.** The NVAPI HDR mode is a driver-level override and does not change the Windows "Use HDR" state (UNVERIFIED). Use the Win32 path in 5.2 instead. |
| Timing | `DisplayDevice.CurrentTiming` returns `Timing` | L105 (`NvAPI_DISP_GetTiming`) | Read-only display. |
| Scaling mode | `PathInfo.GetDisplaysConfig()` returns `PathInfo[]`. Each `PathInfo.TargetsInfo[i].Scaling` is a `Scaling` enum: `Default=0, ToClosest=1, ToNative=2, GPUScanOutToNative=3, ToAspectScanOutToNative=5, ToAspectScanOutToClosest=6, GPUScanOutToClosest=7, Customized=255`. Write with `PathInfo.SetDisplaysConfig(PathInfo[], DisplayConfigFlags)`. | `NW/Display/PathInfo.cs` L131, L167; `PathTargetInfo.cs` L103; `NW/Native/Display/Scaling.cs`; `DisplayConfigFlags { ValidateOnly=1, SaveToPersistence=2, DriverReloadAllowed=4, ForceModeEnumeration=8, ForceCommitVideoPresentNetwork=0x10 }` | `NvAPI_DISP_SetDisplayConfig` rewrites the whole topology. **High risk.** Always call with `ValidateOnly` first and offer a 15-second auto-revert dialog. How the enum maps to the CPL's "Aspect ratio / Full-screen / No scaling" and "Perform scaling on: GPU/Display" is **UNVERIFIED**. **Recommend: v1 read-only.** |
| Refresh-rate override | `Display.OverrideRefreshRate(float, bool isDeferred=false)` | `NW/Display/Display.cs` L227 | The doc comment says it lasts one mode-set only. Do not use it. Use Win32 instead. |
| Custom resolutions | `DisplayDevice.TrialCustomResolution`, `SaveCustomResolution`, `RevertCustomResolution`, `GetCustomResolutions` | L554-705, L888 | Out of scope (risk of a blank screen). |
| G-SYNC capability / active | **Not wrapped.** Only the function IDs `NvAPI_D3D_IsGSyncCapable 0x9C1EED78` and `NvAPI_D3D_IsGSyncActive 0xE942B0FF` exist in `FunctionId.cs`. They need a D3D device and are not callable from the wrapper. `NvAPI_GSync_*` IDs belong to Quadro Sync boards and are unrelated. | `NW/Native/Helpers/FunctionId.cs` | Public NVAPI `NvAPI_Disp_GetVRRInfo` is not in this wrapper (UNVERIFIED ID). To approximate, show the DRS G-SYNC settings (4.3) plus the EDID/monitor name. Report capability as "unknown". |

### 5.2 Win32 display APIs (MS-DOC; not in the reference repos)

Use these for resolution and refresh-rate listing and changes, and for Windows HDR. They work without NVAPI.

```
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern bool EnumDisplaySettingsExW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);
// iModeNum: 0..n to enumerate; ENUM_CURRENT_SETTINGS = -1; ENUM_REGISTRY_SETTINGS = -2
// dwFlags: EDS_RAWMODE = 0x2 (include modes the monitor may not support), EDS_ROTATEDMODE = 0x4

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int ChangeDisplaySettingsExW(string lpszDeviceName, ref DEVMODEW lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
// dwflags: CDS_TEST = 0x2 (validate), CDS_UPDATEREGISTRY = 0x1 (persist), 0 = dynamic/temporary
// returns DISP_CHANGE_SUCCESSFUL 0, RESTART 1, FAILED -1, BADMODE -2, NOTUPDATED -3, BADFLAGS -4, BADPARAM -5, BADDUALVIEW -6
// dmFields to set: DM_PELSWIDTH 0x80000 | DM_PELSHEIGHT 0x100000 | DM_DISPLAYFREQUENCY 0x400000 (| DM_BITSPERPEL 0x40000)

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DEVMODEW {
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
  public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
  public uint dmFields;
  public int dmPositionX, dmPositionY;            // display-device union variant
  public uint dmDisplayOrientation, dmDisplayFixedOutput;
  public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
  public ushort dmLogPixels;
  public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
  public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
} // set dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() (220 bytes)

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DISPLAY_DEVICEW {
  public uint cb;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
  public uint StateFlags;   // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP 0x1, PRIMARY_DEVICE 0x4
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
  [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
}
```

These signatures and constants were written from the Win32 headers and were not fetched this session. They are standard, stable API, but the developer must compile them against `Marshal.SizeOf` (DEVMODEW = 220, DISPLAY_DEVICEW = 840) as a check.

- `dmDisplayFrequency` is an **integer Hz**. GDI cannot tell 239.76 apart from 240. For exact refresh rates (WMI reported 239 on the development PC), use `QueryDisplayConfig` + `DISPLAYCONFIG_PATH_INFO.targetInfo.refreshRate` (a rational) for display, and `SetDisplayConfig` for changes. The struct definitions are long. Use the CsWin32 source generator (`Microsoft.Windows.CsWin32`) to generate them rather than hand-writing them.
- Mode change UX: `CDS_TEST` first, then apply dynamically (flags 0), then show a 15-second "Keep changes?" dialog. On timeout, restore the `ENUM_REGISTRY_SETTINGS` mode. Write `CDS_UPDATEREGISTRY` only after the user confirms.
- **Windows HDR state**: `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9`. Set with `DisplayConfigSetDeviceInfo` + `SET_ADVANCED_COLOR_STATE = 10`. Windows 11 24H2 and later add `GET_ADVANCED_COLOR_INFO_2 = 15` and `SET_HDR_STATE = 16`. Values are from MS-DOC [WEB]; 12-17 are implicit successors of 11 in the published enum. `GET_SDR_WHITE_LEVEL = 11`. Use CsWin32 for the structs. Calling convention and struct layouts are **UNVERIFIED** in this spec.
- No admin is needed for mode changes or the HDR toggle in the current user session (MS-DOC general behaviour; UNVERIFIED on the development PC).

---

## 6. Driver update check

### 6.1 Installed version

`NVIDIA.DriverVersion` (1.3). Fallback: WMI (1.3). On the development PC both give 621.24 (LOCAL).

### 6.2 Latest Game Ready driver query [WEB, confirmed working 2026-11-15]

These endpoints are **unofficial and undocumented**. They are the same ones NVIDIA's own download page uses. They can change without notice, so treat any failure as "unknown" and fall back to 6.3.

Step A, product IDs (only needed to re-derive the constants):
- `GET https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID=127` returns XML `<LookupValueSearch><LookupValues><LookupValue ParentID="127"><Name>NVIDIA GeForce RTX 4070 Ti</Name><Value>1001</Value>...`
  - `psid = 127` is the GeForce RTX 40 Series. `pfid = 1001` is the RTX 4070 Ti.
- `GET ...lookupValueSearch.aspx?TypeID=4&ParentID=127` returns the OS list: "Windows 11" = **135**, "Windows 10 64-bit" = 57.

Step B, latest driver:
```
GET https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php
    ?func=DriverManualLookup&psid=127&pfid=1001&osID=135&languageCode=1033
    &beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1
```
- `upCRD=0` selects Game Ready. `upCRD=1` would request Studio (CRD); this parameter meaning is UNVERIFIED, inferred from `Request.theUserRequestedCRD`.
- `dch=1` selects the DCH package.

Response (JSON) observed on 2026-11-15:
- Top-level keys: `Success`, `IDS`, `spcall`, `Request`.
- `IDS[0].downloadInfo` contains:

| Field | Observed value | Use |
|---|---|---|
| `Version` | `"621.24"` | compare with the installed version |
| `Name` | `"GeForce%20Game%20Ready%20Driver"` | **URL-encoded**, so `Uri.UnescapeDataString` it |
| `ReleaseDateTime` | `"Thu Nov 12, 2026"` | display only. Format is not ISO. |
| `DownloadURL` | `https://us.download.nvidia.com/Windows/621.24/621.24-desktop-win10-win11-64bit-international-dch-whql.exe` | **do not auto-download** (section 6.3) |
| `DetailsURL` | `https://www.nvidia.com/en-us/drivers/details/285320/` | open in the browser |
| `IsWHQL` / `IsBeta` / `IsCRD` / `IsDC` | `"1"` / `"0"` / `"0"` / `"1"` | strings, not booleans |
| `OSName` | `"Windows%2011"` | |

`Request.theSystemIsDc = "1"` is also returned. The response lists the series and products it applies to (RTX 40 includes "4070 Ti"). `Success` was present, but its value was not captured, so treat it as **UNVERIFIED** whether it is `"1"` or `1`.

Comparison: parse both versions as `decimal` with the invariant culture. An update exists when latest > installed. Check at most once per day, with a 10-second timeout, a generic User-Agent, and no identifying headers.

### 6.3 Fallback and policy

- On any HTTP, JSON, or schema error, show "Could not check. Open NVIDIA driver page" linking to `https://www.nvidia.com/en-us/drivers/` (the official manual driver search).
- **Never download or run the installer from RigControl.** Open `DetailsURL` in the browser and let the user install. Driver installation needs admin and reboots the display stack, and NVIDIA App normally owns this flow.

---

## 7. NVIDIA App / Control Panel features RigControl cannot reproduce

| Feature | Why | Needs NVIDIA services? |
|---|---|---|
| Instant Replay / recording / ShadowPlay highlights / screenshots | Proprietary capture stack (NvFBC/NVENC through NVIDIA App's ShadowPlay component, installed as "NVIDIA ShadowPlay 11.0.9.0"). Not exposed by NVAPI or NVML. | Yes (NvContainer, now disabled) |
| In-game overlay, performance overlay, Alt+Z | NVIDIA App process injection | Yes |
| Game optimisation ("Optimal settings") | Cloud database plus per-game config writers | Yes, plus a NVIDIA account / network |
| RTX Video Super Resolution / RTX Video HDR | No DRS ID or NVAPI call found in any source. Stored by NVIDIA App (UNVERIFIED where). | Probably (UNVERIFIED) |
| DLSS Override / DLSS model presets / Smooth Motion / Multi Frame Generation override | NVIDIA App per-game allow-list. Some related DRS IDs exist in NPI-CSN (4.3), but writing them alone is UNVERIFIED to work. | Yes (UNVERIFIED) |
| NVIDIA Broadcast, RTX Voice, NVIDIA Virtual Audio | Separate apps and drivers | Separate |
| Freestyle / Ansel filters | Overlay-based. DRS `AnselEnable 0x1075D972` exists but does not reproduce the UI. | Yes |
| Automatic performance tuning / one-click OC / voltage-frequency curve | Private NVAPI (`ClockBoost*`, `Pstates20` IDs exist in `FunctionId.cs`), undocumented and risky. **Out of scope.** | No, but unsafe |
| DSR / DLDSR factors and smoothness, NIS global toggle and resolutions | No public API (4.3) | n/a |
| G-SYNC "Set up G-SYNC" page, ULMB / Pulsar, monitor-specific features | Monitor-side and driver-private | n/a |
| Driver install and update, Studio/Game Ready switching | Installer flow, admin, reboot | NVIDIA App |
| Redeem rewards, NVIDIA account, notifications, telemetry | Cloud | Yes |
| Physical "Set PhysX configuration" | Private | n/a |
| Multi-display / Mosaic / Surround setup | NVAPI Mosaic exists in the wrapper (`NW/Mosaic/*`), but it is out of scope for a single-monitor PC | No |

---

## 8. Admin requirements (summary)

| Operation | Admin | Basis |
|---|---|---|
| NVAPI init, enumerate, sensors, clocks, usage, DVC **read** | No | LHM runs unelevated. NVIDIA does not document this, so **UNVERIFIED** formally. |
| DVC / hue **write** | **UNVERIFIED** (probably not) | test in bring-up |
| DRS read | No (UNVERIFIED) | |
| DRS `Save()` | **Treat as required**. NPI requires admin. The file ACL on the development PC would allow unelevated writes. **UNVERIFIED.** | NPI manifest; LOCAL ACL |
| NVAPI ClientFanCoolers **write** | **UNVERIFIED** | |
| NVML queries | No | LHM |
| NVML set power limit / fan speed / fan policy | **Yes** | NVML-DOC [WEB] |
| Win32 resolution / refresh / HDR change | No (UNVERIFIED on the development PC) | MS-DOC |
| Colour data / scaling (NVAPI) write | **UNVERIFIED** | |

Architecture: run the main UI **unelevated**. Put every write marked yes or UNVERIFIED behind a small elevated helper process (or a single elevated mode), started on demand with `runas` and receiving commands over a named pipe with an explicit allow-list. On the first "access denied" from NVAPI (`InvalidUserPrivilege -137`, `AccessDenied -175`) or NVML (`NoPermission 4`), show "Requires administrator" instead of failing silently. Do **not** install a service for this (hard rule: no system changes).

---

## 9. Threading, bitness, and error handling

### 9.1 Bitness

- The process **must be x64** (1.1). A 32-bit process on 64-bit Windows throws `NVIDIANotSupportedException` at the first call.
- `nvml.dll` in System32 is 64-bit (LOCAL path). Never probe SysWOW64.

### 9.2 Threading

- NVAPI thread-safety is **not documented** in any source read. NvAPIWrapper only locks its delegate cache (`DelegateFactory.cs` L33). **Rule: one dedicated long-lived "GPU worker" thread (or a `SemaphoreSlim(1)`-guarded single-threaded scheduler) owns every NvAPIWrapper and NVML call**, including `Initialize`, `Unload`, polling, DRS sessions, and fan writes. The UI posts requests to it.
- `DriverSettingsSession` has a **finalizer** that calls `DestroySession` (`DriverSettingsSession.cs` L231). That runs on the GC finalizer thread, which breaks the one-thread rule. **Always `Dispose()` sessions explicitly** inside `using`, on the worker.
- Handles from the `GetDisplays()` and `PhysicalGPU` objects can become invalid after a mode-set, TDR, driver update, or sleep/resume: `Status.HandleInvalidated (-10)`, `ExpectedPhysicalGPUHandle (-101)`, `NvidiaDeviceNotFound (-6)`. On these statuses, do `Unload()` → `Initialize()` → re-enumerate, with backoff (1 s, 5 s, 30 s).
- NVML: call `nvmlInit_v2` once and `nvmlShutdown` once, on the same worker. LHM guards with a lock (`NvidiaML.cs` L145, L267). Device handles become invalid after `GpuIsLost (15)` or a driver reload; re-init.

### 9.3 Error model

- `NvAPIWrapper.Native.Exceptions.NVIDIAApiException : Exception`, with `Status Status { get; }` (`NW/Native/Exceptions/NVIDIAApiException.cs`). **Its `Message` getter calls `GeneralApi.GetErrorMessage`** (L17-20), which calls into NVAPI again. When logging from an unknown thread or after a driver crash, **log `ex.Status` (the enum), not `ex.Message`**.
- `NvAPIWrapper.Native.Exceptions.NVIDIANotSupportedException : NotSupportedException`. It is thrown when a function ID cannot be resolved, when no struct version is accepted, or for the 32-bit case. Catch it separately; it is **not** an `NVIDIAApiException`.
- `System.ArgumentException` comes from the wrapper's own validation (fan defects F1 and F2), and `InvalidOperationException` from use of a deleted DRS profile (`DriverSettingsProfile.cs` L178-183 and others).

`Status` codes most relevant here (`NW/Native/General/Status.cs`):

| Code | Name | Typical cause |
|---|---|---|
| -1 | Error | generic |
| -4 | ApiNotInitialized | call before `Initialize()` |
| -5 | InvalidArgument | bad cooler id or level |
| -6 | NvidiaDeviceNotFound | driver gone (TDR, update) |
| -7 | EndEnumeration | normal end of enumeration (handled inside the wrapper) |
| -8 / -10 | InvalidHandle / HandleInvalidated | after a mode-set, so re-enumerate |
| -9 | IncompatibleStructureVersion | private struct changed in the new driver (fan F4/F7) |
| -104 | NotSupported | feature not on this GPU (legacy coolers on RTX 40, EDID, etc.) |
| -136 | FunctionNotFound | |
| -137 | InvalidUserPrivilege | needs admin |
| -160 | SettingNotFound | DRS: setting not present in the profile (wrapper returns null) |
| -163 | ProfileNotFound | DRS `FindProfileByName` (wrapper throws) |
| -164 | ProfileNameInUse | DRS `CreateProfile` duplicate |
| -166 | ExecutableNotFound | DRS: exe not in any profile (wrapper returns null) |
| -167 | ExecutableAlreadyInUse | DRS `CreateApplication` duplicate |
| -168 | DataTypeMismatch | DRS: wrote a uint to a string setting, or the reverse |
| -175 | AccessDenied | permissions |
| -209 | ApiInUse | another client holds the resource |

Calls known to throw on unsupported hardware or drivers (wrap each one):
- `GPUCoolerInformation.Coolers`, `SetCoolerSettings`, and `RestoreCoolerSettingsToDefault` (section 3).
- `ThermalInformation.ThermalSensors`, and `GPUApi.GetThermalSettings` when no struct version is accepted.
- `MemoryInformation.RAMType`, `RAMMaker`, and the other private getters.
- `PerformanceControl.*` (private power and thermal policies).
- `UsageInformation.*`, whose final fallback is private `GetUsages`.
- `PowerTopologyInformation`.
- `PhysicalGPU.PerformanceStatesInfo` and `ReadEDIDData`.
- `DisplayDevice.HDRColorData` (returns null on NotSupported but rethrows anything else).
- `DisplayDevice.DisplayPortColorCapabilities`.
- DVC in mode-detect: it swallows the first failure, but **rethrows** once the mode is cached (`DVCInformation.cs` L189-197, L222-230).

`PhysicalGPU.Board` already maps NotSupported to `default` (`PhysicalGPU.cs` L93-106).

Polling rule: a getter that fails 3 times in a row is disabled for the rest of the session. The worker retries it once after the next re-init. This keeps a throwing private API from spamming exceptions at 1 Hz.

---

## 10. Implementation checklist for developers

1. x64 only. Reference `NvAPIWrapper.Net` 0.8.1.101, which binds to the netstandard2.0 asset.
2. Create a `GpuWorker` thread. It owns `NVIDIA.Initialize/Unload`, `nvmlInit_v2/nvmlShutdown`, and all calls.
3. Identity: find the `PhysicalGPU` whose PCI IDs match DEV 2782 / SUBSYS 40CA1458; log the raw `PCIIdentifiers` once. Get the NVML handle by PCI bus id (`nvmlDeviceGetHandleByPciBusId_v2`) or by index 0.
4. Stats at 1 Hz:
   - NVML: temperature, utilisation, memory, power (mW), fan % per fan.
   - NvAPIWrapper: clocks (kHz → MHz) and the fan RPM fallback.
5. DRS page: the settings table in 4.3, limited to rows that are **not** UNVERIFIED. Back up to `%LOCALAPPDATA%\RigControl\drs-backup-<driver>-<timestamp>.nip` before the first write. Writes go through the elevated helper.
6. Display page: Win32 resolution and refresh (with revert timer), digital vibrance, NVAPI colour data (with `IsColorDataSupported`), and Windows HDR via DisplayConfig. Scaling is read-only.
7. Driver check: 6.2, with the 6.3 fallback.
8. Fan: monitoring only (3.5).
9. Power limit: NVML get, constraints, and set through the elevated helper, re-applied at start. Only after bring-up confirms it works.

---

## 11. Bring-up verification plan (run on the development PC before enabling any write)

Every step is something the developer runs after RigControl is built. None of it was run during this research.

1. **Init and identity** (read-only): log `NVIDIA.DriverVersion` (expect 62124), `DriverBranchVersion`, `PhysicalGPU.FullName`, `PCIIdentifiers`, and `DedicatedVideoMemoryInkB`. This confirms 1.3 and 1.4, and that NVAPI works with NVDisplay.Container **disabled**.
2. **NVML** (read-only): `nvmlInit_v2`, `GetCount`, `GetName`, `GetPowerUsage`, `GetPowerManagementLimitConstraints`, `GetNumFans`, `GetFanSpeed_v2`, and `GetMinMaxFanSpeed`. Log every return code.
3. **Fan path probe** (read-only): call `GPUApi.GetCoolerSettings(handle)` and log the `Status`. Expect -104 (UNVERIFIED). Then call `GetClientFanCoolersStatus`, `GetClientFanCoolersControl`, and `GetClientFanCoolersInfo` and log `Status`, cooler count, ids, `CurrentLevel`, min, max, and mode. This resolves 3.3 and fan defects F2, F4, and F7.
4. **DRS read**: `CreateAndLoad`, then `Save(backup)`. For every ID in 4.3, call `GetSetting(id)` on `CurrentGlobalProfile` and `SettingInfo.FromId(id).Name`. Unknown IDs show up here. Mark them unsupported.
5. **DRS write round-trip** (unelevated, then elevated): on a **new throw-away profile** with a dummy exe name, set `0x00A879CF = 0x08416747`, `Save`, reload, read it back, `RestoreSettingToDefault`, `Save`, then delete the profile. This resolves the DRS admin question.
6. **DVC round-trip**: read the level, set it to the same value, then set it +1 and back. This tests write privilege without a visible change.
7. **Ultra low latency recipe**: compare the IDs `0x007BA09E`, `0x0005F543`, and `0x10835000` in the Global profile after the user sets "Ultra" in NVIDIA App's Graphics page, if NVIDIA App works with its services disabled.
8. Re-run steps 1-4 automatically after every driver version change, and disable dependent features if any step changes result.

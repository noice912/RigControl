// Development-only fake backend, used when the UI runs in a normal browser (npm run dev).
// It is never used inside RigControl.exe. Values imitate this PC so layouts can be judged honestly.
import { RpcError } from "./bridge";
import { evaluateCurve, presetCurve } from "./curve";
import { extrasMock } from "./extras";
import type {
  AppSettings,
  CoolingDevice,
  CoolingStatus,
  CpuPower,
  CpuPowerSetting,
  Display,
  DriverProfile,
  DriverSetting,
  FanControl,
  FirstRunInfo,
  HealthFinding,
  HealthReport,
  LightingStatus,
  ModuleInfo,
  NvidiaOverclock,
  NvidiaPower,
  Scene,
  SensorSnapshot,
} from "./types";

const jitter = (base: number, spread: number) => Math.round((base + (Math.random() - 0.5) * spread) * 10) / 10;

let driversChecked = false;
let captureStatus: any = {
  available: true,
  problem: null,
  recording: false,
  recordingSince: null,
  recordingFile: null,
  replayRunning: false,
  lastSaved: null,
  hotkeyProblems: [],
  settings: {
    folder: "C:\\Users\\You\\Videos\\RigControl",
    fps: 60,
    bitrateMbps: 30,
    codec: "h264",
    display: 0,
    systemAudio: true,
    replayEnabled: false,
    replaySeconds: 60,
    overlayStats: false,
    recordHotkey: "Alt+F9",
    replayHotkey: "Alt+F10",
    overlayHotkey: "Alt+R",
  },
};

export function createMockBackend() {
  let settings: AppSettings = { minimizeToTray: true, startMinimized: false, startWithWindows: false, temperatureUnit: "F" };
  const blockedBy: string[] = ["NZXT CAM"];
  let camAutoStart = true;
  let liquid = 31.4;

  const kraken: CoolingDevice = {
    id: "kraken-1",
    name: "Kraken Elite",
    product: "NZXT Kraken Elite (2023)",
    firmware: "2.1.0",
    connected: true,
    liquidTempC: 31.4,
    channels: [
      {
        id: "pump",
        name: "Pump",
        kind: "pump",
        rpm: 2380,
        dutyPercent: 70,
        minDutyPercent: 20,
        curveTempRange: { min: 20, max: 59 },
        maxCurvePoints: 40,
        curveSource: "liquid",
        storedOnDevice: true,
        mode: "unknown",
        fixedDutyPercent: null,
        curve: null,
      },
      {
        id: "fan",
        name: "Radiator fans",
        kind: "fan",
        rpm: 940,
        dutyPercent: 45,
        minDutyPercent: 0,
        curveTempRange: { min: 20, max: 59 },
        maxCurvePoints: 40,
        curveSource: "liquid",
        storedOnDevice: true,
        mode: "unknown",
        fixedDutyPercent: null,
        curve: null,
      },
    ],
    lcd: { brightness: 70, orientation: 0 },
    notes: [],
    screen: {
      mode: "unset",
      layout: "single",
      primary: "cpuTemp",
      secondary: "gpuTemp",
      accent: "#8B5CF6",
      mediaName: null,
      live: false,
      problem: null,
      layouts: ["single", "dual", "triple"],
      sources: ["cpuTemp", "gpuTemp", "liquidTemp", "cpuLoad", "gpuLoad"],
      tertiary: "liquidTemp",
      backgroundName: null,
      url: null,
      modes: ["liquid", "infographic", "media", "clock", "visualizer", "web"],
    },
  };
  const controller: CoolingDevice = {
    id: "smart-2011",
    name: "RGB & Fan Controller",
    product: "NZXT RGB & Fan Controller (3+6)",
    firmware: "1.7",
    connected: true,
    liquidTempC: null,
    channels: [1, 2, 3].map((i) => ({
      id: `fan${i}`,
      name: `Case fan ${i}`,
      kind: "fan" as const,
      rpm: i === 3 ? 0 : 820 + i * 15,
      dutyPercent: 40,
      minDutyPercent: 25,
      curveTempRange: null,
      maxCurvePoints: null,
      curveSource: null,
      storedOnDevice: false,
      mode: "unknown" as const,
      fixedDutyPercent: null,
      curve: null,
    })),
    lcd: null,
    notes: ["Runs fixed speeds. The two NZXT RGB Controllers (lighting only) are on the Lighting page."],
  };

  const coolingStatus = (): CoolingStatus => ({
    blockedBy: [...blockedBy],
    cam: { appRunning: blockedBy.length > 0, serviceRunning: blockedBy.length > 0, startsWithWindows: camAutoStart },
    devices: [kraken, controller].map((d) => ({
      ...d,
      liquidTempC: d.id === "kraken-1" ? (blockedBy.length ? null : liquid) : null,
      channels: d.channels.map((c) => ({ ...c, rpm: c.rpm === 0 ? 0 : c.rpm !== null ? Math.round(c.rpm + (Math.random() - 0.5) * 30) : null })),
    })),
  });

  const fanControls: FanControl[] = [
    { id: "lpc/0", name: "CPU fan", rpmSensorId: "fan/0", mode: "auto", dutyPercent: null, curve: null, minDutyPercent: 20, canControl: true, reason: null },
    { id: "lpc/1", name: "Chassis fan 1", rpmSensorId: "fan/1", mode: "auto", dutyPercent: null, curve: null, minDutyPercent: 20, canControl: true, reason: null },
    { id: "lpc/2", name: "Chassis fan 2", rpmSensorId: "fan/2", mode: "auto", dutyPercent: null, curve: null, minDutyPercent: 20, canControl: true, reason: null },
    { id: "lpc/4", name: "AIO pump", rpmSensorId: "fan/4", mode: "auto", dutyPercent: null, curve: null, minDutyPercent: 100, canControl: false, reason: "Pump headers are never controlled from software, so the pump can't be slowed by mistake." },
  ];

  const snapshot = (): SensorSnapshot => ({
    time: new Date().toISOString(),
    elevated: false,
    cpu: { name: "13th Gen Intel Core i9-13900KF", packageTempC: jitter(47, 6), coreMaxTempC: jitter(52, 8), loadPercent: jitter(9, 8), powerW: jitter(41, 10), clockMHz: jitter(4900, 300) },
    gpu: {
      name: "NVIDIA GeForce RTX 4070 Ti",
      tempC: jitter(41, 2),
      hotspotTempC: jitter(50, 2),
      loadPercent: jitter(6, 6),
      powerW: jitter(28, 6),
      coreClockMHz: 210,
      memoryClockMHz: 405,
      fanPercent: 0,
      fanRpm: 0,
      vramUsedMB: 1840,
      vramTotalMB: 12282,
    },
    memory: { usedGB: jitter(17.6, 0.4), totalGB: 63.8, loadPercent: 27.6 },
    board: { name: "ASUS ROG STRIX Z790-A GAMING WIFI II", superIo: "Nuvoton NCT6798D" },
    temperatures: [
      { id: "cpu/pkg", name: "CPU package", hardware: "Processor", value: jitter(47, 6) },
      { id: "gpu/core", name: "GPU core", hardware: "Graphics", value: jitter(41, 2) },
      { id: "lpc/t1", name: "Motherboard", hardware: "Motherboard", value: jitter(33, 1) },
    ],
    fans: [
      { id: "fan/0", name: "CPU fan", hardware: "Motherboard", rpm: 0, controlId: "lpc/0" },
      { id: "fan/1", name: "Chassis fan 1", hardware: "Motherboard", rpm: Math.round(jitter(760, 20)), controlId: "lpc/1" },
      { id: "fan/2", name: "Chassis fan 2", hardware: "Motherboard", rpm: 0, controlId: "lpc/2" },
      { id: "fan/4", name: "AIO pump", hardware: "Motherboard", rpm: Math.round(jitter(2380, 20)), controlId: "lpc/4" },
    ],
    notes: ["Run RigControl as administrator to read processor temperatures and motherboard fans. The values shown are estimates from Windows."],
  });

  const modules: ModuleInfo[] = [
    { name: "sensors", displayName: "Sensors and motherboard fans", state: "degraded", message: "Running without administrator rights: processor temperatures and motherboard fans need it." },
    { name: "nvidia", displayName: "NVIDIA graphics", state: "ready", message: null },
    { name: "cooling", displayName: "NZXT cooling", state: "degraded", message: "NZXT CAM is running, so cooling is read-only." },
    { name: "lighting", displayName: "Lighting (OpenRGB)", state: "degraded", message: "OpenRGB is not installed." },
    { name: "cpu", displayName: "Processor", state: "ready", message: null },
  ];

  const cpuSetting = (key: CpuPowerSetting["key"], value: number, min: number, max: number, unit: string): CpuPowerSetting => ({
    key, available: true, ac: value, dc: value, min, max, zeroMeansNoLimit: key === "maxFrequencyMHz", unit,
    options: key === "boostMode"
      ? ["Off", "On", "Aggressive", "Efficient", "Efficient aggressive", "Aggressive at guaranteed", "Efficient aggressive at guaranteed"].map((label, i) => ({ value: i, label }))
      : null,
    coreTypesDiffer: false, originalAc: null, originalDc: null,
  });
  const cpuPowerStock = (): CpuPower => ({
    available: true, problem: null, planId: "381b4222-f694-41f0-9685-ff5bb260df2e", planName: "Balanced", hasBattery: false, changed: false, changedPlans: [],
    settings: [
      cpuSetting("boostMode", 2, 0, 6, "choice"),
      cpuSetting("maxStatePercent", 100, 30, 100, "%"),
      cpuSetting("minStatePercent", 5, 0, 100, "%"),
      cpuSetting("maxFrequencyMHz", 0, 800, 10000, "MHz"),
      cpuSetting("minCoresPercent", 10, 0, 100, "%"),
    ],
  });
  let cpuPower = cpuPowerStock();

  const driverSettings: DriverSetting[] = [
    { key: "lowLatency", name: "Low latency mode", group: "Responsiveness", description: "Cuts input lag by limiting frames queued by the CPU. Ultra submits frames just in time.", kind: "enum", value: 0, defaultValue: 0, isDefault: true, options: [{ value: 0, label: "Off" }, { value: 1, label: "On" }, { value: 2, label: "Ultra" }], min: null, max: null, unit: null },
    { key: "vsync", name: "Vertical sync", group: "Responsiveness", description: "Stops tearing by waiting for the display. Adds latency.", kind: "enum", value: 0x60925292, defaultValue: 0x60925292, isDefault: true, options: [{ value: 0x60925292, label: "Let the game decide" }, { value: 0x08416747, label: "Off" }, { value: 0x47814940, label: "On" }, { value: 0x18888888, label: "Fast" }], min: null, max: null, unit: null },
    { key: "maxFrameRate", name: "Max frame rate", group: "Responsiveness", description: "Caps frames per second. 0 means no cap.", kind: "number", value: 0, defaultValue: 0, isDefault: true, options: null, min: 0, max: 1000, unit: "fps" },
    { key: "powerMode", name: "Power management mode", group: "Performance", description: "Prefer maximum performance keeps clocks high, using more power at idle.", kind: "enum", value: 1, defaultValue: 0, isDefault: false, options: [{ value: 0, label: "Normal" }, { value: 1, label: "Prefer maximum performance" }], min: null, max: null, unit: null },
    { key: "textureQuality", name: "Texture filtering quality", group: "Image quality", description: "High performance trades a little sharpness for speed.", kind: "enum", value: 0, defaultValue: 0, isDefault: true, options: [{ value: 20, label: "High quality" }, { value: 0, label: "Quality" }, { value: 10, label: "Performance" }, { value: 20 + 0, label: "High performance" }].filter((o, i, a) => a.findIndex((x) => x.value === o.value) === i), min: null, max: null, unit: null },
    { key: "shaderCache", name: "Shader cache size", group: "Performance", description: "How much disk space compiled shaders may use. Bigger means fewer stutters.", kind: "enum", value: 0xffffffff, defaultValue: 0xffffffff, isDefault: true, options: [{ value: 0xffffffff, label: "Driver default" }, { value: 10240, label: "10 GB" }, { value: 102400, label: "100 GB" }, { value: 0, label: "Unlimited" }], min: null, max: null, unit: null },
  ];

  let overclock: NvidiaOverclock = {
    available: true, coreMHz: 225, coreMin: -500, coreMax: 300, memoryMHz: 0, memoryMin: -1000, memoryMax: 1500,
    canSet: true, reason: null, applyAtStart: false, notice: null,
  };
  let power: NvidiaPower = { powerW: 28, limitW: 285, defaultLimitW: 285, minLimitW: 150, maxLimitW: 300, canSet: false, reason: "Run RigControl as administrator to change the power limit." };

  const displays: Display[] = [
    {
      id: "display-1",
      name: "LG UltraGear+",
      primary: true,
      width: 2560,
      height: 1440,
      refreshHz: 240,
      modes: [
        ...[240, 165, 144, 120, 60].map((hz) => ({ width: 2560, height: 1440, refreshHz: hz })),
        ...[240, 144, 60].map((hz) => ({ width: 1920, height: 1080, refreshHz: hz })),
      ],
      vibrance: { value: 50, min: 0, max: 100, defaultValue: 50 },
    },
  ];

  let lighting: LightingStatus = {
    openRgb: { installed: false, path: null, running: false, connected: false, port: 6742, version: null, message: null },
    devices: [],
    conflicts: [],
  };
  const scenes: Scene[] = [];

  // ---- health and first-time setup
  let healthScannedAt = new Date().toISOString();
  let healthFindings: HealthFinding[] = [
    { id: "cooling.nzxt-cam", module: "cooling", name: "NZXT CAM", kind: "process", severity: "blocks", verified: true, fix: null, evidence: "NZXT CAM.exe is running.",
      whatItBreaks: "While NZXT CAM is open, RigControl can read your cooler but can't change its pump, fans or screen.",
      whatToDo: "Close NZXT CAM yourself when you want RigControl to run the cooler. RigControl never closes it for you." },
    { id: "sensors.armoury-crate", module: "sensors", name: "Armoury Crate", kind: "service", severity: "warn", verified: true,
      fix: { label: "Close Armoury Crate", method: "sensors.closeArmouryCrate", args: null,
        confirm: "Close Armoury Crate and stop its background services? It can be opened again from the Start menu." },
      whatItBreaks: "Armoury Crate also sets the motherboard fans, so fan speeds you choose here may be changed back.",
      whatToDo: "Close Armoury Crate, or leave the motherboard fans on automatic in RigControl." },
    { id: "lighting.dynamic-lighting", module: "lighting", name: "Windows Dynamic Lighting", kind: "setting", severity: "warn", verified: true, fix: null,
      whatItBreaks: "Windows is also setting the colour of your lights, so they may flicker or change back.",
      whatToDo: "In Windows Settings open Personalisation, then Dynamic Lighting, and turn off \"Use Dynamic Lighting on my devices\"." },
    { id: "nvidia.afterburner", module: "nvidia", name: "MSI Afterburner", kind: "process", severity: "warn", verified: false, fix: null,
      whatItBreaks: "Afterburner sets the same graphics card clocks and fan speeds, so the last program to apply its settings wins.",
      whatToDo: "Use one of the two for overclocking. Close Afterburner, or leave the Graphics page on its defaults." },
    { id: "capture.nvidia-overlay", module: "capture", name: "NVIDIA overlay shortcuts", kind: "process", severity: "info", verified: true, fix: null,
      whatItBreaks: "The NVIDIA overlay uses Alt+F9 and Alt+F10 too, so one key press may start a recording in both.",
      whatToDo: "Change the shortcuts on the Recording page, or turn the NVIDIA overlay off in the NVIDIA app." },
    { id: "system.memory-integrity", module: "system", name: "Memory integrity is on", kind: "setting", severity: "info", verified: true, fix: null,
      whatItBreaks: "Nothing in RigControl. Some other tuning tools can't load their drivers while this Windows protection is on.",
      whatToDo: "Leave it on. RigControl never changes Windows security settings." },
  ];
  const healthReport = (): HealthReport => ({
    scannedAt: healthScannedAt,
    findings: healthFindings,
    counts: {
      blocks: healthFindings.filter((f) => f.severity === "blocks").length,
      warn: healthFindings.filter((f) => f.severity === "warn").length,
      info: healthFindings.filter((f) => f.severity === "info").length,
    },
    system: { elevated: false, fastStartup: true, memoryIntegrity: true, driverBlocklist: true, controlledFolderAccess: null, pendingRestart: false,
      gameBarRecording: true, gameBarBackgroundRecording: false, gameBarLogoButton: true, gameMode: true, dynamicLighting: true,
      windowsUpdateDrivers: true, sessionCount: 1, otherSessionRunsRigControl: false },
  });
  // The setup shows once per browser in the preview; "Go through first-time setup again" on the Health page reopens it.
  let firstRunDone = false;
  try {
    firstRunDone = window.localStorage.getItem("rigcontrol.mock.firstRunDone") === "1";
  } catch {
    // storage unavailable
  }
  let firstRunAutostart = false;
  // The preview pretends to run as administrator, so "Install and continue" can be tried; it installs nothing.
  let helpersInstalled = false;
  const firstRun = (): FirstRunInfo => ({
    done: firstRunDone,
    elevated: true,
    autostart: firstRunAutostart,
    restartSuggested: false,
    restartForAutostart: false,
    restartForEqualiser: false,
    otherUsersSignedIn: 0,
    accountIsAdministrator: true,
    otherAccount: false,
    programFolderOpen: true,
    hardware: [
      { label: "Processor", value: "Intel Core i7-13700K" },
      { label: "Motherboard", value: "ASUS ROG STRIX Z790-E GAMING WIFI (BIOS 2301)" },
      { label: "Graphics card", value: "NVIDIA GeForce RTX 4070 Ti" },
      { label: "Memory", value: "32 GB" },
      { label: "Windows", value: "Windows 11 24H2 (build 26100)" },
      { label: "NZXT device", value: "NZXT Kraken Elite" },
      { label: "Discord", value: "Running" },
    ],
    features: [
      { id: "sensors", name: "Temperatures and motherboard fans", available: true, reason: null },
      { id: "cooling", name: "NZXT cooler: pump, fans and screen", available: true, reason: null },
      { id: "nvidia", name: "NVIDIA graphics: driver settings, displays and overclocking", available: true, reason: null },
      { id: "lighting", name: "Lighting through OpenRGB", available: true, reason: null },
      { id: "capture", name: "Recording and instant replay", available: true, reason: null },
      { id: "presence", name: "Discord status", available: true, reason: null },
      { id: "cpu", name: "Processor: details and Windows power settings", available: true, reason: null },
      { id: "bios", name: "Processor settings in the BIOS", available: false, reason: "Only for the ASUS board and BIOS version it was made for." },
      { id: "gcc", name: "Taking the lights over from GIGABYTE Control Center", available: false, reason: "GIGABYTE Control Center is not installed." },
      { id: "richpresence", name: "Copying settings from RichPresence", available: false, reason: "RichPresence is not installed." },
    ],
    helpers: {
      items: [
        { id: "openrgb", name: "OpenRGB", what: "Sets the colours of your RGB lights. RigControl keeps its own copy in its Program Files folder.",
          licence: "GPL-2.0-only (its Qt files: LGPL-3.0)", carried: true, installed: helpersInstalled },
        { id: "pawnio", name: "PawnIO", what: "A signed Windows driver that RigControl needs to read temperatures and control fans.", licence: "GPL-2.0 or later, with PawnIO's own exception (see pawnio.eu)",
          carried: true, installed: true },
        { id: "equalizerapo", name: "Equalizer APO",
          what: "Puts RigControl's audio equaliser on your sound output. Windows needs one restart after it is installed.", licence: "GPL-2.0",
          carried: true, installed: helpersInstalled },
      ],
      consented: helpersInstalled,
      needsYes: !helpersInstalled,
      pending: !helpersInstalled,
      serviceWillMove: !helpersInstalled,
      notCarried: [],
      problems: [],
    },
    findings: healthFindings,
  });

  const ok = <T,>(v: T) => new Promise<T>((r) => window.setTimeout(() => r(structuredClone(v)), 120));

  return {
    subscribe(emit: (event: string, data: unknown) => void) {
      window.setInterval(() => {
        liquid = Math.round((liquid + (Math.random() - 0.5) * 0.2) * 10) / 10;
        emit("sensors.snapshot", snapshot());
        emit("cooling.status", coolingStatus());
      }, 1000);
    },

    async call(method: string, params: any): Promise<unknown> {
      // An add-on answers for its own methods first.
      const extra = extrasMock(method, params);
      if (extra !== undefined) return ok(extra);
      switch (method) {
        case "app.getInfo":
          return ok({ version: "1.0.0", isElevated: false, dataDirectory: "C:\\Users\\you\\AppData\\Local\\RigControl", logDirectory: "…\\logs", machineName: "MY-PC" });
        case "app.getModules":
          return ok(modules);
        case "app.getSettings":
          return ok({ ...settings });
        case "app.setSettings":
          settings = { ...settings, ...params };
          return ok({ ...settings });
        case "app.restartAsAdmin":
          throw new RpcError("cancelled", "Administrator permission was not granted.");
        case "app.openExternal":
          window.open(params.url, "_blank", "noopener");
          return ok({ opened: true });
        case "app.openLogFolder":
          return ok({ opened: true });
        case "app.getLogs":
          return ok([
            { time: new Date().toISOString(), level: "info", source: "app", message: "RigControl 1.0.0 starting. Elevated=False." },
            { time: new Date().toISOString(), level: "warn", source: "cooling", message: "NZXT CAM is running; cooling stays read-only." },
          ]);
        case "app.setNativeView":
          return ok({ view: params?.view ?? null });
        case "app.getAudioStatus":
          return ok({ state: "running", message: null });
        case "app.pickFile":
          return ok({ path: "C:\\Games\\Example\\Game.exe" });

        case "sensors.getSnapshot":
          return ok(snapshot());
        case "sensors.getArmouryCrate":
          return ok({ installed: true, running: ["Armoury Crate helper", "ASUS framework"], startsWithWindows: true });
        case "sensors.closeArmouryCrate":
          healthFindings = healthFindings.filter((f) => f.id !== "sensors.armoury-crate");
          return ok({ installed: true, running: [], startsWithWindows: !params?.disableAutoStart });
        case "sensors.setArmouryCrateAutoStart":
          return ok({ installed: true, running: [], startsWithWindows: !!params?.enabled });
        case "sensors.getGpuFans":
          return ok([{ id: "/gpu-nvidia/0/control/1", name: "Graphics card fans", rpmSensorId: null, mode: "auto", dutyPercent: 0, curve: null, minDutyPercent: 30, canControl: true, reason: null }]);
        case "sensors.getFanControls":
          throw new RpcError("needsAdmin", "Run RigControl as administrator to control motherboard fans.");
        case "sensors.getTemperatureSources":
          return ok(snapshot().temperatures.map(({ id, name, hardware }) => ({ id, name, hardware })));
        case "sensors.setFanFixed":
        case "sensors.setFanCurve":
        case "sensors.setFanAuto":
          return ok(fanControls[0]);

        case "cooling.getDevices":
        case "cooling.rescan":
          return ok(coolingStatus());
        case "cooling.setCamAutoStart":
          camAutoStart = !!params?.enabled;
          return ok(coolingStatus());
        case "cooling.closeCam":
          if (params?.disableAutoStart) camAutoStart = false;
          blockedBy.length = 0;
          modules[2] = { ...modules[2]!, state: "ready", message: null };
          return ok(coolingStatus());
        case "cooling.setCurve": {
          if (blockedBy.length) throw new RpcError("unavailable", "NZXT CAM is running.");
          const ch = kraken.channels.find((c) => c.id === params.channel)!;
          ch.mode = "curve";
          ch.curve = params.points;
          ch.dutyPercent = evaluateCurve(params.points, liquid);
          return ok(coolingStatus().devices[0]);
        }
        case "cooling.setFixed": {
          if (blockedBy.length) throw new RpcError("unavailable", "NZXT CAM is running.");
          const dev = [kraken, controller].find((d) => d.id === params.deviceId)!;
          const ch = dev.channels.find((c) => c.id === params.channel)!;
          if (params.dutyPercent < ch.minDutyPercent) throw new RpcError("unsafe", `${ch.name} must stay at ${ch.minDutyPercent}% or more.`);
          ch.mode = "fixed";
          ch.fixedDutyPercent = params.dutyPercent;
          ch.dutyPercent = params.dutyPercent;
          return ok(coolingStatus().devices.find((d) => d.id === dev.id));
        }
        case "cooling.setLcdScreen": {
          if (blockedBy.length) throw new RpcError("unavailable", "NZXT CAM is running.");
          const s = kraken.screen!;
          kraken.screen = {
            ...s,
            mode: params.mode ?? s.mode,
            layout: params.layout ?? s.layout,
            primary: params.primary ?? s.primary,
            secondary: params.secondary ?? s.secondary,
            accent: params.accent ?? s.accent,
            mediaName: params.path ? String(params.path).split("\\").pop()! : s.mediaName,
            tertiary: params.tertiary ?? s.tertiary,
            backgroundName: params.background === undefined ? s.backgroundName : params.background ? String(params.background).split("\\").pop()! : null,
            url: params.url ?? s.url,
            live: ["infographic", "clock", "visualizer", "web"].includes(params.mode ?? s.mode),
          };
          return ok(coolingStatus().devices[0]);
        }
        case "cooling.getLcdPreview": {
          const accent = params?.accent ?? kraken.screen!.accent;
          const two = (params?.layout ?? kraken.screen!.layout) === "dual";
          const text = two ? "62 | 48" : "62";
          const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 640"><rect width="640" height="640" fill="#000"/><circle cx="320" cy="320" r="270" fill="none" stroke="${accent}" stroke-width="30"/><text x="320" y="370" font-family="Segoe UI" font-size="${two ? 110 : 190}" font-weight="700" fill="#fff" text-anchor="middle">${text}</text></svg>`;
          return ok({ image: "data:image/svg+xml;utf8," + encodeURIComponent(svg) });
        }
        case "cooling.setLcd":
          kraken.lcd = { ...kraken.lcd!, ...("brightness" in params ? { brightness: params.brightness } : {}), ...("orientation" in params ? { orientation: params.orientation } : {}) };
          return ok(coolingStatus().devices[0]);

        case "nvidia.getInfo":
          return ok({
            available: true,
            driverVersion: "621.24",
            driverBranch: "r620_00",
            gpus: [{ index: 0, name: "NVIDIA GeForce RTX 4070 Ti", vramMB: 12282, busId: "1:0", chip: "AD104", family: "GeForce RTX 40 series", architecture: "Ada Lovelace" }],
            limitations: [
              "Recording, instant replay and the in-game overlay (ShadowPlay)",
              "Per-game optimal settings and DLSS overrides",
              "RTX Video and RTX HDR for browser video",
            ],
          });
        case "nvidia.getPower":
          return ok(power);
        case "nvidia.setPowerLimit":
          power = { ...power, limitW: params.watts };
          return ok(power);
        case "lighting.getLayout":
        case "lighting.setLayout":
          return ok({
            perLed: true,
            zones: [
              { key: "kb", device: "Razer Blackwidow V4 75%", zone: 0, name: "Keyboard", leds: 12, matrixWidth: 6, matrixHeight: 2, matrix: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], connected: true,
                placed: { key: "kb", zone: 0, x: 0.3, y: 0.6, w: 0.4, h: 0.25, reverse: false } },
              { key: "fan", device: "NZXT RGB & Fan Controller", zone: 1, name: "Hue 2 Channel 2", leds: 20, matrixWidth: 0, matrixHeight: 0, matrix: null, connected: true,
                placed: { key: "fan", zone: 1, x: 0.05, y: 0.05, w: 0.9, h: 0.06, reverse: false } },
              { key: "nanoleaf|Lines|S1", device: "Lines (Nanoleaf)", zone: -1, name: "Whole device", leds: 6, matrixWidth: 0, matrixHeight: 0, matrix: null, connected: true,
                placed: { key: "nanoleaf|Lines|S1", zone: -1, x: 0.62, y: 0.15, w: 0.3, h: 0.4, reverse: false },
                segments: [[0, 1, 0.25, 0.55], [0.25, 0.55, 0.5, 1], [0.5, 1, 0.75, 0.55], [0.75, 0.55, 1, 1], [0.25, 0.55, 0.5, 0.1], [0.5, 0.1, 0.75, 0.55]] },
              { key: "gpu", device: "Gigabyte GeForce RTX 4070 Ti Eagle OC", zone: -1, name: "Whole device", leds: 5, matrixWidth: 0, matrixHeight: 0, matrix: null, connected: true, placed: null },
              { key: "gpu", device: "Gigabyte GeForce RTX 4070 Ti Eagle OC", zone: 0, name: "GPU zone 1", leds: 1, matrixWidth: 0, matrixHeight: 0, matrix: null, connected: true, placed: null },
              { key: "gpu", device: "Gigabyte GeForce RTX 4070 Ti Eagle OC", zone: 1, name: "GPU zone 2", leds: 1, matrixWidth: 0, matrixHeight: 0, matrix: null, connected: true, placed: null },
            ],
          });
        case "lighting.getSyncPreview": {
          let px = "";
          for (let y = 0; y < 27; y++) for (let x = 0; x < 48; x++) px += x < 24 ? (y < 13 ? "E0402A" : "2A6FE0") : y < 13 ? "30C070" : "F0B44C";
          return ok({ width: 48, height: 27, pixels: px });
        }
        case "health.getReport":
          return ok(healthReport());
        case "health.rescan":
          healthScannedAt = new Date().toISOString();
          return ok(healthReport());
        case "app.getFirstRun":
          return ok(firstRun());
        case "app.installHelpers": {
          const equaliser = !helpersInstalled;
          helpersInstalled = true;
          return ok({ ...firstRun(), restartSuggested: equaliser, restartForEqualiser: equaliser });
        }
        case "app.quit":
          return ok({ quitting: true }); // the preview stays open
        case "app.completeFirstRun":
          firstRunAutostart = !!params?.autostart;
          firstRunDone = true;
          try {
            window.localStorage.setItem("rigcontrol.mock.firstRunDone", "1");
          } catch {
            // storage unavailable
          }
          return ok({ ...firstRun(), restartSuggested: firstRunAutostart, restartForAutostart: firstRunAutostart });
        case "app.restartPc":
          return ok({ scheduled: true, seconds: 30 }); // the preview never restarts anything
        case "app.cancelRestartPc":
          return ok({ cancelled: true });
        case "app.getBundled":
          return ok({
            status: { openRgbCarried: true, openRgbPath: helpersInstalled ? "C:\\Program Files\\RigControl\\OpenRGB\\OpenRGB.exe" : null,
              openRgbServiceUsesIt: helpersInstalled, pawnIoCarried: true, pawnIoInstalled: true, equalizerApoCarried: true,
              equalizerApoInstalled: helpersInstalled, notes: [] },
            helpers: firstRun().helpers,
            oldApps: [{ id: "gcc", name: "GIGABYTE Control Center", version: "26.10.21.01", why: "RigControl runs the graphics card's lights and fans." }],
            elevated: true,
          });
        case "app.uninstallOldApp":
          return ok({ started: "GIGABYTE Control Center" });
        case "lighting.getGcc":
          return ok({ installed: true, running: true, startsWithWindows: true, disabledByRigControl: false });
        case "lighting.takeOverGcc":
        case "lighting.giveBackGcc":
          return ok({ gcc: { installed: true, running: false, startsWithWindows: false, disabledByRigControl: method === "lighting.takeOverGcc" }, status: null });
        case "lighting.getNanoleaf":
        case "lighting.forgetNanoleaf":
          return ok({ paired: false, ip: null, name: null, panels: 0, streaming: false, problem: null, found: null });
        case "lighting.findNanoleaf":
          return ok({ paired: false, ip: null, name: null, panels: 0, streaming: false, problem: null, found: ["192.0.2.61"] });
        case "lighting.pairNanoleaf":
          return ok({ paired: true, ip: "192.0.2.61", name: "Lines 5A3B", panels: 9, streaming: false, problem: null, found: null });
        case "lighting.getSync":
        case "lighting.setSync":
        case "lighting.listSyncSources":
          return ok({ enabled: true, source: "", sourceName: null, brightness: 100, status: "Following the screen", color: "#3A7BFF",
            sources: [{ id: "usb1", name: "USB Video", present: true }], captureSize: null, vivid: true, bass: true, bassStrength: 70, bassProblem: null, treble: true, trebleStrength: 50, audioGain: 100, mid: true, midStrength: 50,
            effect: "wave",
            effects: [
              { id: "screen", name: "Screen ambience", description: "Every light takes the colours of your screen or a capture card.", group: "Screen", audio: false, uses: [] },
              { id: "solid", name: "Solid colour", description: "One steady colour everywhere.", group: "Simple", audio: false, uses: ["color1"] },
              { id: "breathing", name: "Breathing", description: "A colour that slowly fades in and out.", group: "Simple", audio: false, uses: ["color1", "speed"] },
              { id: "cycle", name: "Colour cycle", description: "Every light moves through the whole colour wheel together.", group: "Simple", audio: false, uses: ["speed"] },
              { id: "wave", name: "Rainbow wave", description: "A rainbow that rolls across your setup.", group: "Animated", audio: false, uses: ["speed", "size", "direction"] },
              { id: "spiral", name: "Rainbow spiral", description: "A rainbow turning round the middle.", group: "Animated", audio: false, uses: ["speed", "size"] },
              { id: "gradient", name: "Gradient sweep", description: "Two colours flowing into each other.", group: "Animated", audio: false, uses: ["color1", "color2", "speed", "size", "direction"] },
              { id: "stars", name: "Starfield", description: "Stars twinkling on a background colour.", group: "Animated", audio: false, uses: ["color1", "color2", "speed", "size"] },
              { id: "rain", name: "Colour rain", description: "Drops of colour falling down.", group: "Animated", audio: false, uses: ["color1", "color2", "speed", "size"] },
              { id: "visualizer", name: "Audio visualiser", description: "Bars that jump with the bass, mids and treble of what the PC plays.", group: "Audio", audio: true, uses: ["color1", "color2"] },
              { id: "ripple", name: "Bass ripple", description: "Every bass hit sends a ring of colour out from the middle.", group: "Audio", audio: true, uses: ["color1", "speed", "size"] },
              { id: "audiocolor", name: "Sound colour", description: "The colour follows the music: bass is red, mids green, treble blue.", group: "Audio", audio: true, uses: [] },
            ],
            effectParams: Object.fromEntries(["screen", "solid", "breathing", "cycle", "wave", "spiral", "gradient", "stars", "rain", "visualizer", "ripple", "audiocolor"]
              .map((id) => [id, { speed: 50, color1: "#FF0040", color2: "#0040FF", direction: "right", size: 50 }])) });
        case "presence.getStatus":
        case "presence.setSettings":
        case "presence.setGameEnabled":
        case "presence.rescan":
          return ok({
            enabled: true, discordRunning: true, blocked: "RichPresence is running. Close it to let RigControl show your Discord status.",
            status: "Paused", playing: null, music: { player: "Spotify", title: "Song", artist: "Band", album: "", playing: true }, lyricLine: "a lyric line", app: null,
            scanning: false, scannedAt: new Date().toISOString(),
            games: [
              { id: "steam:223850", name: "3DMark", store: "Steam", matched: true, enabled: true, running: false, hasArt: false, canPlay: true },
              { id: "folder:genshin", name: "Genshin Impact", store: "Folder", matched: true, enabled: true, running: true, hasArt: false, canPlay: true },
            ],
            settings: { showMusic: true, showSpotify: true, showLyrics: true, showAlbumArt: true, showCurrentApp: false, officialToDiscord: true, officialDelayMinutes: 2, genshinUid: "", extraFolders: [],
              showWatching: true, showWatchTitle: true, showWatchPoster: true, showWatchCasual: true, customMode: "separate",
              customStatuses: [{ id: "a1", enabled: true, type: 0, name: "Building RigControl", details: "C# and React", state: "", large_image: "", large_text: "", small_image: "", small_text: "", button1_label: "", button1_url: "", button2_label: "", button2_url: "", elapsed: true, app_id: "" }] },
            importedFromRichPresence: new Date().toISOString(),
            watching: "Netflix - Stranger Things", customShown: 1, customSkipped: null, customErrors: { a1: "Invalid asset" }, extensionLinked: true,
            attachedUser: { id: "123456789012345678", name: "Alex" }, discordState: "attached", discordReason: null, attachNote: null, extensionPort: 47610,
            setUp: true, setupNote: null, ownAppIds: [],
          });
        case "overlay.getAudio":
        case "overlay.setVolume":
          return ok({ master: 62, masterMuted: false, apps: [{ pid: 1, name: "Spotify", volume: 80, muted: false }, { pid: 2, name: "Discord", volume: 100, muted: false }, { pid: 3, name: "Fortnite", volume: 55, muted: true }] });
        case "overlay.getState":
        case "overlay.openDiscord":
        case "overlay.closeDiscord":
        case "overlay.forgetDiscord":
          return ok({ enabled: true, hotkey: "Ctrl+Alt+G", hotkeyProblem: null, discordOpen: false, discordProblem: null, discordSignedIn: true, controller: true, gameBarButtonOff: true, pauseRemotePlay: true });
        case "overlay.mediaKey":
        case "overlay.open":
        case "overlay.phone":
        case "overlay.hide":
          return ok(null);
        case "presence.getExtension":
          return ok({ path: "C:\\Users\\you\\AppData\\Local\\RigControl\\browser-extension-1a2b3c4d" });
        case "presence.getArt":
          return ok(null);
        case "cpu.getInfo":
          return ok({
            name: "13th Gen Intel(R) Core(TM) i9-13900KF", vendor: "Intel", vendorId: "GenuineIntel", family: 6, model: 183, stepping: 1,
            cores: 24, threads: 32, performanceCores: 8, efficiencyCores: 16, known: true, generation: "13th gen Core", codeName: "Raptor Lake",
            line: "Desktop", summary: "13th gen Core (Raptor Lake), desktop", tested: true, note: null,
          });
        case "cpu.getPower":
          return ok(cpuPower);
        case "cpu.setPower": {
          const settings = cpuPower.settings.map((s) => {
            const ac = params?.ac?.[s.key];
            if (ac == null || ac === s.ac) return s;
            return { ...s, ac, originalAc: s.originalAc ?? s.ac, originalDc: s.originalDc ?? s.dc };
          });
          const changed = settings.some((s) => s.originalAc != null);
          cpuPower = { ...cpuPower, settings, changed, changedPlans: changed ? ["Balanced"] : [] };
          return ok(cpuPower);
        }
        case "cpu.resetPower":
          cpuPower = cpuPowerStock();
          return ok(cpuPower);
        case "bios.getStatus":
          return ok({
            board: "ROG STRIX Z790-A GAMING WIFI II", biosVersion: "2301", elevated: true, supported: true, problem: null,
            variableName: "OcmrSettings", variableSize: 17546, variableAttributes: "non-volatile, boot, runtime (0x7)", backupPath: null,
            cpu: { profile: "intelExtreme", pl1: 0, pl2: 0, undervoltMv: 0, allCoreRatio: 0, notes: [] }, cpuProblem: null, writable: null, writeProblem: null,
            restartNeeded: false, originalBackup: "x.bin", lastChanges: null,
            settings: [
              { name: "Ai Overclock Tuner", form: "Ai Tweaker", offset: 0, size: 1, kind: "oneof", value: 5, label: "XMP I", options: [{ value: 0, label: "Auto" }, { value: 5, label: "XMP I" }], min: 0, max: 11, help: null, valueKnown: true },
              { name: "Long Duration Package Power Limit", form: "Internal CPU Power Management", offset: 1358, size: 2, kind: "numeric", value: 253, label: null, options: null, min: 0, max: 4095, help: "Power limit 1, in watts.", valueKnown: true },
            ],
          });
        case "nvidia.getOverclock":
          return ok(overclock);
        case "nvidia.setOverclock":
          overclock = {
            ...overclock,
            coreMHz: params.coreMHz ?? overclock.coreMHz,
            memoryMHz: params.memoryMHz ?? overclock.memoryMHz,
            applyAtStart: params.applyAtStart ?? overclock.applyAtStart,
          };
          return ok(overclock);
        case "nvidia.resetOverclock":
          overclock = { ...overclock, coreMHz: 0, memoryMHz: 0 };
          return ok(overclock);
        case "nvidia.getDriverSettings":
          return ok(driverSettings);
        case "nvidia.setDriverSetting": {
          const s = driverSettings.find((x) => x.key === params.key)!;
          s.value = params.value;
          s.isDefault = s.value === s.defaultValue;
          return ok(s);
        }
        case "nvidia.resetDriverSetting": {
          const s = driverSettings.find((x) => x.key === params.key)!;
          s.value = s.defaultValue ?? s.value;
          s.isDefault = true;
          return ok(s);
        }
        case "nvidia.listProfiles": {
          const all: DriverProfile[] = [
            { name: "Fortnite", programs: ["FortniteClient-Win64-Shipping.exe"], isPredefined: true, modifiedCount: 0 },
            { name: "Counter-Strike 2", programs: ["cs2.exe"], isPredefined: true, modifiedCount: 2 },
            { name: "Roblox", programs: ["RobloxPlayerBeta.exe"], isPredefined: true, modifiedCount: 0 },
          ];
          const q = String(params?.query ?? "").toLowerCase();
          return ok(all.filter((p) => p.name.toLowerCase().includes(q) || p.programs.some((x) => x.toLowerCase().includes(q))));
        }
        case "nvidia.addProgram":
          return ok({ name: "Game", programs: ["Game.exe"], isPredefined: false, modifiedCount: 0 });
        case "nvidia.getDisplays":
          return ok(displays);
        case "nvidia.setDisplayMode":
          return ok({ applied: true, revertToken: "t1", revertInSeconds: 15 });
        case "nvidia.confirmDisplayMode":
          return ok({ kept: true });
        case "nvidia.revertDisplayMode":
          return ok({ reverted: true });
        case "nvidia.setVibrance":
          displays[0]!.vibrance!.value = params.value;
          return ok(displays[0]);
        case "drivers.getStatus":
        case "drivers.check": {
          const checked = method === "drivers.check" || driversChecked;
          driversChecked = checked;
          return ok({
            board: "ROG STRIX Z790-A GAMING WIFI II",
            checkedAt: checked ? new Date().toISOString() : null,
            checking: false,
            rows: checked
              ? [
                  { key: "lan", name: "Wired network (Intel I226)", category: "LAN", device: "Intel(R) Ethernet Controller I226-V", installedVersion: "2.1.5.10", availableVersion: "2.1.5.10", availableTitle: "Intel I226 LAN driver", released: "2026-03-31", size: "5.22 MB", fileId: "lan", state: "current", note: null },
                  { key: "wifi", name: "Wi-Fi", category: "Wireless", device: "Intel(R) Wi-Fi 7 BE202 160MHz", installedVersion: "23.80.0.7", availableVersion: "24.20.0.4", availableTitle: "Intel Wi-Fi Driver", released: "2026-05-14", size: "88.73 MB", fileId: "wifi", state: "update", note: null },
                  { key: "audio", name: "Audio (Realtek)", category: "Audio", device: "Realtek USB Audio", installedVersion: "6.4.0.2414", availableVersion: "6.3.9600.2393", availableTitle: "Realtek USB Audio Driver", released: "2025-03-04", size: "928.91 MB", fileId: "audio", state: "newer", note: "Windows Update or the maker already gave you a newer driver than ASUS offers, so nothing to do." },
                  { key: "chipset", name: "Intel chipset", category: "Chipset", device: "Intel(R) SMBus - 7A23", installedVersion: "10.1.46.5", availableVersion: "10.1.20532.8842", availableTitle: "Intel Chipset driver", released: "2026-07-02", size: "96.73 MB", fileId: "chipset", state: "unknown", note: "ASUS numbers this package differently from the version Windows shows." },
                ]
              : [],
            asusProblem: null,
            windowsUpdate: checked ? [] : null,
            windowsUpdateProblem: null,
            busy: null,
            elevated: true,
          });
        }
        case "drivers.installAsus":
        case "drivers.installWindowsUpdate":
          return ok({ started: true, message: "The installer has started.", restartNeeded: false, status: null });
        case "capture.getStatus":
        case "capture.openFolder":
          return ok(captureStatus);
        case "capture.setSettings":
          captureStatus = { ...captureStatus, settings: { ...captureStatus.settings, ...params } };
          captureStatus.replayRunning = captureStatus.settings.replayEnabled;
          return ok(captureStatus);
        case "capture.startRecording":
          captureStatus = { ...captureStatus, recording: true, recordingSince: new Date().toISOString(), recordingFile: "C:\\Users\\You\\Videos\\RigControl\\Recording.mp4" };
          return ok(captureStatus);
        case "capture.stopRecording":
          captureStatus = { ...captureStatus, recording: false, recordingSince: null, lastSaved: captureStatus.recordingFile, recordingFile: null };
          return ok(captureStatus);
        case "capture.saveReplay":
          captureStatus = { ...captureStatus, lastSaved: "C:\\Users\\You\\Videos\\RigControl\\Replay.mp4" };
          return ok(captureStatus);
        case "app.pickFolder":
          return ok({ path: "D:\\Videos" });
        case "nvidia.installDriver":
          return ok({ started: true, version: "621.24" });
        case "nvidia.checkDriverUpdate":
          return ok({ installed: "621.24", latest: "621.24", newer: false, downloadUrl: null, checkedAt: new Date().toISOString(), error: null });

        case "lighting.getStatus":
          return ok(lighting);
        case "lighting.installOpenRgb":
          lighting = { ...lighting, openRgb: { ...lighting.openRgb, installed: true, path: "C:\\Program Files\\OpenRGB\\OpenRGB.exe", version: "1.0" } };
          return ok({ started: true, message: "The OpenRGB download page is open in your browser. Install it, then press Start OpenRGB here." });
        case "lighting.startOpenRgb":
          lighting = {
            openRgb: { ...lighting.openRgb, running: true, connected: true },
            conflicts: ["Armoury Crate"],
            devices: [
              { id: 0, name: "ASUS ROG STRIX Z790-A GAMING WIFI II", type: "Motherboard", vendor: "ASUS", ledCount: 12, zones: [{ id: 0, name: "Aura Mainboard", ledCount: 4 }, { id: 1, name: "Aura Addressable 1", ledCount: 8 }], modes: mockModes(), activeModeId: 1, color: "#00E5FF" },
              { id: 1, name: "Team Group T-Force Delta RGB DDR5", type: "DRAM", vendor: "ENE", ledCount: 15, zones: [{ id: 0, name: "DRAM", ledCount: 15 }], modes: mockModes(), activeModeId: 1, color: "#9B4DFF" },
              { id: 2, name: "NZXT Kraken Elite", type: "Cooler", vendor: "NZXT", ledCount: 24, zones: [{ id: 0, name: "Ring", ledCount: 24 }], modes: mockModes(), activeModeId: 1, color: "#00E5FF" },
              { id: 3, name: "Razer BlackWidow V4 75%", type: "Keyboard", vendor: "Razer", ledCount: 84, zones: [{ id: 0, name: "Keys", ledCount: 84 }], modes: mockModes(), activeModeId: 2, color: "#FF3DCB" },
            ],
          };
          return ok(lighting);
        case "lighting.setColor":
        case "lighting.setZoneColor":
        case "lighting.setMode":
        case "lighting.off":
          lighting = {
            ...lighting,
            devices: lighting.devices.map((d) =>
              params?.deviceIds == null && params?.deviceId == null ? { ...d, color: method === "lighting.off" ? "#000000" : params.color } : params?.deviceIds?.includes(d.id) || params?.deviceId === d.id ? { ...d, color: params.color ?? d.color } : d,
            ),
          };
          return ok(lighting);
        case "lighting.listScenes":
          return ok(scenes);
        case "lighting.saveScene":
          scenes.push({ name: params.name, savedAt: new Date().toISOString() });
          return ok(scenes);
        case "lighting.applyScene":
          return ok(lighting);
        case "lighting.deleteScene":
          return ok(scenes.splice(0, scenes.length, ...scenes.filter((s) => s.name !== params.name)) && scenes);
      }
      throw new RpcError("unknownMethod", `Mock has no ${method}`);
    },
  };
}

function mockModes() {
  return [
    { id: 0, name: "Off", colorCount: 0, supportsSpeed: false, supportsBrightness: false, speedMin: null, speedMax: null, brightnessMin: null, brightnessMax: null },
    { id: 1, name: "Static", colorCount: 1, supportsSpeed: false, supportsBrightness: true, speedMin: null, speedMax: null, brightnessMin: 0, brightnessMax: 100 },
    { id: 2, name: "Breathing", colorCount: 1, supportsSpeed: true, supportsBrightness: true, speedMin: 0, speedMax: 4, brightnessMin: 0, brightnessMax: 100 },
    { id: 3, name: "Spectrum cycle", colorCount: 0, supportsSpeed: true, supportsBrightness: true, speedMin: 0, speedMax: 4, brightnessMin: 0, brightnessMax: 100 },
  ];
}

// Keep the preset import used (handy when experimenting in the console).
void presetCurve;

// Mirrors docs/API.md exactly. Change both together.

export type ModuleState = "notStarted" | "starting" | "ready" | "degraded" | "failed" | "stopped";
export type ModuleInfo = { name: string; displayName: string; state: ModuleState; message: string | null };
export type CurvePoint = { tempC: number; dutyPercent: number };

// ---------- app ----------
export type AppInfo = {
  version: string;
  isElevated: boolean;
  dataDirectory: string;
  logDirectory: string;
  machineName: string;
  /** "windows", "linux" or "macos". Left out by an older app, which is always Windows. */
  platform?: string;
  /**
   * What works here: one name per module that is really there, "module.part" for its parts and "host.*" for what
   * the app itself can do. Left out by an older app, where everything works.
   */
  capabilities?: string[];
};
export type AppSettings = { minimizeToTray: boolean; startMinimized: boolean; startWithWindows: boolean; temperatureUnit?: "F" | "C" };
export type LogLine = { time: string; level: "info" | "warn" | "error"; source: string; message: string };

// ---------- sensors ----------
export type SensorSnapshot = {
  time: string;
  elevated: boolean;
  cpu: null | {
    name: string;
    packageTempC: number | null;
    coreMaxTempC: number | null;
    loadPercent: number | null;
    powerW: number | null;
    clockMHz: number | null;
  };
  gpu: null | {
    name: string;
    tempC: number | null;
    hotspotTempC: number | null;
    loadPercent: number | null;
    powerW: number | null;
    coreClockMHz: number | null;
    memoryClockMHz: number | null;
    fanPercent: number | null;
    fanRpm: number | null;
    vramUsedMB: number | null;
    vramTotalMB: number | null;
  };
  memory: { usedGB: number | null; totalGB: number | null; loadPercent: number | null };
  board: null | { name: string; superIo: string | null };
  temperatures: { id: string; name: string; hardware: string; value: number | null }[];
  fans: { id: string; name: string; hardware: string; rpm: number | null; controlId: string | null }[];
  notes: string[];
};

export type FanControl = {
  id: string;
  name: string;
  rpmSensorId: string | null;
  mode: "auto" | "fixed" | "curve";
  dutyPercent: number | null;
  curve: null | { sourceId: string; points: CurvePoint[] };
  minDutyPercent: number;
  canControl: boolean;
  reason: string | null;
};

export type TemperatureSource = { id: string; name: string; hardware: string };

// ---------- nvidia ----------
export type NvidiaInfo = {
  available: boolean;
  driverVersion: string | null;
  driverBranch: string | null;
  /** chip, family and architecture come from RigControl's built-in card list; null when it does not know the card. */
  gpus: { index: number; name: string; vramMB: number | null; busId: string | null; chip: string | null; family: string | null; architecture: string | null }[];
  limitations: string[];
};

export type NvidiaPower = {
  powerW: number | null;
  limitW: number | null;
  defaultLimitW: number | null;
  minLimitW: number | null;
  maxLimitW: number | null;
  canSet: boolean;
  reason: string | null;
};

export type BiosSetting = {
  name: string;
  form: string;
  offset: number;
  size: number;
  kind: string;
  value: number;
  label: string | null;
  options: { value: number; label: string }[] | null;
  min: number | null;
  max: number | null;
  help: string | null;
  valueKnown: boolean;
};

export type CpuEdits = {
  profile: string;
  pl1: number;
  pl2: number;
  undervoltMv: number;
  allCoreRatio: number;
  notes: string[];
};

export type BiosStatus = {
  board: string | null;
  biosVersion: string | null;
  elevated: boolean;
  supported: boolean;
  problem: string | null;
  variableName: string | null;
  variableSize: number | null;
  variableAttributes: string | null;
  backupPath: string | null;
  settings: BiosSetting[];
  cpu: CpuEdits | null;
  cpuProblem: string | null;
  writable: boolean | null;
  writeProblem: string | null;
  restartNeeded: boolean;
  originalBackup: string | null;
  lastChanges: string[] | null;
};

// ---------- cpu ----------
export type CpuInfo = {
  name: string;
  vendor: "Intel" | "AMD" | "Other";
  vendorId: string;
  family: number;
  model: number;
  stepping: number;
  cores: number;
  threads: number;
  performanceCores: number | null;
  efficiencyCores: number | null;
  known: boolean;
  generation: string | null;
  codeName: string | null;
  line: string | null;
  summary: string;
  tested: boolean;
  note: string | null;
};

export type CpuPowerKey = "boostMode" | "maxStatePercent" | "minStatePercent" | "maxFrequencyMHz" | "minCoresPercent";

export type CpuPowerSetting = {
  key: CpuPowerKey;
  available: boolean;
  ac: number | null;
  dc: number | null;
  min: number;
  max: number;
  zeroMeansNoLimit: boolean;
  unit: string;
  options: { value: number; label: string }[] | null;
  coreTypesDiffer: boolean;
  originalAc: number | null;
  originalDc: number | null;
};

export type CpuPower = {
  available: boolean;
  problem: string | null;
  planId: string | null;
  planName: string | null;
  hasBattery: boolean;
  changed: boolean;
  changedPlans: string[];
  settings: CpuPowerSetting[];
};

export type NvidiaOverclock = {
  available: boolean;
  coreMHz: number | null;
  coreMin: number;
  coreMax: number;
  memoryMHz: number | null;
  memoryMin: number;
  memoryMax: number;
  canSet: boolean;
  reason: string | null;
  applyAtStart: boolean;
  notice: string | null;
};

export type DriverSetting = {
  key: string;
  name: string;
  group: string;
  description: string;
  kind: "enum" | "number" | "toggle";
  value: number;
  defaultValue: number | null;
  isDefault: boolean;
  options: { value: number; label: string }[] | null;
  min: number | null;
  max: number | null;
  unit: string | null;
};

export type DriverProfile = { name: string; programs: string[]; isPredefined: boolean; modifiedCount: number };

export type DisplayMode = { width: number; height: number; refreshHz: number };
export type Display = {
  id: string;
  name: string;
  primary: boolean;
  width: number;
  height: number;
  refreshHz: number;
  modes: DisplayMode[];
  vibrance: null | { value: number; min: number; max: number; defaultValue: number };
};

export type DriverUpdate = {
  installed: string | null;
  latest: string | null;
  newer: boolean | null;
  downloadUrl: string | null;
  checkedAt: string | null;
  error: string | null;
  canInstall?: boolean;
};

export type DriverDownloadProgress = { version: string; receivedMB: number; totalMB: number | null; percent: number | null };

// ---------- drivers ----------
export type DriverRow = {
  key: string;
  name: string;
  category: string;
  device: string | null;
  installedVersion: string | null;
  availableVersion: string | null;
  availableTitle: string | null;
  released: string | null;
  size: string | null;
  fileId: string | null;
  state: "update" | "current" | "newer" | "missing" | "unknown";
  note: string | null;
};

export type WindowsUpdateDriver = { id: string; title: string; driverClass: string | null; date: string | null; sizeMB: number; needsLicense: boolean };

export type DriversStatus = {
  board: string | null;
  checkedAt: string | null;
  checking: boolean;
  rows: DriverRow[];
  asusProblem: string | null;
  windowsUpdate: WindowsUpdateDriver[] | null;
  windowsUpdateProblem: string | null;
  busy: string | null;
  elevated: boolean;
};

export type DriverAction = { started: boolean; message: string; restartNeeded: boolean; status: DriversStatus };
export type DriverDownload = { name: string; receivedMB: number; totalMB: number | null; percent: number | null };

// ---------- capture ----------
export type CaptureSettings = {
  folder: string;
  fps: number;
  bitrateMbps: number;
  codec: string;
  display: number;
  systemAudio: boolean;
  replayEnabled: boolean;
  replaySeconds: number;
  overlayStats: boolean;
  recordHotkey: string;
  replayHotkey: string;
  overlayHotkey: string;
};

export type CaptureStatus = {
  available: boolean;
  problem: string | null;
  recording: boolean;
  recordingSince: string | null;
  recordingFile: string | null;
  replayRunning: boolean;
  lastSaved: string | null;
  hotkeyProblems: string[];
  settings: CaptureSettings;
};

// ---------- cooling ----------
export type CoolingChannel = {
  id: string;
  name: string;
  kind: "pump" | "fan";
  rpm: number | null;
  dutyPercent: number | null;
  minDutyPercent: number;
  curveTempRange: { min: number; max: number } | null;
  maxCurvePoints: number | null;
  curveSource: "liquid" | null;
  storedOnDevice: boolean;
  mode: "unknown" | "fixed" | "curve";
  fixedDutyPercent: number | null;
  curve: CurvePoint[] | null;
};

export type CoolingDevice = {
  id: string;
  name: string;
  product: string;
  firmware: string | null;
  connected: boolean;
  liquidTempC: number | null;
  channels: CoolingChannel[];
  lcd: null | { brightness: number; orientation: number };
  notes: string[];
  screen?: LcdScreen | null;
};

/** ASUS Armoury Crate, whose background parts also drive the motherboard's fans and lights. */
export type ArmouryState = { installed: boolean; running: string[]; startsWithWindows: boolean };

export type LcdScreenMode = "unset" | "liquid" | "infographic" | "media" | "clock" | "visualizer" | "web";

/** What RigControl is showing on the cooler's screen. */
export type LcdScreen = {
  mode: LcdScreenMode;
  layout: string;
  primary: string;
  secondary: string;
  accent: string;
  mediaName: string | null;
  live: boolean;
  problem: string | null;
  layouts: string[];
  sources: string[];
  tertiary?: string;
  backgroundName?: string | null;
  url?: string | null;
  modes?: string[];
  textFollowsLights?: boolean;
};

export type CamState = { appRunning: boolean; serviceRunning: boolean; startsWithWindows: boolean };
export type CoolingStatus = { blockedBy: string[]; devices: CoolingDevice[]; cam: CamState };

// ---------- lighting ----------
export type LightMode = {
  id: number;
  name: string;
  colorCount: number;
  supportsSpeed: boolean;
  supportsBrightness: boolean;
  speedMin: number | null;
  speedMax: number | null;
  brightnessMin: number | null;
  brightnessMax: number | null;
};

export type LightDevice = {
  id: number;
  name: string;
  type: string;
  vendor: string;
  ledCount: number;
  /** `resizable`: the user may set how many lights hang on this header (lighting.setZoneSize), within ledsMin..ledsMax. */
  zones: { id: number; name: string; ledCount: number; ledsMin: number; ledsMax: number; resizable: boolean }[];
  modes: LightMode[];
  activeModeId: number;
  color: string | null;
  leftOut?: boolean;
};

export type LightingStatus = {
  openRgb: {
    installed: boolean;
    path: string | null;
    running: boolean;
    connected: boolean;
    port: number;
    version: string | null;
    message: string | null;
  };
  devices: LightDevice[];
  conflicts: string[];
};

export type Scene = { name: string; savedAt: string };

// ---------- events ----------
export type PresenceGame = {
  id: string;
  name: string;
  store: string;
  matched: boolean;
  enabled: boolean;
  running: boolean;
  hasArt: boolean;
  canPlay: boolean;
};

export type CustomStatus = {
  id: string;
  enabled: boolean;
  type: number;
  name: string;
  details: string;
  state: string;
  large_image: string;
  large_text: string;
  small_image: string;
  small_text: string;
  button1_label: string;
  button1_url: string;
  button2_label: string;
  button2_url: string;
  elapsed: boolean;
  app_id: string;
};

export type PresenceStatus = {
  enabled: boolean;
  discordRunning: boolean;
  blocked: string | null;
  status: string;
  playing: string | null;
  music: { player: string; title: string; artist: string; album: string; playing: boolean } | null;
  lyricLine: string | null;
  app: string | null;
  scanning: boolean;
  scannedAt: string | null;
  games: PresenceGame[];
  settings: {
    showMusic: boolean;
    showSpotify: boolean;
    showLyrics: boolean;
    showAlbumArt: boolean;
    showCurrentApp: boolean;
    officialToDiscord: boolean;
    officialDelayMinutes: number;
    genshinUid: string;
    extraFolders: string[];
    showWatching: boolean;
    showWatchTitle: boolean;
    showWatchPoster: boolean;
    showWatchCasual: boolean;
    customStatuses: CustomStatus[];
    customMode: string;
  };
  importedFromRichPresence: string | null;
  watching: string | null;
  customShown: number;
  customSkipped: string[] | null;
  customErrors: Record<string, string> | null;
  extensionLinked: boolean;
  /** The Discord account this Windows user's own Discord is signed in to, once Discord has answered. Can be set while the status is off. */
  attachedUser: { id: string; name: string } | null;
  /** "found": this user's Discord is running but has not said which account yet. */
  discordState: "attached" | "found" | "notRunning" | "otherUser" | "noAnswer";
  /** Why nothing is attached, in plain words; null when attached or found. */
  discordReason: string | null;
  /** Shown for a few minutes after Discord switched to another account. */
  attachNote: string | null;
  /** The port the browser extension link listens on (47610 to 47619), or null when it is not listening. */
  extensionPort: number | null;
  /** False when this build carries no Discord application ids and the user has added none of their own. */
  setUp: boolean;
  /** Plain words on what is missing for Discord status; null when nothing is. */
  setupNote: string | null;
  /** Card names the user gave their own Discord application id for (never the ids themselves). */
  ownAppIds: string[];
};

export type PanelAudio = {
  master: number;
  masterMuted: boolean;
  apps: { pid: number; name: string; volume: number; muted: boolean }[];
};

export type PanelState = {
  enabled: boolean;
  hotkey: string;
  hotkeyProblem: string | null;
  discordOpen: boolean;
  discordProblem: string | null;
  discordSignedIn: boolean;
  controller: boolean;
  gameBarButtonOff: boolean;
  pauseRemotePlay: boolean;
};

export type LightSync = {
  enabled: boolean;
  source: string;
  sourceName: string | null;
  brightness: number;
  status: string;
  color: string | null;
  sources: { id: string; name: string; present: boolean }[];
  captureSize: string | null;
  vivid: boolean;
  bass: boolean;
  bassStrength: number;
  bassProblem: string | null;
  treble: boolean;
  trebleStrength: number;
  audioGain: number;
  mid: boolean;
  midStrength: number;
  effect: string;
  effects: EffectInfo[] | null;
  effectParams: Record<string, EffectParams> | null;
};

/** One brand of network light this build can drive. `tested` false = built but not tried on real hardware. */
export type NetworkBrand = {
  brand: string;
  name: string;
  tested: boolean;
  notes: string;
  steps: string[];
  returnsSecret: boolean;
  manualAddress: boolean;
  options: { key: string; label: string; choices: { value: string; label: string }[]; default: string | null }[];
  nothingFound: string | null;
  maxFramesPerSecond: number;
  /** False = this brand can't be searched for; the user types the light's address. */
  canSearch: boolean;
  /** False = a paired light of this brand is left alone until it is placed on the layout. */
  followsUnplaced: boolean;
};

/** A paired network light. `id` is also its key on the layout. */
export type NetworkLight = {
  id: string;
  brand: string;
  brandName: string;
  deviceId: string;
  name: string;
  model: string;
  address: string;
  tested: boolean;
  kind: "single" | "strip" | "points";
  points: number;
  pointName: string;
  placed: boolean;
  streaming: boolean;
  problem: string | null;
  note: string | null;
  /** What it can be told directly; each with a plain reason when it can't. Null for a brand this version doesn't know. */
  controls: NetworkLightControls | null;
  /** As last read from the light; null when not known (or the brand can't say). */
  on: boolean | null;
  /** 0-100, as last read. */
  brightness: number | null;
  /** "#RRGGBB" at full strength, as last read; null for an effect, a white, or not known. */
  color: string | null;
  /** True when RigControl changed it by hand and remembers what it did before (Give back puts that back). */
  takenOver: boolean;
  /** False for a brand left out of "Everything at once" and "Turn all lights off" (a Hue Bridge). */
  joinsEverything: boolean;
  /** False for a brand the effects leave alone until it is placed on the layout (a Hue Bridge). */
  followsUnplaced: boolean;
};

export type LightAbility = { can: boolean; why: string | null };

export type NetworkLightControls = {
  power: LightAbility;
  brightness: LightAbility;
  colour: LightAbility;
  readBack: LightAbility;
  giveBack: LightAbility;
};

export type FoundLight = {
  brand: string;
  brandName: string;
  deviceId: string;
  name: string;
  model: string;
  address: string;
  paired: boolean;
};

export type NetworkLights = {
  brands: NetworkBrand[];
  paired: NetworkLight[];
  found: FoundLight[];
  scanning: boolean;
  scanProblems: string[];
  notSupported: { brand: string; name: string; why: string }[];
  /** True when the last search was stopped before every brand had finished. */
  scanStopped: boolean;
  /** The brands whose search ran to its end in the last search. */
  scanFinished: string[];
};

export type EffectInfo = { id: string; name: string; description: string; group: string; audio: boolean; uses: string[] };

export type EffectParams = { speed: number; color1: string; color2: string; direction: string; size: number };

export type LayoutItem = { key: string; zone: number; x: number; y: number; w: number; h: number; reverse: boolean };

export type LayoutZone = {
  key: string;
  device: string;
  zone: number;
  name: string;
  leds: number;
  matrixWidth: number;
  matrixHeight: number;
  matrix: number[] | null;
  connected: boolean;
  placed: LayoutItem | null;
  /** Lights drawn as bars (Nanoleaf Lines): each bar's two ends, [x1, y1, x2, y2] in 0-1 across the box. */
  segments?: number[][] | null;
};

export type LayoutState = { perLed: boolean; zones: LayoutZone[] };

export type SyncPreview = { width: number; height: number; pixels: string };

export type BundledInfo = {
  status: {
    openRgbCarried: boolean;
    openRgbPath: string | null;
    openRgbServiceUsesIt: boolean;
    pawnIoCarried: boolean;
    pawnIoInstalled: boolean;
    equalizerApoCarried: boolean;
    equalizerApoInstalled: boolean;
    notes: string[];
  } | null;
  /** Where the helper programs stand. Null while RigControl is still starting. */
  helpers: HelpersInfo | null;
  oldApps: { id: string; name: string; version: string | null; why: string }[];
  elevated: boolean;
};

export type GccState = { installed: boolean; running: boolean; startsWithWindows: boolean; disabledByRigControl: boolean };

// ---------- health and first run ----------
export type HealthSeverity = "info" | "warn" | "blocks";
/** `confirm` is the question to ask before calling `method`. */
export type HealthFix = { label: string; method: string; args: unknown; confirm?: string | null };
export type HealthFinding = {
  id: string;
  /** The module it gets in the way of ("lighting", "cooling", "capture", ...). */
  module: string;
  name: string;
  kind: string;
  severity: HealthSeverity;
  whatItBreaks: string;
  whatToDo: string;
  /** False when this entry has not been checked against a real install of that program. */
  verified: boolean;
  fix: HealthFix | null;
  /** What was seen, for example "GCC.exe is running." */
  evidence?: string;
};
export type HealthReport = {
  scannedAt: string;
  findings: HealthFinding[];
  counts: { blocks: number; warn: number; info: number };
  system: Record<string, boolean | number | string | null>;
};

/** One helper program RigControl needs. There is no yes or no per program: all are installed together. */
export type HelperProgram = {
  id: "openrgb" | "pawnio" | "equalizerapo";
  name: string;
  what: string;
  licence: string;
  /** This build of RigControl has it inside. */
  carried: boolean;
  installed: boolean;
};
export type HelpersInfo = {
  items: HelperProgram[];
  /** The one yes was given (as administrator), or nothing is left to set up. */
  consented: boolean;
  /** Something carried is not set up and nobody said yes yet: the setup step is shown. */
  needsYes: boolean;
  /** Something carried is still not set up (before the yes, or after it when a set-up failed). */
  pending: boolean;
  /** An OpenRGB service is on this PC and will run RigControl's copy after the yes. */
  serviceWillMove: boolean;
  /** Names of programs that are neither on this PC nor inside this build. */
  notCarried: string[];
  /** What went wrong at the last try, one line per program. */
  problems: string[];
};
export type FirstRunInfo = {
  done: boolean;
  elevated: boolean;
  autostart: boolean;
  restartSuggested: boolean;
  /** Why a restart is offered: "Start with Windows" was switched on, and/or Equalizer APO was just installed. */
  restartForAutostart: boolean;
  restartForEqualiser: boolean;
  /** Other Windows users signed in right now: a restart closes their programs too. */
  otherUsersSignedIn: number;
  /** False for a standard Windows account, which can never start RigControl as administrator by itself. */
  accountIsAdministrator: boolean;
  /** RigControl runs as a different Windows account than the one signed in to this desktop. */
  otherAccount: boolean;
  /** Programs without administrator rights can change RigControl's own folder. */
  programFolderOpen: boolean;
  hardware: { label: string; value: string }[];
  features: { id: string; name: string; available: boolean; reason: string | null }[];
  helpers: HelpersInfo;
  findings: HealthFinding[];
};

/** Every event the backend sends. */
export interface EventMap {
  "app.modulesChanged": ModuleInfo[];
  "sensors.snapshot": SensorSnapshot;
  "cooling.status": CoolingStatus;
  "lighting.changed": LightingStatus;
  "capture.status": CaptureStatus;
  "drivers.status": DriversStatus;
  "presence.status": PresenceStatus;
  "health.report": HealthReport;
  "lighting.sync": LightSync;
  "overlay.shown": { wide: boolean };
  "overlay.discord": { open: boolean; problem: string | null };
  "overlay.pad": { what: string };
  "overlay.mode": { mode: string };
  "drivers.download": DriverDownload;
  "nvidia.driverDownload": DriverDownloadProgress;
}

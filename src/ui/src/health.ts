// One shared copy of the health report (programs and Windows settings that get in RigControl's way),
// so the menu badge, the notices on each page and the Health page all show the same thing.
import { useSyncExternalStore } from "react";
import { call, errorMessage, on } from "./bridge";
import { extraModuleName } from "./extras";
import type { HealthFinding, HealthReport, HealthSeverity } from "./types";

type State = { report: HealthReport | null; error: string | null };

let state: State = { report: null, error: null };
let started = false;
let retries = 0;
const subscribers = new Set<() => void>();

export const SEVERITY_ORDER: HealthSeverity[] = ["blocks", "warn", "info"];

/** Makes a list of findings safe to draw whatever the backend left out. */
export function tidyFindings(list: unknown): HealthFinding[] {
  if (!Array.isArray(list)) return [];
  return list
    .filter((f): f is HealthFinding => !!f && typeof f === "object" && typeof (f as HealthFinding).id === "string")
    .map((f) => ({
      ...f,
      module: f.module || "system",
      name: f.name || f.id,
      kind: typeof f.kind === "string" ? f.kind : "",
      severity: SEVERITY_ORDER.includes(f.severity) ? f.severity : "info",
      whatItBreaks: f.whatItBreaks ?? "",
      whatToDo: f.whatToDo ?? "",
      verified: f.verified !== false,
      fix: f.fix && typeof f.fix.method === "string" && f.fix.method ? f.fix : null,
    }));
}

function tidy(r: HealthReport): HealthReport {
  const findings = tidyFindings(r?.findings);
  const count = (s: HealthSeverity) => findings.filter((f) => f.severity === s).length;
  return {
    scannedAt: r?.scannedAt ?? new Date().toISOString(),
    findings,
    counts: { blocks: count("blocks"), warn: count("warn"), info: count("info") },
    system: r?.system && typeof r.system === "object" ? r.system : {},
  };
}

function set(next: State) {
  state = next;
  for (const fn of [...subscribers]) fn();
}

function load() {
  call<HealthReport>("health.getReport")
    .then((r) => set({ report: tidy(r), error: null }))
    .catch((err) => {
      if (!state.report) set({ report: null, error: errorMessage(err) });
      // The health check may still be starting; ask a few more times, then leave it.
      if (retries++ < 6) window.setTimeout(load, 5000);
    });
}

function subscribe(fn: () => void) {
  if (!started) {
    started = true;
    on("health.report", (r) => set({ report: tidy(r), error: null }));
    load();
  }
  subscribers.add(fn);
  return () => {
    subscribers.delete(fn);
  };
}

const snapshot = () => state;

/** The latest health report, or null while it is not known. Never throws. */
export function useHealth(): State {
  return useSyncExternalStore(subscribe, snapshot);
}

/** Checks the PC again and returns the new report. */
export async function rescanHealth(): Promise<HealthReport> {
  const r = tidy(await call<HealthReport>("health.rescan", undefined, 60000));
  set({ report: r, error: null });
  return r;
}

const MODULE_NAMES: Record<string, string> = {
  lighting: "Lighting",
  cooling: "Cooling",
  sensors: "Sensors and motherboard fans",
  nvidia: "Graphics",
  capture: "Recording",
  presence: "Games and Discord status",
  overlay: "In-game panel",
  audio: "Audio",
  bios: "CPU",
  cpu: "CPU",
  drivers: "Drivers",
  app: "RigControl",
  system: "Windows",
  windows: "Windows",
};

/** The name a user knows a module by. */
export function moduleLabel(module: string, fallback?: string | null): string {
  return (
    MODULE_NAMES[module] ?? extraModuleName(module) ?? fallback ?? module.charAt(0).toUpperCase() + module.slice(1)
  );
}

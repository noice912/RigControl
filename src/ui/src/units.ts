import { useSyncExternalStore } from "react";

/** How temperatures are shown. Everything inside the app (sensors, curves, devices) stays in °C. */
export type TempUnit = "F" | "C";

let current: TempUnit = "F";
const listeners = new Set<() => void>();

export function setTempUnit(unit: TempUnit) {
  if (unit !== "F" && unit !== "C") return;
  if (unit === current) return;
  current = unit;
  listeners.forEach((l) => l());
}

export function getTempUnit(): TempUnit {
  return current;
}

export function useTempUnit(): TempUnit {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => current,
  );
}

/** A °C value in the chosen unit. */
export function toUnit(celsius: number, unit: TempUnit): number {
  return unit === "F" ? (celsius * 9) / 5 + 32 : celsius;
}

/** "°F" or "°C". */
export function unitLabel(unit: TempUnit): string {
  return unit === "F" ? "°F" : "°C";
}

/** A °C value as text in the chosen unit, without the unit: "–" when unknown. */
export function fmtTemp(celsius: number | null | undefined, unit: TempUnit, digits = 0): string {
  if (celsius === null || celsius === undefined || Number.isNaN(celsius)) return "–";
  return toUnit(celsius, unit).toFixed(digits);
}

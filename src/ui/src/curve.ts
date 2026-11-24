import type { CurvePoint } from "./types";

/** Same rules the backend enforces (docs/API.md). Returns an error message or null. */
export function validateCurve(points: CurvePoint[], minDuty: number, range: { min: number; max: number }): string | null {
  if (points.length < 2) return "A curve needs at least 2 points.";
  for (let i = 0; i < points.length; i++) {
    const p = points[i]!;
    if (!Number.isInteger(p.tempC) || !Number.isInteger(p.dutyPercent)) return "Points must be whole numbers.";
    if (p.tempC < range.min || p.tempC > range.max)
      return `Temperatures must be between ${range.min} and ${range.max} °C (${Math.round((range.min * 9) / 5 + 32)} and ${Math.round((range.max * 9) / 5 + 32)} °F).`;
    if (p.dutyPercent < minDuty || p.dutyPercent > 100) return `Speed must be between ${minDuty}% and 100%.`;
    if (i > 0) {
      const prev = points[i - 1]!;
      if (p.tempC <= prev.tempC) return "Each point must be hotter than the one before it.";
      if (p.dutyPercent < prev.dutyPercent) return "Speed must not drop as the temperature rises.";
    }
  }
  return null;
}

/** Linear interpolation, clamped at both ends. NaN (unknown temperature) returns 100. */
export function evaluateCurve(points: CurvePoint[], tempC: number | null): number {
  if (tempC === null || Number.isNaN(tempC) || points.length === 0) return 100;
  const first = points[0]!;
  const last = points[points.length - 1]!;
  if (tempC <= first.tempC) return first.dutyPercent;
  if (tempC >= last.tempC) return last.dutyPercent;
  for (let i = 1; i < points.length; i++) {
    const b = points[i]!;
    if (tempC > b.tempC) continue;
    const a = points[i - 1]!;
    const t = (tempC - a.tempC) / (b.tempC - a.tempC);
    return Math.round(a.dutyPercent + t * (b.dutyPercent - a.dutyPercent));
  }
  return last.dutyPercent;
}

/** Presets scaled to a temperature range; duties never go below minDuty. */
export function presetCurve(
  kind: "quiet" | "balanced" | "performance",
  range: { min: number; max: number },
  minDuty: number,
  pointCount = 5,
): CurvePoint[] {
  const shape: Record<typeof kind, number[]> = {
    quiet: [0.0, 0.15, 0.35, 0.65, 1.0],
    balanced: [0.15, 0.3, 0.5, 0.8, 1.0],
    performance: [0.4, 0.55, 0.75, 0.95, 1.0],
  };
  const fractions = shape[kind];
  const n = Math.max(2, Math.min(pointCount, fractions.length));
  const pts: CurvePoint[] = [];
  for (let i = 0; i < n; i++) {
    const tempC = Math.round(range.min + ((range.max - range.min) * i) / (n - 1));
    const f = fractions[Math.round((i * (fractions.length - 1)) / (n - 1))]!;
    const duty = Math.round(minDuty + (100 - minDuty) * f);
    pts.push({ tempC, dutyPercent: Math.max(minDuty, Math.min(100, duty)) });
  }
  // Enforce monotonic duty after rounding.
  for (let i = 1; i < pts.length; i++) {
    if (pts[i]!.dutyPercent < pts[i - 1]!.dutyPercent) pts[i]!.dutyPercent = pts[i - 1]!.dutyPercent;
  }
  return pts;
}

/** Moves one point while keeping the curve valid. Returns a new array. */
export function movePoint(
  points: CurvePoint[],
  index: number,
  tempC: number,
  duty: number,
  minDuty: number,
  range: { min: number; max: number },
): CurvePoint[] {
  const prev = points[index - 1];
  const next = points[index + 1];
  const tMin = prev ? prev.tempC + 1 : range.min;
  const tMax = next ? next.tempC - 1 : range.max;
  const dMin = Math.max(minDuty, prev ? prev.dutyPercent : minDuty);
  const dMax = next ? next.dutyPercent : 100;
  const copy = points.map((p) => ({ ...p }));
  copy[index] = {
    tempC: Math.round(Math.min(Math.max(tempC, tMin), Math.max(tMin, tMax))),
    dutyPercent: Math.round(Math.min(Math.max(duty, dMin), Math.max(dMin, dMax))),
  };
  return copy;
}

/** Inserts a point halfway along the widest gap. */
export function addPoint(points: CurvePoint[]): CurvePoint[] {
  if (points.length < 2) return points;
  let best = 1;
  let gap = -1;
  for (let i = 1; i < points.length; i++) {
    const g = points[i]!.tempC - points[i - 1]!.tempC;
    if (g > gap) {
      gap = g;
      best = i;
    }
  }
  if (gap < 2) return points;
  const a = points[best - 1]!;
  const b = points[best]!;
  const tempC = Math.round((a.tempC + b.tempC) / 2);
  const dutyPercent = Math.round((a.dutyPercent + b.dutyPercent) / 2);
  const copy = points.map((p) => ({ ...p }));
  copy.splice(best, 0, { tempC, dutyPercent });
  return copy;
}

export function removePoint(points: CurvePoint[], index: number): CurvePoint[] {
  if (points.length <= 2) return points;
  return points.filter((_, i) => i !== index);
}

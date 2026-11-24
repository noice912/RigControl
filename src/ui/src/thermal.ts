/** Temperature bands per kind of sensor: below `cool` is blue, `warm` is amber, at/above `hot` is red. */
export const BANDS = {
  cpu: { cool: 45, warm: 70, hot: 90 },
  gpu: { cool: 45, warm: 68, hot: 83 },
  liquid: { cool: 30, warm: 37, hot: 45 },
  generic: { cool: 40, warm: 60, hot: 80 },
} as const;

export type Band = (typeof BANDS)[keyof typeof BANDS];

const COOL: [number, number, number] = [0x5c, 0xc6, 0xe8];
const WARM: [number, number, number] = [0xf0, 0xb4, 0x4c];
const HOT: [number, number, number] = [0xee, 0x5a, 0x42];
const UNKNOWN = "#6B7886";

function mix(a: [number, number, number], b: [number, number, number], t: number): string {
  const c = a.map((v, i) => Math.round(v + (b[i]! - v) * Math.min(1, Math.max(0, t))));
  return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
}

/** Colour for a temperature in its band. Unknown (null) is a neutral grey. */
export function thermalColor(tempC: number | null | undefined, band: Band = BANDS.generic): string {
  if (tempC === null || tempC === undefined || Number.isNaN(tempC)) return UNKNOWN;
  if (tempC <= band.cool) return mix(COOL, COOL, 0);
  if (tempC <= band.warm) return mix(COOL, WARM, (tempC - band.cool) / (band.warm - band.cool));
  return mix(WARM, HOT, (tempC - band.warm) / (band.hot - band.warm));
}

/** 0..1 position of a temperature on a gauge running from 20 °C to the band's hot mark plus a margin. */
export function thermalFraction(tempC: number | null | undefined, band: Band = BANDS.generic): number {
  if (tempC === null || tempC === undefined || Number.isNaN(tempC)) return 0;
  const lo = band === BANDS.liquid ? 20 : 25;
  const hi = band.hot + (band === BANDS.liquid ? 5 : 10);
  return Math.min(1, Math.max(0, (tempC - lo) / (hi - lo)));
}

export function fmt(value: number | null | undefined, digits = 0): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "–";
  return value.toFixed(digits);
}

import {
  useEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
} from "react";
import { call } from "../bridge";
import { useAction, useQuery } from "../hooks";
import { Switch } from "./controls";
import type {
  LayoutItem,
  LayoutState,
  LayoutZone,
  SyncPreview,
} from "../types";

const zoneId = (z: { key: string; zone: number }) => `${z.key}#${z.zone}`;
/** A box that holds a whole device (zone -1) is named after the device. */
const boxName = (z: LayoutZone) => (z.zone < 0 ? z.device : z.name);

/** Where each LED of a placed zone looks, the same way the app works it out (a grid for keyboards, else a strip). */
function ledSpots(item: LayoutItem, z: LayoutZone): { x: number; y: number }[] {
  const out: { x: number; y: number }[] = [];
  if (z.matrix && z.matrixWidth > 0 && z.matrixHeight > 0) {
    const cw = item.w / z.matrixWidth;
    const ch = item.h / z.matrixHeight;
    for (let r = 0; r < z.matrixHeight; r++)
      for (let c = 0; c < z.matrixWidth; c++)
        if ((z.matrix[r * z.matrixWidth + c] ?? -1) >= 0)
          out.push({ x: item.x + (c + 0.5) * cw, y: item.y + (r + 0.5) * ch });
    return out;
  }
  const n = Math.max(1, z.leds);
  const across = item.w >= item.h;
  for (let i = 0; i < n; i++) {
    const pos = item.reverse ? n - 1 - i : i;
    out.push(
      across
        ? { x: item.x + ((pos + 0.5) * item.w) / n, y: item.y + item.h / 2 }
        : { x: item.x + item.w / 2, y: item.y + ((pos + 0.5) * item.h) / n },
    );
  }
  return out;
}

/** A first size for a zone that is placed: keyboards keep their grid's shape, strips are long and thin. */
function startSize(z: LayoutZone): { w: number; h: number } {
  if (z.matrix && z.matrixWidth > 0 && z.matrixHeight > 0) {
    const w = 0.4;
    return {
      w,
      h: Math.min(0.9, ((w * z.matrixHeight) / z.matrixWidth) * (16 / 9)),
    };
  }
  if (z.leds <= 1) return { w: 0.07, h: 0.12 };
  return { w: Math.min(0.6, 0.05 * z.leds), h: 0.06 };
}

const clamp = (v: number, lo: number, hi: number) =>
  Math.min(hi, Math.max(lo, v));

/** SignalRGB-style layout: put each zone where it sits around the picture; each LED then takes the colour under it. */
export function LedLayoutPanel({ syncOn }: { syncOn: boolean }) {
  const q = useQuery<LayoutState>("lighting.getLayout");
  const { busy, run } = useAction();
  const [items, setItems] = useState<LayoutItem[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [openZones, setOpenZones] = useState<Set<string>>(new Set());
  const [preview, setPreview] = useState<SyncPreview | null>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const drag = useRef<{
    id: string;
    mode: "move" | "size";
    sx: number;
    sy: number;
    start: LayoutItem;
    last?: LayoutItem;
  } | null>(null);

  const layout = q.data;
  useEffect(() => {
    if (layout)
      setItems(layout.zones.filter((z) => z.placed).map((z) => z.placed!));
  }, [layout]);

  // The picture the lights follow, about 4 times a second while the sync runs.
  useEffect(() => {
    if (!syncOn) {
      setPreview(null);
      return;
    }
    let alive = true;
    const tick = () =>
      call<SyncPreview | null>("lighting.getSyncPreview")
        .then((p) => alive && setPreview(p))
        .catch(() => {});
    void tick();
    const t = window.setInterval(tick, 250);
    return () => {
      alive = false;
      window.clearInterval(t);
    };
  }, [syncOn]);

  useEffect(() => {
    const c = canvasRef.current;
    if (!c || !preview) return;
    c.width = preview.width;
    c.height = preview.height;
    const ctx = c.getContext("2d");
    if (!ctx) return;
    const img = ctx.createImageData(preview.width, preview.height);
    for (let i = 0; i < preview.width * preview.height; i++) {
      const hex = preview.pixels.substr(i * 6, 6);
      img.data[i * 4] = parseInt(hex.slice(0, 2), 16);
      img.data[i * 4 + 1] = parseInt(hex.slice(2, 4), 16);
      img.data[i * 4 + 2] = parseInt(hex.slice(4, 6), 16);
      img.data[i * 4 + 3] = 255;
    }
    ctx.putImageData(img, 0, 0);
  }, [preview]);

  if (!layout) return null;

  const save = async (next: LayoutItem[], patch: { perLed?: boolean } = {}) => {
    setItems(next);
    const r = await run("layout", () =>
      call<LayoutState>("lighting.setLayout", { ...patch, items: next }, 15000),
    );
    if (r) q.setData(r);
  };

  const colourAt = (x: number, y: number) => {
    if (!preview) return "rgba(255,255,255,0.55)";
    const cx = clamp(Math.floor(x * preview.width), 0, preview.width - 1);
    const cy = clamp(Math.floor(y * preview.height), 0, preview.height - 1);
    return "#" + preview.pixels.substr((cy * preview.width + cx) * 6, 6);
  };

  const zones = layout.zones;
  const byId = new Map(zones.map((z) => [zoneId(z), z]));
  const place = (z: LayoutZone) => {
    const { w, h } = startSize(z);
    const n = items.length % 8;
    const item: LayoutItem = {
      key: z.key,
      zone: z.zone,
      x: clamp(0.04 + n * 0.05, 0, 1 - w),
      y: clamp(0.06 + n * 0.1, 0, 1 - h),
      w,
      h,
      reverse: false,
    };
    setSelected(zoneId(z));
    void save([...items.filter((i) => zoneId(i) !== zoneId(z)), item]);
  };
  const remove = (id: string) =>
    void save(items.filter((i) => zoneId(i) !== id));

  const onDown = (e: ReactPointerEvent, id: string, mode: "move" | "size") => {
    const it = items.find((i) => zoneId(i) === id);
    if (!it || busy !== null) return;
    e.stopPropagation();
    (e.target as Element).setPointerCapture(e.pointerId);
    setSelected(id);
    drag.current = { id, mode, sx: e.clientX, sy: e.clientY, start: it };
  };
  const onMove = (e: ReactPointerEvent) => {
    const d = drag.current;
    const box = boxRef.current?.getBoundingClientRect();
    if (!d || !box) return;
    const dx = (e.clientX - d.sx) / box.width;
    const dy = (e.clientY - d.sy) / box.height;
    const s = d.start;
    const next =
      d.mode === "move"
        ? {
            ...s,
            x: clamp(s.x + dx, 0, 1 - s.w),
            y: clamp(s.y + dy, 0, 1 - s.h),
          }
        : {
            ...s,
            w: clamp(s.w + dx, 0.02, 1 - s.x),
            h: clamp(s.h + dy, 0.02, 1 - s.y),
          };
    d.last = next;
    setItems((all) => all.map((i) => (zoneId(i) === d.id ? next : i)));
  };
  const onUp = () => {
    const d = drag.current;
    drag.current = null;
    if (!d?.last) return;
    const last = d.last;
    void save(items.map((i) => (zoneId(i) === d.id ? last : i)));
  };

  const devices = [...new Set(zones.map((z) => z.key))];
  const sel = selected ? items.find((i) => zoneId(i) === selected) : undefined;
  const selZone = selected ? byId.get(selected) : undefined;

  return (
    <section className="panel" aria-label="Light layout">
      <div className="panel-head">
        <div>
          <h2>Layout (per LED)</h2>
          <div className="sub">
            Put each light where it sits around your screen. While "Follow the
            screen" is on, every LED takes the colour of the part of the picture
            under it, like SignalRGB. Lights you don't place take the whole
            picture's colour. Drag a box to move it, its corner to resize it.
          </div>
        </div>
        <Switch
          checked={layout.perLed}
          disabled={busy !== null}
          onChange={(v) => void save(items, { perLed: v })}
          label="Per LED"
        />
      </div>

      <div className="layout-wrap">
        <div
          ref={boxRef}
          className="layout-canvas"
          onPointerMove={onMove}
          onPointerUp={onUp}
          onPointerCancel={onUp}
          onPointerDown={() => setSelected(null)}
          aria-label="The picture, with your lights placed on it"
        >
          <canvas ref={canvasRef} className="layout-picture" aria-hidden />
          {!preview && (
            <div className="layout-empty sub">
              {syncOn
                ? "Waiting for the picture…"
                : "Turn on Lighting effects to see the picture here."}
            </div>
          )}
          {items.map((it) => {
            const id = zoneId(it);
            const z = byId.get(id);
            if (!z) return null;
            return (
              <div
                key={id}
                className={`layout-zone${selected === id ? " selected" : ""}${z.connected ? "" : " missing"}`}
                style={{
                  left: `${it.x * 100}%`,
                  top: `${it.y * 100}%`,
                  width: `${it.w * 100}%`,
                  height: `${it.h * 100}%`,
                }}
                onPointerDown={(e) => onDown(e, id, "move")}
                title={`${z.device}: ${z.name}`}
              >
                <span className="layout-label">{boxName(z)}</span>
                {z.connected && z.segments && (
                  <svg
                    className="layout-lines"
                    viewBox="0 0 100 100"
                    preserveAspectRatio="none"
                    aria-hidden
                  >
                    {z.segments.map(([x1 = 0, y1 = 0, x2 = 0, y2 = 0], i) => (
                      <line
                        key={i}
                        x1={x1 * 100}
                        y1={y1 * 100}
                        x2={x2 * 100}
                        y2={y2 * 100}
                        stroke={colourAt(
                          it.x + ((x1 + x2) / 2) * it.w,
                          it.y + ((y1 + y2) / 2) * it.h,
                        )}
                        strokeWidth={4}
                        strokeLinecap="round"
                        vectorEffect="non-scaling-stroke"
                      />
                    ))}
                  </svg>
                )}
                {z.connected &&
                  !z.segments &&
                  ledSpots(it, z).map((p, i) => (
                    <span
                      key={i}
                      className="layout-led"
                      style={{
                        left: `${((p.x - it.x) / it.w) * 100}%`,
                        top: `${((p.y - it.y) / it.h) * 100}%`,
                        background: colourAt(p.x, p.y),
                      }}
                    />
                  ))}
                <span
                  className="layout-handle"
                  onPointerDown={(e) => onDown(e, id, "size")}
                  aria-label="Resize"
                />
              </div>
            );
          })}
        </div>

        <div className="layout-list">
          {sel && selZone && (
            <div className="layout-selected">
              <strong>
                {selZone.zone < 0
                  ? selZone.device
                  : `${selZone.device}: ${selZone.name}`}
              </strong>
              <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
                {!selZone.matrix && selZone.leds > 1 && (
                  <button
                    className="btn small"
                    disabled={busy !== null}
                    onClick={() =>
                      void save(
                        items.map((i) =>
                          zoneId(i) === selected
                            ? { ...i, reverse: !i.reverse }
                            : i,
                        ),
                      )
                    }
                  >
                    {sel.reverse ? "Run forwards" : "Run backwards"}
                  </button>
                )}
                {!selZone.matrix && selZone.leds > 1 && (
                  <button
                    className="btn small"
                    disabled={busy !== null}
                    onClick={() => {
                      // Turn a strip on its side, keeping it in the picture.
                      const w = clamp(sel.h * (9 / 16), 0.02, 1);
                      const h = clamp(sel.w * (16 / 9), 0.02, 1);
                      void save(
                        items.map((i) =>
                          zoneId(i) === selected
                            ? {
                                ...i,
                                w,
                                h,
                                x: clamp(i.x, 0, 1 - w),
                                y: clamp(i.y, 0, 1 - h),
                              }
                            : i,
                        ),
                      );
                    }}
                  >
                    Turn
                  </button>
                )}
                <button
                  className="btn small"
                  disabled={busy !== null}
                  onClick={() => remove(selected!)}
                >
                  Take off the layout
                </button>
              </div>
            </div>
          )}
          {devices.map((key) => {
            const all = zones.filter((z) => z.key === key);
            const whole = all.find((z) => z.zone < 0);
            const parts = all.filter((z) => z.zone >= 0);
            // Zones show when asked for, or when one of them is already placed on its own.
            const showZones =
              openZones.has(key) ||
              !whole ||
              parts.some((z) => items.some((i) => zoneId(i) === zoneId(z)));
            const row = (z: LayoutZone, label: React.ReactNode) => {
              const id = zoneId(z);
              const placed = items.some((i) => zoneId(i) === id);
              return (
                <div
                  key={id}
                  className={`layout-row${selected === id ? " selected" : ""}`}
                >
                  <button
                    className="linkish"
                    onClick={() => placed && setSelected(id)}
                    disabled={!placed}
                  >
                    {label}{" "}
                    <span className="sub">
                      · {z.leds} {z.segments ? "panel" : "LED"}
                      {z.leds === 1 ? "" : "s"}
                      {z.connected ? "" : " · not connected"}
                    </span>
                  </button>
                  {placed ? (
                    <button
                      className="btn small ghost"
                      disabled={busy !== null}
                      onClick={() => remove(id)}
                    >
                      Remove
                    </button>
                  ) : (
                    <button
                      className="btn small"
                      disabled={busy !== null || !z.connected}
                      onClick={() => place(z)}
                    >
                      Place
                    </button>
                  )}
                </div>
              );
            };
            return (
              <div key={key} className="layout-device">
                {whole ? (
                  row(whole, <strong>{whole.device}</strong>)
                ) : (
                  <div className="layout-device-name">{all[0]?.device}</div>
                )}
                {whole && parts.length > 1 && (
                  <button
                    className="linkish sub layout-zones-toggle"
                    onClick={() =>
                      setOpenZones((s) => {
                        const n = new Set(s);
                        if (n.has(key)) n.delete(key);
                        else n.add(key);
                        return n;
                      })
                    }
                  >
                    {showZones
                      ? "Hide zones"
                      : `Place zones separately (${parts.length})`}
                  </button>
                )}
                {showZones && parts.length > 1 && (
                  <div className="layout-zones">
                    {parts.map((z) => row(z, z.name))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </section>
  );
}

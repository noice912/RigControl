import { useEffect, useRef } from "react";
import { Slider } from "./controls";
import type { EffectInfo, EffectParams, LightSync } from "../types";

/** A small moving picture of each effect for its card in the library. */
const THUMBS: Record<string, string> = {
  screen: "linear-gradient(135deg, #1b3a5c, #3a7bff 40%, #f0b44c 75%, #ee5a42)",
  solid: "VAR1",
  breathing: "radial-gradient(circle at 50% 50%, VAR1, #000 80%)",
  cycle: "linear-gradient(90deg, #f00, #ff0, #0f0, #0ff, #00f, #f0f, #f00)",
  wave: "linear-gradient(90deg, #f00, #ff0, #0f0, #0ff, #00f, #f0f, #f00, #ff0, #0f0)",
  spiral: "conic-gradient(#f00, #ff0, #0f0, #0ff, #00f, #f0f, #f00)",
  gradient: "linear-gradient(90deg, VAR1, VAR2, VAR1, VAR2)",
  stars:
    "radial-gradient(circle at 20% 30%, VAR1 0 2px, transparent 3px), radial-gradient(circle at 70% 60%, VAR1 0 2px, transparent 3px), radial-gradient(circle at 45% 80%, VAR1 0 1.5px, transparent 2.5px), radial-gradient(circle at 85% 20%, VAR1 0 1.5px, transparent 2.5px), VAR2",
  rain: "repeating-linear-gradient(180deg, VAR1 0 6px, VAR2 6px 22px)",
  visualizer:
    "linear-gradient(0deg, VAR1, VAR2) left bottom / 30% 80% no-repeat, linear-gradient(0deg, VAR1, VAR2) center bottom / 30% 55% no-repeat, linear-gradient(0deg, VAR1, VAR2) right bottom / 30% 35% no-repeat, #0b0f14",
  ripple:
    "radial-gradient(circle, transparent 20%, VAR1 24%, transparent 30%, transparent 45%, VAR1 50%, transparent 56%), #0b0f14",
  audiocolor: "linear-gradient(90deg, #e33, #3e3, #33e)",
};

const ANIMATED = new Set([
  "cycle",
  "wave",
  "spiral",
  "gradient",
  "rain",
  "breathing",
]);

function thumb(id: string, p?: EffectParams): string {
  const t = THUMBS[id] ?? "#000";
  return t
    .split("VAR1")
    .join(p?.color1 ?? "#ff0040")
    .split("VAR2")
    .join(p?.color2 ?? "#0040ff");
}

/** A colour box that only saves once the picker is closed (not on every drag). */
function ColorPick(props: {
  value: string;
  label: string;
  disabled?: boolean;
  onCommit: (v: string) => void;
}) {
  const ref = useRef<HTMLInputElement>(null);
  const commit = useRef(props.onCommit);
  commit.current = props.onCommit;
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.value = props.value.toLowerCase();
    const onChange = () => commit.current(el.value.toUpperCase());
    el.addEventListener("change", onChange);
    return () => el.removeEventListener("change", onChange);
  }, [props.value]);
  return (
    <input
      ref={ref}
      type="color"
      className="fx-color"
      aria-label={props.label}
      disabled={props.disabled}
      defaultValue={props.value.toLowerCase()}
    />
  );
}

/** The effects library: pick what paints the lights, then tune it. */
export function EffectLibrary(props: {
  sync: LightSync;
  busy: boolean;
  save: (patch: Record<string, unknown>, done?: string) => Promise<void>;
}) {
  const { sync: s, busy, save } = props;
  const effects: EffectInfo[] = s.effects ?? [];
  const current = effects.find((e) => e.id === s.effect);
  const params = s.effectParams?.[s.effect];
  const groups = [...new Set(effects.map((e) => e.group))];
  const setParam = (patch: Partial<EffectParams>) => {
    if (!params) return;
    void save({ effect: s.effect, params: { ...params, ...patch } });
  };
  const uses = (name: string) => current?.uses.includes(name) ?? false;

  return (
    <>
      {groups.map((g) => (
        <div key={g} className="fx-group">
          <div className="fx-group-name">{g}</div>
          <div className="fx-grid">
            {effects
              .filter((e) => e.group === g)
              .map((e) => (
                <button
                  key={e.id}
                  className={"fx-card" + (e.id === s.effect ? " active" : "")}
                  disabled={busy}
                  aria-pressed={e.id === s.effect}
                  title={e.description}
                  onClick={() =>
                    e.id !== s.effect &&
                    void save(
                      { effect: e.id, ...(s.enabled ? {} : { enabled: true }) },
                      e.name,
                    )
                  }
                >
                  <span
                    className={
                      "fx-thumb" + (ANIMATED.has(e.id) ? " anim-" + e.id : "")
                    }
                    style={{ background: thumb(e.id, s.effectParams?.[e.id]) }}
                    aria-hidden
                  />
                  <span className="fx-name">
                    {e.name}
                    {e.audio && <span className="fx-badge">sound</span>}
                  </span>
                  <span className="fx-desc">{e.description}</span>
                </button>
              ))}
          </div>
        </div>
      ))}

      {current && params && current.uses.length > 0 && (
        <div className="fx-settings">
          <div className="fx-group-name">{current.name} settings</div>
          {(uses("color1") || uses("color2")) && (
            <div className="field">
              <div className="field-label">
                <span>Colours</span>
                <span className="sub">
                  {current.id === "visualizer"
                    ? "Bottom of the bars, then the top."
                    : current.id === "stars" || current.id === "rain"
                      ? "The stars or drops, then the background."
                      : uses("color2")
                        ? "First and second colour."
                        : "The effect's colour."}
                </span>
              </div>
              <div className="field-control row" style={{ gap: 8 }}>
                {uses("color1") && (
                  <ColorPick
                    label="First colour"
                    value={params.color1}
                    disabled={busy}
                    onCommit={(v) => setParam({ color1: v })}
                  />
                )}
                {uses("color2") && (
                  <ColorPick
                    label="Second colour"
                    value={params.color2}
                    disabled={busy}
                    onCommit={(v) => setParam({ color2: v })}
                  />
                )}
              </div>
            </div>
          )}
          {uses("speed") && (
            <div className="field">
              <div className="field-label">Speed</div>
              <div className="field-control">
                <Slider
                  label="Effect speed"
                  min={1}
                  max={100}
                  value={params.speed}
                  disabled={busy}
                  format={(v) => `${v}%`}
                  onCommit={(v) => setParam({ speed: v })}
                />
              </div>
            </div>
          )}
          {uses("size") && (
            <div className="field">
              <div className="field-label">Size</div>
              <div className="field-control">
                <Slider
                  label="Effect size"
                  min={1}
                  max={100}
                  value={params.size}
                  disabled={busy}
                  format={(v) => `${v}%`}
                  onCommit={(v) => setParam({ size: v })}
                />
              </div>
            </div>
          )}
          {uses("direction") && (
            <div className="field">
              <div className="field-label">Direction</div>
              <div className="field-control">
                <select
                  className="select"
                  aria-label="Effect direction"
                  value={params.direction}
                  disabled={busy}
                  onChange={(e) => setParam({ direction: e.target.value })}
                >
                  <option value="right">Left to right</option>
                  <option value="left">Right to left</option>
                  <option value="down">Top to bottom</option>
                  <option value="up">Bottom to top</option>
                </select>
              </div>
            </div>
          )}
        </div>
      )}
    </>
  );
}

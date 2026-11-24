import { useEffect, useMemo, useRef, useState } from "react";
import { call } from "../bridge";
import { useAction, useLive, useQuery } from "../hooks";
import { useApp } from "../appContext";
import { presetCurve, validateCurve } from "../curve";
import { BANDS, thermalColor } from "../thermal";
import { CurveEditor } from "../components/CurveEditor";
import { fmtTemp, unitLabel, useTempUnit } from "../units";
import { Dialog, Notice, Segmented, Slider, Switch } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import { HealthNotice } from "../components/HealthFindings";
import type { ArmouryState, CoolingChannel, CoolingDevice, CoolingStatus, CurvePoint, FanControl, LcdScreen, LcdScreenMode, TemperatureSource } from "../types";

type Draft = { mode: "curve" | "fixed"; points: CurvePoint[]; fixed: number };

function initialDraft(c: CoolingChannel): Draft {
  const range = c.curveTempRange;
  const canCurve = range !== null;
  const points = c.curve && c.curve.length >= 2 ? c.curve : range ? presetCurve("balanced", range, c.minDutyPercent) : [];
  return {
    mode: canCurve && (c.mode === "curve" || c.mode === "unknown") ? "curve" : "fixed",
    points,
    fixed: Math.max(c.minDutyPercent, c.fixedDutyPercent ?? c.dutyPercent ?? (c.kind === "pump" ? 75 : 50)),
  };
}

function ChannelEditor(props: {
  device: CoolingDevice;
  channel: CoolingChannel;
  liquidTempC?: number | null;
  locked: boolean;
  onApplied: (d: CoolingDevice) => void;
}) {
  const { device, channel: c } = props;
  const { busy, run } = useAction();
  const [draft, setDraft] = useState<Draft>(() => initialDraft(c));
  const [dirty, setDirty] = useState(false);
  const range = c.curveTempRange;

  // Re-seed from the device only when the user has nothing unsaved.
  const seededFrom = useRef(JSON.stringify([c.mode, c.curve, c.fixedDutyPercent]));
  useEffect(() => {
    const key = JSON.stringify([c.mode, c.curve, c.fixedDutyPercent]);
    if (!dirty && key !== seededFrom.current) {
      seededFrom.current = key;
      setDraft(initialDraft(c));
    }
  }, [c, dirty]);

  const update = (d: Partial<Draft>) => {
    setDraft((cur) => ({ ...cur, ...d }));
    setDirty(true);
  };

  const curveError = draft.mode === "curve" && range ? validateCurve(draft.points, c.minDutyPercent, range) : null;

  const apply = async () => {
    const result =
      draft.mode === "curve"
        ? await run("apply", () =>
            call<CoolingDevice>("cooling.setCurve", { deviceId: device.id, channel: c.id, points: draft.points }),
            `${c.name} curve applied`,
          )
        : await run("apply", () =>
            call<CoolingDevice>("cooling.setFixed", { deviceId: device.id, channel: c.id, dutyPercent: draft.fixed }),
            `${c.name} set to ${draft.fixed}%`,
          );
    if (result) {
      setDirty(false);
      props.onApplied(result);
    }
  };

  return (
    <div className={`inset stack ${props.locked ? "locked" : ""}`}>
      <div className="spread">
        <div>
          <h3>{c.name}</h3>
          <div className="sub">
            {c.rpm != null ? `${c.rpm.toLocaleString()} rpm` : "No speed reading"}
            {c.dutyPercent != null ? `, running at ${c.dutyPercent}%` : ""}
          </div>
        </div>
        <Segmented
          label={`${c.name} mode`}
          value={draft.mode}
          disabled={props.locked}
          options={[
            { value: "curve", label: "Curve", disabled: !range },
            { value: "fixed", label: "Fixed speed" },
          ]}
          onChange={(m) => update({ mode: m })}
        />
      </div>

      {draft.mode === "curve" && range ? (
        <>
          <div className="row">
            <span className="sub">Start from</span>
            {(["quiet", "balanced", "performance"] as const).map((p) => (
              <button
                key={p}
                className="btn small"
                disabled={props.locked}
                onClick={() => update({ points: presetCurve(p, range, c.minDutyPercent, Math.min(5, c.maxCurvePoints ?? 5)) })}
              >
                {p === "quiet" ? "Quiet" : p === "balanced" ? "Balanced" : "Performance"}
              </button>
            ))}
          </div>
          <CurveEditor
            points={draft.points}
            onChange={(points) => update({ points })}
            range={range}
            minDuty={c.minDutyPercent}
            maxPoints={c.maxCurvePoints}
            currentTempC={device.liquidTempC ?? props.liquidTempC ?? null}
            sourceLabel="liquid temperature"
            disabled={props.locked}
          />
          <div className="sub">
            {c.storedOnDevice
              ? "This curve is saved on the cooler and keeps running even when RigControl is closed."
              : "RigControl runs this curve, so it only works while RigControl is open (in the tray is fine)."}
          </div>
        </>
      ) : (
        <Slider
          label={`${c.name} speed`}
          min={c.minDutyPercent}
          max={100}
          value={draft.fixed}
          disabled={props.locked}
          format={(v) => `${v}%`}
          onChange={(v) => update({ fixed: v })}
        />
      )}

      {c.minDutyPercent > 0 && (
        <div className="sub faint">
          {c.kind === "pump"
            ? `The pump never goes below ${c.minDutyPercent}%, so your processor is always cooled.`
            : `This fan never goes below ${c.minDutyPercent}%.`}
        </div>
      )}

      <div className="row" style={{ justifyContent: "flex-end" }}>
        {curveError && <span className="sub" style={{ color: "var(--hot)", marginRight: "auto" }}>{curveError}</span>}
        <button
          className="btn"
          disabled={!dirty || busy !== null}
          onClick={() => {
            setDraft(initialDraft(c));
            setDirty(false);
          }}
        >
          Discard changes
        </button>
        <button className="btn primary" disabled={props.locked || !dirty || busy !== null || curveError !== null} onClick={() => void apply()}>
          {busy ? "Applying…" : `Apply to ${c.kind === "pump" ? "pump" : "fan"}`}
        </button>
      </div>
    </div>
  );
}

const SOURCE_NAMES: Record<string, string> = {
  cpuTemp: "CPU temperature",
  gpuTemp: "GPU temperature",
  liquidTemp: "Liquid temperature",
  cpuLoad: "CPU load",
  gpuLoad: "GPU load",
};
const LAYOUT_NAMES: Record<string, string> = { single: "One reading", dual: "Two readings", triple: "Three readings" };
const MODE_NAMES: Record<string, string> = {
  liquid: "Liquid temperature (the cooler's own screen)",
  infographic: "Infographic",
  media: "Picture or GIF",
  clock: "Clock",
  visualizer: "Audio visualizer",
  web: "Web page",
};
const LIVE_MODES = ["infographic", "clock", "visualizer", "web"];
const PICTURE_FILTER = "Pictures and GIFs (*.gif;*.png;*.jpg;*.jpeg;*.bmp)|*.gif;*.png;*.jpg;*.jpeg;*.bmp";

type ScreenDraft = {
  mode: LcdScreenMode;
  layout: string;
  primary: string;
  secondary: string;
  tertiary: string;
  accent: string;
  /** Newly chosen picture/GIF for "Picture or GIF" mode (null = keep the one already on the cooler). */
  path: string | null;
  fileName: string | null;
  /** Background behind an infographic or clock: undefined = unchanged, null = removed, text = newly chosen file. */
  background: string | null | undefined;
  backgroundName: string | null;
  url: string;
};

function draftOf(s: LcdScreen): ScreenDraft {
  return {
    mode: s.mode,
    layout: s.layout,
    primary: s.primary,
    secondary: s.secondary,
    tertiary: s.tertiary ?? "liquidTemp",
    accent: s.accent,
    path: null,
    fileName: s.mediaName,
    background: undefined,
    backgroundName: s.backgroundName ?? null,
    url: s.url ?? "",
  };
}

function fileNameOf(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

/** What the screen shows: the cooler's own liquid temperature, or one of the pictures RigControl draws or stores. */
function ScreenContent(props: { device: CoolingDevice; screen: LcdScreen; locked: boolean; onApplied: (d: CoolingDevice) => void }) {
  const { device, screen } = props;
  const { busy, run } = useAction();
  const [draft, setDraft] = useState<ScreenDraft>(() => draftOf(screen));
  const [preview, setPreview] = useState<string | null>(null);
  const applied = useRef(screen);
  applied.current = screen;

  // Pick up changes made elsewhere (for example the app restarting the screen) unless the user is mid-edit.
  const appliedKey = [screen.mode, screen.layout, screen.primary, screen.secondary, screen.tertiary, screen.accent, screen.mediaName, screen.backgroundName, screen.url].join("|");
  useEffect(() => setDraft(draftOf(applied.current)), [appliedKey]);

  const patch = (p: Partial<ScreenDraft>) => setDraft((d) => ({ ...d, ...p }));
  const live = LIVE_MODES.includes(draft.mode);
  const backgroundArg = draft.background === undefined ? undefined : (draft.background ?? "");

  useEffect(() => {
    if (!live) return;
    let cancelled = false;
    const load = () =>
      call<{ image: string }>("cooling.getLcdPreview", {
        mode: draft.mode,
        layout: draft.layout,
        primary: draft.primary,
        secondary: draft.secondary,
        tertiary: draft.tertiary,
        accent: draft.accent,
        background: backgroundArg,
        url: draft.url || undefined,
      })
        .then((r) => !cancelled && setPreview(r.image))
        .catch(() => undefined);
    void load();
    const timer = window.setInterval(load, draft.mode === "visualizer" ? 400 : 1000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [live, draft.mode, draft.layout, draft.primary, draft.secondary, draft.tertiary, draft.accent, backgroundArg, draft.url]);

  const pick = async (title: string) => {
    const picked = await call<{ path: string | null }>("app.pickFile", { title, filter: PICTURE_FILTER }).catch(() => null);
    return picked?.path ?? null;
  };
  const chooseMedia = async () => {
    const p = await pick("Choose a picture or GIF for the cooler's screen");
    if (p) patch({ path: p, fileName: fileNameOf(p) });
  };
  const chooseBackground = async () => {
    const p = await pick("Choose a picture or GIF to show behind");
    if (p) patch({ background: p, backgroundName: fileNameOf(p) });
  };

  const sameLook =
    draft.layout === screen.layout &&
    draft.primary === screen.primary &&
    draft.secondary === screen.secondary &&
    draft.tertiary === (screen.tertiary ?? "liquidTemp") &&
    draft.accent.toLowerCase() === screen.accent.toLowerCase() &&
    draft.background === undefined;
  const dirty =
    draft.mode !== screen.mode ||
    (draft.mode === "infographic" && !sameLook) ||
    (draft.mode === "clock" && (draft.accent.toLowerCase() !== screen.accent.toLowerCase() || draft.background !== undefined)) ||
    (draft.mode === "visualizer" && draft.accent.toLowerCase() !== screen.accent.toLowerCase()) ||
    (draft.mode === "web" && draft.url.trim() !== (screen.url ?? "")) ||
    (draft.mode === "media" && draft.path !== null);
  const ready =
    draft.mode !== "unset" &&
    (draft.mode !== "media" || draft.path !== null || screen.mediaName !== null) &&
    (draft.mode !== "web" || /^https?:\/\/\S+$/i.test(draft.url.trim()));

  const apply = async () => {
    const args: Record<string, unknown> = { deviceId: device.id, mode: draft.mode };
    if (live) Object.assign(args, { layout: draft.layout, primary: draft.primary, secondary: draft.secondary, tertiary: draft.tertiary, accent: draft.accent });
    if (backgroundArg !== undefined && (draft.mode === "infographic" || draft.mode === "clock")) args.background = backgroundArg;
    if (draft.mode === "web") args.url = draft.url.trim();
    if (draft.mode === "media" && draft.path) args.path = draft.path;
    const r = await run("screen", () => call<CoolingDevice>("cooling.setLcdScreen", args, 120000), "Screen updated");
    if (r) {
      patch({ path: null, background: undefined });
      props.onApplied(r);
    }
  };

  const sourceSelect = (label: string, value: string, onChange: (v: string) => void) => (
    <select className="select" value={value} aria-label={label} disabled={props.locked} onChange={(e) => onChange(e.target.value)}>
      {screen.sources.map((s) => (
        <option key={s} value={s}>
          {SOURCE_NAMES[s] ?? s}
        </option>
      ))}
    </select>
  );

  const colourField = (
    <div className="field">
      <div className="field-label">Colour</div>
      <div className="field-control">
        <input type="color" aria-label="Screen colour" value={draft.accent} disabled={props.locked} onChange={(e) => patch({ accent: e.target.value })} />
      </div>
    </div>
  );

  const backgroundField = (
    <div className="field">
      <div className="field-label">
        <span>Picture behind</span>
        <span className="sub">Optional. A GIF plays behind the numbers.</span>
      </div>
      <div className="field-control" style={{ display: "flex", gap: 8, alignItems: "center", minWidth: 0 }}>
        <button className="btn" disabled={props.locked || busy !== null} onClick={() => void chooseBackground()}>
          Choose…
        </button>
        {draft.backgroundName && (
          <button className="btn" disabled={props.locked || busy !== null} onClick={() => patch({ background: null, backgroundName: null })}>
            Remove
          </button>
        )}
        <span className="sub" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
          {draft.backgroundName ?? "None"}
        </span>
      </div>
    </div>
  );

  const modes = (screen.modes ?? ["liquid", "infographic", "media"]) as LcdScreenMode[];

  const followSwitch = (
    <Switch
      checked={screen.textFollowsLights ?? false}
      disabled={props.locked || busy !== null}
      onChange={(v) =>
        void run("letters", () => call<CoolingDevice>("cooling.setLcdScreen", { deviceId: device.id, textFollowsLights: v }, 60000)).then((r) => r && props.onApplied(r))
      }
      label="Colour follows the lights (while a Lighting effect is on; the colour above is used otherwise)"
    />
  );

  return (
    <>
      {screen.problem && (
        <Notice tone="warn" title="The screen stopped updating">
          {screen.problem}
        </Notice>
      )}
      <div className="field">
        <div className="field-label">Show</div>
        <div className="field-control">
          <select
            className="select"
            aria-label="What the screen shows"
            value={draft.mode}
            disabled={props.locked}
            onChange={(e) => patch({ mode: e.target.value as LcdScreenMode })}
          >
            {draft.mode === "unset" && <option value="unset">Not set by RigControl</option>}
            {modes.map((m) => (
              <option key={m} value={m}>
                {MODE_NAMES[m] ?? m}
              </option>
            ))}
          </select>
        </div>
      </div>

      {live && (
        <div className="lcd-layout">
          <div className="lcd-preview" aria-label="Preview of the cooler's screen">
            {preview && <img src={preview} alt="" />}
          </div>
          <div className="stack" style={{ flex: 1, minWidth: 0 }}>
            {draft.mode === "infographic" && (
              <>
                <div className="field">
                  <div className="field-label">Layout</div>
                  <div className="field-control">
                    <Segmented
                      label="Infographic layout"
                      value={draft.layout}
                      disabled={props.locked}
                      options={screen.layouts.map((l) => ({ value: l, label: LAYOUT_NAMES[l] ?? l }))}
                      onChange={(layout) => patch({ layout })}
                    />
                  </div>
                </div>
                <div className="field">
                  <div className="field-label">{draft.layout === "single" ? "Reading" : draft.layout === "dual" ? "Left" : "Top"}</div>
                  <div className="field-control">{sourceSelect("First reading", draft.primary, (primary) => patch({ primary }))}</div>
                </div>
                {draft.layout !== "single" && (
                  <div className="field">
                    <div className="field-label">{draft.layout === "dual" ? "Right" : "Bottom left"}</div>
                    <div className="field-control">{sourceSelect("Second reading", draft.secondary, (secondary) => patch({ secondary }))}</div>
                  </div>
                )}
                {draft.layout === "triple" && (
                  <div className="field">
                    <div className="field-label">Bottom right</div>
                    <div className="field-control">{sourceSelect("Third reading", draft.tertiary, (tertiary) => patch({ tertiary }))}</div>
                  </div>
                )}
                {colourField}
                {screen.mode === "infographic" && followSwitch}
                {backgroundField}
              </>
            )}
            {draft.mode === "clock" && (
              <>
                {colourField}
                {screen.mode === "clock" && followSwitch}
                {backgroundField}
              </>
            )}
            {draft.mode === "visualizer" && (
              <>
                {colourField}
                {screen.mode === "visualizer" && followSwitch}
                <p className="sub">Bars move with whatever the PC is playing. The preview shows a sample pattern.</p>
              </>
            )}
            {draft.mode === "web" && (
              <>
                <div className="field">
                  <div className="field-label">Web address</div>
                  <div className="field-control">
                    <input
                      className="select"
                      style={{ width: "100%" }}
                      placeholder="https://…"
                      aria-label="Web address"
                      value={draft.url}
                      disabled={props.locked}
                      onChange={(e) => patch({ url: e.target.value })}
                    />
                  </div>
                </div>
                <p className="sub">
                  The page is opened in a hidden 640 × 640 browser and its picture is sent to the screen a few times a second. It can't
                  be clicked, and it can't open windows, download files or use your camera or microphone. The preview appears once it is
                  on the cooler.
                </p>
              </>
            )}
            <p className="sub">Drawn by RigControl, so it needs RigControl open. When RigControl closes, the screen goes back to liquid temperature.</p>
          </div>
        </div>
      )}

      {draft.mode === "media" && (
        <div className="stack">
          <div className="field">
            <div className="field-label">File</div>
            <div className="field-control" style={{ display: "flex", gap: 10, alignItems: "center", minWidth: 0 }}>
              <button className="btn" disabled={props.locked || busy !== null} onClick={() => void chooseMedia()}>
                Choose…
              </button>
              <span className="sub" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                {draft.fileName ?? "No file chosen"}
              </span>
            </div>
          </div>
          <p className="sub">The picture is cropped to fill the round screen and stored in the cooler, so it keeps playing when RigControl is closed. Long GIFs can take a few seconds to send.</p>
        </div>
      )}

      <div className="row-end">
        <button className="btn primary" disabled={props.locked || !dirty || !ready || busy !== null} onClick={() => void apply()}>
          {busy === "screen" ? "Sending…" : "Apply to screen"}
        </button>
      </div>
    </>
  );
}

function LcdControls(props: { device: CoolingDevice; locked: boolean; onApplied: (d: CoolingDevice) => void }) {
  const { device } = props;
  const { run } = useAction();
  if (!device.lcd) return null;
  const set = async (patch: { brightness?: number; orientation?: number }) => {
    const r = await run("lcd", () => call<CoolingDevice>("cooling.setLcd", { deviceId: device.id, ...patch }));
    if (r) props.onApplied(r);
  };
  return (
    <div className="inset stack">
      <h3>Screen on the pump</h3>
      <div className="field">
        <div className="field-label">Brightness</div>
        <div className="field-control">
          <Slider
            label="Screen brightness"
            min={0}
            max={100}
            value={device.lcd.brightness}
            disabled={props.locked}
            format={(v) => `${v}%`}
            onCommit={(v) => void set({ brightness: v })}
          />
        </div>
      </div>
      <div className="field">
        <div className="field-label">Rotation</div>
        <div className="field-control">
          <Segmented
            label="Screen rotation"
            value={device.lcd.orientation}
            disabled={props.locked}
            options={[0, 90, 180, 270].map((o) => ({ value: o, label: `${o}°` }))}
            onChange={(o) => void set({ orientation: o })}
          />
        </div>
      </div>
      {device.screen && <ScreenContent device={device} screen={device.screen} locked={props.locked} onApplied={props.onApplied} />}
    </div>
  );
}

function DevicePanel(props: { device: CoolingDevice; liquidTempC?: number | null; locked: boolean; onApplied: (d: CoolingDevice) => void }) {
  const d = props.device;
  const unit = useTempUnit();
  return (
    <section className="panel" aria-label={d.name}>
      <div className="panel-head">
        <div>
          <h2>{d.name}</h2>
          <div className="sub">
            {d.product}
            {d.firmware ? `, firmware ${d.firmware}` : ""}
            {!d.connected ? ", not connected" : ""}
          </div>
        </div>
        {d.liquidTempC !== null && (
          <div style={{ textAlign: "right" }}>
            <div className="sub">Liquid</div>
            <div className="meter-value" style={{ color: thermalColor(d.liquidTempC, BANDS.liquid) }}>
              {fmtTemp(d.liquidTempC, unit, 1)}
              <small>{unitLabel(unit)}</small>
            </div>
          </div>
        )}
      </div>
      {d.notes.map((n) => (
        <div key={n} className="sub">
          {n}
        </div>
      ))}
      <div className="channel-grid">
        {d.channels.map((c) => (
          <ChannelEditor key={c.id} device={d} channel={c} liquidTempC={props.liquidTempC} locked={props.locked || !d.connected} onApplied={props.onApplied} />
        ))}
      </div>
      <LcdControls device={d} locked={props.locked || !d.connected} onApplied={props.onApplied} />
    </section>
  );
}

// ---------------------------------------------------------------- motherboard headers

function HeaderEditor(props: {
  fan: FanControl;
  sources: TemperatureSource[];
  temps: Map<string, number | null>;
  rpm: number | null;
  onApplied: (f: FanControl) => void;
}) {
  const { fan } = props;
  const { busy, run } = useAction();
  const range = { min: 20, max: 100 };
  const defaultSource = props.sources.find((s) => /package|tctl|cpu/i.test(s.name))?.id ?? props.sources[0]?.id ?? "";
  const [mode, setMode] = useState<FanControl["mode"]>(fan.mode);
  const [fixed, setFixed] = useState(Math.max(fan.minDutyPercent, fan.dutyPercent ?? 50));
  const [sourceId, setSourceId] = useState(fan.curve?.sourceId ?? defaultSource);
  const [points, setPoints] = useState<CurvePoint[]>(fan.curve?.points ?? presetCurve("balanced", { min: 35, max: 85 }, fan.minDutyPercent));
  const [dirty, setDirty] = useState(false);
  const curveError = mode === "curve" ? validateCurve(points, fan.minDutyPercent, range) : null;
  const sourceName = props.sources.find((s) => s.id === sourceId)?.name ?? "a temperature";

  const apply = async () => {
    const r =
      mode === "auto"
        ? await run("apply", () => call<FanControl>("sensors.setFanAuto", { id: fan.id }), `${fan.name} handed back to the BIOS`)
        : mode === "fixed"
          ? await run("apply", () => call<FanControl>("sensors.setFanFixed", { id: fan.id, dutyPercent: fixed }), `${fan.name} set to ${fixed}%`)
          : await run("apply", () => call<FanControl>("sensors.setFanCurve", { id: fan.id, sourceId, points }), `${fan.name} curve applied`);
    if (r) {
      setDirty(false);
      props.onApplied(r);
    }
  };

  return (
    <div className="inset stack">
      <div className="spread">
        <div>
          <h3>{fan.name}</h3>
          <div className="sub">
            {props.rpm != null ? `${props.rpm.toLocaleString()} rpm` : "No speed reading"}
            {fan.mode === "auto" ? ", controlled by the BIOS" : fan.dutyPercent != null ? `, running at ${fan.dutyPercent}%` : ""}
          </div>
        </div>
        <Segmented
          label={`${fan.name} mode`}
          value={mode}
          disabled={!fan.canControl}
          options={[
            { value: "auto", label: "BIOS" },
            { value: "curve", label: "Curve" },
            { value: "fixed", label: "Fixed speed" },
          ]}
          onChange={(m) => {
            setMode(m);
            setDirty(true);
          }}
        />
      </div>
      {!fan.canControl && fan.reason && <div className="sub">{fan.reason}</div>}
      {mode === "fixed" && (
        <Slider
          label={`${fan.name} speed`}
          min={fan.minDutyPercent}
          max={100}
          value={fixed}
          disabled={!fan.canControl}
          format={(v) => `${v}%`}
          onChange={(v) => {
            setFixed(v);
            setDirty(true);
          }}
        />
      )}
      {mode === "curve" && (
        <>
          <div className="row">
            <span className="sub">Follows</span>
            <select
              className="select"
              value={sourceId}
              disabled={!fan.canControl}
              onChange={(e) => {
                setSourceId(e.target.value);
                setDirty(true);
              }}
            >
              {props.sources.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.hardware})
                </option>
              ))}
            </select>
          </div>
          <CurveEditor
            points={points}
            onChange={(p) => {
              setPoints(p);
              setDirty(true);
            }}
            range={range}
            minDuty={fan.minDutyPercent}
            currentTempC={props.temps.get(sourceId) ?? null}
            sourceLabel={sourceName}
            disabled={!fan.canControl}
          />
        </>
      )}
      {mode !== "auto" && (
        <div className="sub faint">
          RigControl runs this while it is open. When you exit RigControl the header goes back to the BIOS.
        </div>
      )}
      <div className="row" style={{ justifyContent: "flex-end" }}>
        {curveError && <span className="sub" style={{ color: "var(--hot)", marginRight: "auto" }}>{curveError}</span>}
        <button className="btn primary" disabled={!fan.canControl || !dirty || busy !== null || curveError !== null} onClick={() => void apply()}>
          {busy ? "Applying…" : "Apply"}
        </button>
      </div>
    </div>
  );
}

/** The graphics card's own fans: automatic (the card decides), a fixed speed, or a curve RigControl runs. */
function GpuFans() {
  const fans = useQuery<FanControl[]>("sensors.getGpuFans");
  const sources = useQuery<TemperatureSource[]>("sensors.getTemperatureSources");
  const { data: snap } = useLive("sensors.snapshot", "sensors.getSnapshot");
  const temps = useMemo(() => new Map((snap?.temperatures ?? []).map((t) => [t.id, t.value])), [snap]);
  const rpmFor = (f: FanControl) => (f.rpmSensorId ? (snap?.fans.find((x) => x.id === f.rpmSensorId)?.rpm ?? null) : null);
  if (!fans.data || fans.data.length === 0) return null;

  return (
    <section className="panel" aria-label="Graphics card fans">
      <div className="panel-head">
        <div>
          <h2>Graphics card fans</h2>
          <div className="sub">
            {snap?.gpu?.name ?? "Graphics card"}. On automatic the card decides and may stop its fans when cool. A fixed speed or a
            curve is run by RigControl; when RigControl closes, the card goes back to automatic.
          </div>
        </div>
      </div>
      <div className="channel-grid">
        {fans.data.map((f) => (
          <HeaderEditor
            key={f.id}
            fan={f}
            sources={sources.data ?? []}
            temps={temps}
            rpm={rpmFor(f)}
            onApplied={(nf) => fans.setData((cur) => (cur ?? []).map((x) => (x.id === nf.id ? nf : x)))}
          />
        ))}
      </div>
    </section>
  );
}

function MotherboardFans() {
  const { info, restartAsAdmin } = useApp();
  const fans = useQuery<FanControl[]>("sensors.getFanControls");
  const sources = useQuery<TemperatureSource[]>("sensors.getTemperatureSources");
  const { data: snap } = useLive("sensors.snapshot", "sensors.getSnapshot");

  const temps = useMemo(() => new Map((snap?.temperatures ?? []).map((t) => [t.id, t.value])), [snap]);
  const rpmFor = (f: FanControl) => (f.rpmSensorId ? (snap?.fans.find((x) => x.id === f.rpmSensorId)?.rpm ?? null) : null);

  return (
    <section className="panel" aria-label="Motherboard fan headers">
      <div className="panel-head">
        <div>
          <h2>Motherboard fan headers</h2>
          <div className="sub">{snap?.board?.name ?? "Motherboard"}</div>
        </div>
      </div>
      {!info?.isElevated ? (
        <Notice
          tone="warn"
          title="Run as administrator to control motherboard fans"
          actions={
            <button className="btn" onClick={() => void restartAsAdmin()}>
              Restart as administrator
            </button>
          }
        >
          Windows only lets programs running as administrator talk to the motherboard's fan chip.
        </Notice>
      ) : fans.error ? (
        <Notice tone="error" title="Motherboard fans are not available">
          {fans.error}
        </Notice>
      ) : !fans.data ? (
        <div className="empty">Reading fan headers…</div>
      ) : fans.data.length === 0 ? (
        <div className="empty">No controllable fan headers were found.</div>
      ) : (
        <div className="channel-grid">
        {fans.data.map((f) => (
          <HeaderEditor
            key={f.id}
            fan={f}
            sources={sources.data ?? []}
            temps={temps}
            rpm={rpmFor(f)}
            onApplied={(nf) => fans.setData((cur) => (cur ?? []).map((x) => (x.id === nf.id ? nf : x)))}
          />
        ))}
        </div>
      )}
    </section>
  );
}

// ---------------------------------------------------------------- page

/** ASUS Armoury Crate: shows whether its background parts are running and lets the user stop them. */
export function ArmouryCratePanel() {
  const state = useQuery<ArmouryState>("sensors.getArmouryCrate");
  const { busy, run } = useAction();
  const [confirm, setConfirm] = useState(false);
  const [stopAutoStart, setStopAutoStart] = useState(true);
  const a = state.data;
  if (!a || !a.installed) return null;

  const close = async () => {
    setConfirm(false);
    const r = await run(
      "armoury",
      () => call<ArmouryState>("sensors.closeArmouryCrate", { disableAutoStart: stopAutoStart }, 90000),
      "Armoury Crate stopped. RigControl now has the motherboard's fans and lights to itself.",
    );
    if (r) state.setData(r);
    else state.reload();
  };
  const setAutoStart = async (enabled: boolean) => {
    const r = await run(
      "armourystart",
      () => call<ArmouryState>("sensors.setArmouryCrateAutoStart", { enabled }, 60000),
      enabled ? "Armoury Crate will start by itself again." : "Armoury Crate will no longer start by itself.",
    );
    if (r) state.setData(r);
  };
  const active = a.running.length > 0;

  return (
    <section className="panel" aria-label="ASUS Armoury Crate">
      <div className="panel-head">
        <div>
          <h2>ASUS Armoury Crate</h2>
          <div className="sub">
            {active ? `Running: ${a.running.join(", ")}` : "Not running. RigControl has the motherboard's fans and lights to itself."}
          </div>
        </div>
        {active && (
          <button className="btn primary" disabled={busy !== null} onClick={() => setConfirm(true)}>
            {busy === "armoury" ? "Stopping…" : "Take over from Armoury Crate"}
          </button>
        )}
      </div>
      <p className="sub">
        RigControl does Armoury Crate's jobs for this board itself: fan headers are just above, and the board's Aura lights are on the
        Lighting page. Two programs sending fan speeds or colours at once makes both misbehave, so only one should run.
      </p>
      <Switch
        checked={a.startsWithWindows}
        disabled={busy !== null}
        onChange={(v) => void setAutoStart(v)}
        label="Let Armoury Crate start by itself with Windows"
      />
      {confirm && (
        <Dialog
          title="Take over from Armoury Crate?"
          onClose={() => setConfirm(false)}
          actions={
            <>
              <button className="btn" onClick={() => setConfirm(false)}>
                Cancel
              </button>
              <button className="btn primary" onClick={() => void close()}>
                Stop Armoury Crate
              </button>
            </>
          }
        >
          <div className="stack">
            <span>
              RigControl stops Armoury Crate's background programs and services. Fans keep running on the BIOS settings until you set
              them here. Armoury Crate stays installed and you can switch its automatic start back on any time.
            </span>
            <Switch checked={stopAutoStart} onChange={setStopAutoStart} label="Also stop Armoury Crate starting with Windows" />
            <span className="faint">This needs RigControl to be running as administrator.</span>
          </div>
        </Dialog>
      )}
    </section>
  );
}

export function Cooling() {
  const { data: status, setData, error } = useLive("cooling.status", "cooling.getDevices");
  const { busy, run } = useAction();
  const [takeover, setTakeover] = useState(false);
  const [stopAutoStart, setStopAutoStart] = useState(true);

  const blocked = (status?.blockedBy.length ?? 0) > 0;
  const applyDevice = (d: CoolingDevice) =>
    setData((cur: CoolingStatus | null) => (cur ? { ...cur, devices: cur.devices.map((x) => (x.id === d.id ? d : x)) } : cur));

  const closeCam = async () => {
    setTakeover(false);
    const r = await run(
      "cam",
      () => call<CoolingStatus>("cooling.closeCam", { disableAutoStart: stopAutoStart }, 60000),
      "NZXT CAM closed. RigControl is now in charge of cooling.",
    );
    if (r) setData(r);
  };

  const setCamAutoStart = async (enabled: boolean) => {
    const r = await run(
      "camstart",
      () => call<CoolingStatus>("cooling.setCamAutoStart", { enabled }),
      enabled ? "NZXT CAM will start with Windows again." : "NZXT CAM will no longer start with Windows.",
    );
    if (r) setData(r);
  };

  return (
    <div className="page">
      <div className="page-head">
        <h1>Cooling</h1>
        <button className="btn" disabled={busy !== null} onClick={() => void run("rescan", () => call<CoolingStatus>("cooling.rescan").then(setData))}>
          Look for devices again
        </button>
      </div>

      {!blocked && <ModuleProblem name="cooling" />}
      {/* The notice below already explains NZXT CAM and has the take-over button. */}
      <HealthNotice modules={["cooling", "sensors"]} exclude={blocked && status!.blockedBy.includes("NZXT CAM") ? ["cooling.nzxt-cam"] : undefined} />

      {blocked && (
        <Notice
          tone="warn"
          title={`${status!.blockedBy.join(" and ")} is controlling your cooler`}
          actions={
            status!.blockedBy.includes("NZXT CAM") ? (
              <button className="btn primary" disabled={busy !== null} onClick={() => setTakeover(true)}>
                {busy === "cam" ? "Closing…" : "Take over from NZXT CAM"}
              </button>
            ) : undefined
          }
        >
          While it is open, RigControl only shows readings, so two programs never fight over your pump. Take over to set curves and
          speeds here.
        </Notice>
      )}

      {status && !blocked && status.cam.startsWithWindows && (
        <Notice
          title="NZXT CAM will take over again when you next sign in"
          actions={
            <button className="btn" disabled={busy !== null} onClick={() => void setCamAutoStart(false)}>
              Stop CAM starting with Windows
            </button>
          }
        >
          It is still set to start with Windows. While it runs, RigControl goes back to showing readings only.
        </Notice>
      )}
      {status && !blocked && !status.cam.startsWithWindows && status.devices.length > 0 && (
        <div className="sub">
          NZXT CAM no longer starts with Windows, so RigControl stays in charge of cooling.{" "}
          <button className="btn ghost small" disabled={busy !== null} onClick={() => void setCamAutoStart(true)}>
            Let CAM start with Windows again
          </button>
        </div>
      )}

      {error && !status && <Notice tone="error" title="Cooling devices are not available">{error}</Notice>}
      {status && status.devices.length === 0 && (
        <div className="panel">
          <div className="empty">
            No NZXT cooler or fan controller was found. RigControl supports the NZXT Kraken Elite (2023) and the NZXT RGB &amp; Fan
            Controller; if you have one, check its USB cable to the motherboard. The graphics card and motherboard fans below work
            without them.
          </div>
        </div>
      )}
      {status?.devices.map((d) => (
        <DevicePanel
          key={d.id}
          device={d}
          liquidTempC={status.devices.find((x) => x.liquidTempC != null)?.liquidTempC ?? null}
          locked={blocked}
          onApplied={applyDevice}
        />
      ))}

      <GpuFans />
      <MotherboardFans />
      <ArmouryCratePanel />
      {takeover && (
        <Dialog
          title="Take over from NZXT CAM?"
          onClose={() => setTakeover(false)}
          actions={
            <>
              <button className="btn" onClick={() => setTakeover(false)}>
                Cancel
              </button>
              <button className="btn primary" onClick={() => void closeCam()}>
                Close CAM and take over
              </button>
            </>
          }
        >
          <div className="stack">
            <span>
              RigControl closes NZXT CAM and its background service so the two programs don't both send speeds to your cooler. Your pump
              and fans keep their current speeds until you apply new ones here. CAM stays installed and you can open it again any time.
            </span>
            <Switch checked={stopAutoStart} onChange={setStopAutoStart} label="Also stop NZXT CAM starting with Windows" />
            <span className="faint">This needs RigControl to be running as administrator.</span>
          </div>
        </Dialog>
      )}
    </div>
  );
}

import { useEffect, useState } from "react";
import { call } from "../bridge";
import { useAction } from "../hooks";
import { Notice, Segmented, Slider, useConfirm } from "./controls";
import type {
  CpuPower as CpuPowerData,
  CpuPowerKey,
  CpuPowerSetting,
} from "../types";

type Source = "ac" | "dc";
type Draft = Partial<Record<CpuPowerKey, number>>;

const TEXT: Record<CpuPowerKey, { label: string; hint: string }> = {
  boostMode: {
    label: "Boost",
    hint: "Lets the processor run faster than its normal top speed for short bursts. Off runs cooler and quieter, and a little slower.",
  },
  maxStatePercent: {
    label: "Most speed it may use",
    hint: "Windows calls this the maximum processor state. Below 100 % the processor runs cooler; on most PCs 99 % also switches boost off.",
  },
  minStatePercent: {
    label: "Least speed when idle",
    hint: "Windows calls this the minimum processor state. Lower saves power while the PC does little.",
  },
  maxFrequencyMHz: {
    label: "Speed limit",
    hint: "A fixed top clock speed in MHz. 4000 means the processor never goes above 4.0 GHz.",
  },
  minCoresPercent: {
    label: "Cores kept awake",
    hint: "Windows may put idle cores to sleep (core parking). 100 % keeps every core awake, which can smooth out games and uses a little more power.",
  },
};

function valueOf(s: CpuPowerSetting, source: Source): number | null {
  return source === "ac" ? s.ac : s.dc;
}

function original(s: CpuPowerSetting, source: Source): number | null {
  return source === "ac" ? s.originalAc : s.originalDc;
}

function show(s: CpuPowerSetting, v: number): string {
  if (s.options)
    return s.options.find((o) => o.value === v)?.label ?? String(v);
  if (s.zeroMeansNoLimit && v === 0) return "no limit";
  return `${v} ${s.unit}`;
}

function Control(props: {
  setting: CpuPowerSetting;
  value: number;
  disabled: boolean;
  onChange: (v: number) => void;
}) {
  const { setting: s, value } = props;
  const label = TEXT[s.key].label;
  if (s.options) {
    return (
      <select
        className="select"
        value={value}
        aria-label={label}
        disabled={props.disabled}
        onChange={(e) => props.onChange(Number(e.target.value))}
      >
        {s.options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    );
  }
  if (s.zeroMeansNoLimit) {
    const off = value === 0;
    return (
      <>
        <select
          className="select"
          style={{ minWidth: 120 }}
          value={off ? "off" : "set"}
          aria-label={`${label}: on or off`}
          disabled={props.disabled}
          onChange={(e) => props.onChange(e.target.value === "off" ? 0 : 4000)}
        >
          <option value="off">No limit</option>
          <option value="set">Set</option>
        </select>
        {!off && (
          <>
            <input
              className="input num"
              type="number"
              min={s.min}
              max={s.max}
              step={100}
              value={value}
              aria-label={label}
              style={{ width: 100 }}
              disabled={props.disabled}
              onChange={(e) =>
                props.onChange(Math.round(Number(e.target.value) || 0))
              }
            />
            <span className="sub">{s.unit}</span>
          </>
        )}
      </>
    );
  }
  return (
    <Slider
      label={label}
      min={s.min}
      max={s.max}
      value={Math.min(s.max, Math.max(s.min, value))}
      disabled={props.disabled}
      format={(v) => `${v} %`}
      onChange={props.onChange}
    />
  );
}

/** Tuning through Windows' own power plan settings: no driver, nothing written to the processor, and it can be put back. */
export function CpuPower({
  power,
  onPower,
}: {
  power: CpuPowerData;
  onPower: (p: CpuPowerData) => void;
}) {
  const { busy, run } = useAction();
  const { confirm, element } = useConfirm();
  const [source, setSource] = useState<Source>("ac");
  const [draft, setDraft] = useState<Draft>({});
  const shown: Source = power.hasBattery ? source : "ac";

  // A new answer from the app (after a save, or another plan) starts the form again from what is really set.
  useEffect(
    () => setDraft({}),
    [JSON.stringify(power.settings), power.planId, shown],
  );

  if (!power.available) {
    return (
      <section className="panel" aria-label="Speed and power">
        <div className="panel-head">
          <div>
            <h2>Speed and power</h2>
          </div>
        </div>
        <Notice tone="warn" title="Windows' power plan could not be read">
          {power.problem ?? "No details were given."}
        </Notice>
      </section>
    );
  }

  const usable = power.settings.filter(
    (s) => s.available && valueOf(s, shown) != null,
  );
  const current = (s: CpuPowerSetting) =>
    draft[s.key] ?? valueOf(s, shown) ?? 0;
  const edited = usable.filter(
    (s) => draft[s.key] != null && draft[s.key] !== valueOf(s, shown),
  );
  const min = usable.find((s) => s.key === "minStatePercent");
  const max = usable.find((s) => s.key === "maxStatePercent");
  const freq = usable.find((s) => s.key === "maxFrequencyMHz");
  const problems: string[] = [];
  if (min && max && current(min) > current(max))
    problems.push("The least speed can't be higher than the most speed.");
  if (
    freq &&
    current(freq) !== 0 &&
    (current(freq) < freq.min || current(freq) > freq.max)
  )
    problems.push(
      `The speed limit must be between ${freq.min} and ${freq.max} MHz.`,
    );
  const differ = usable.some((s) => s.coreTypesDiffer);
  const missing = power.settings.filter((s) => !s.available);

  const save = async () => {
    const values: Draft = {};
    for (const s of edited) values[s.key] = draft[s.key];
    const r = await run(
      "save",
      () => call<CpuPowerData>("cpu.setPower", { [shown]: values }),
      "Saved to the power plan",
    );
    if (r) onPower(r);
  };

  const reset = async () => {
    const plans =
      power.changedPlans.length > 0
        ? power.changedPlans.map((p) => `"${p}"`).join(", ")
        : `"${power.planName}"`;
    const yes = await confirm({
      title: "Put the power plan back how it was?",
      body: `Every value RigControl changed in ${plans} goes back to what it was before the first change.`,
      confirmLabel: "Put back",
    });
    if (!yes) return;
    const r = await run(
      "reset",
      () => call<CpuPowerData>("cpu.resetPower"),
      "Put back how it was",
    );
    if (r) onPower(r);
  };

  return (
    <section className="panel" aria-label="Speed and power">
      <div className="panel-head">
        <div>
          <h2>Speed and power</h2>
          <div className="sub">
            These are Windows' own settings in the power plan in use now, "
            {power.planName}". No driver is used and nothing is written to the
            processor itself. They stay set when RigControl is closed, and you
            can put them back at any time.
          </div>
        </div>
        {power.hasBattery && (
          <Segmented
            label="Power source"
            value={shown}
            options={[
              { value: "ac", label: "Plugged in" },
              { value: "dc", label: "On battery" },
            ]}
            onChange={setSource}
          />
        )}
      </div>
      {differ && (
        <Notice title="The fast and the efficient cores have different values now">
          Windows keeps these settings once for each kind of core. The values
          shown are for the efficient cores; saving a setting gives both kinds
          the same value.
        </Notice>
      )}
      {usable.map((s) => {
        const was = original(s, shown);
        const now = valueOf(s, shown);
        return (
          <div className="field" key={s.key}>
            <div className="field-label">
              <span>
                {TEXT[s.key].label}
                {was != null && was !== now && (
                  <span className="badge">was {show(s, was)}</span>
                )}
              </span>
              <span className="sub">{TEXT[s.key].hint}</span>
            </div>
            <div className="field-control">
              <Control
                setting={s}
                value={current(s)}
                disabled={busy !== null}
                onChange={(v) => setDraft({ ...draft, [s.key]: v })}
              />
            </div>
          </div>
        );
      })}
      {missing.length > 0 && (
        <div className="sub faint">
          Windows on this PC does not have:{" "}
          {missing.map((s) => TEXT[s.key].label.toLowerCase()).join(", ")}.
        </div>
      )}
      {problems.map((p) => (
        <Notice key={p} tone="warn" title={p} />
      ))}
      <div className="row-end" style={{ gap: 8, marginTop: 12 }}>
        {power.changed && (
          <button
            className="btn"
            disabled={busy !== null}
            onClick={() => void reset()}
          >
            {busy === "reset" ? "Putting back…" : "Put back how it was"}
          </button>
        )}
        <button
          className="btn primary"
          disabled={busy !== null || edited.length === 0 || problems.length > 0}
          onClick={() => void save()}
        >
          {busy === "save" ? "Saving…" : "Save"}
        </button>
      </div>
      {element}
    </section>
  );
}

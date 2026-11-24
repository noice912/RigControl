import { useEffect, useState } from "react";
import { call } from "../bridge";
import { useAction } from "../hooks";
import { Dialog, Notice } from "./controls";
import type { BiosStatus, CpuEdits } from "../types";

const PROFILES: { value: string; label: string; note: string }[] = [
  { value: "intelPerformance", label: "Intel Default: Performance", note: "Intel's 253 W limits with a lower current limit. The safest choice for this chip." },
  { value: "intelExtreme", label: "Intel Default: Extreme", note: "Intel's 253 W limits with the higher current limit." },
  { value: "asus", label: "ASUS Advanced OC Profile", note: "Your own power limits below. Leaves Intel's baseline." },
];

function NumField(props: { label: string; hint: string; value: number; min: number; max: number; unit: string; autoLabel: string; onChange: (v: number) => void }) {
  const auto = props.value === 0;
  return (
    <div className="field">
      <div className="field-label">
        <span>{props.label}</span>
        <span className="sub">{props.hint}</span>
      </div>
      <div className="field-control" style={{ display: "flex", gap: 8, alignItems: "center" }}>
        <select className="select" value={auto ? "auto" : "set"} aria-label={`${props.label} mode`} onChange={(e) => props.onChange(e.target.value === "auto" ? 0 : props.min)}>
          <option value="auto">{props.autoLabel}</option>
          <option value="set">Set</option>
        </select>
        {!auto && (
          <>
            <input
              className="input num"
              type="number"
              min={props.min}
              max={props.max}
              value={props.value}
              aria-label={props.label}
              style={{ width: 90 }}
              onChange={(e) => props.onChange(Math.round(Number(e.target.value) || 0))}
            />
            <span className="sub">{props.unit}</span>
          </>
        )}
      </div>
    </div>
  );
}

/** Changing the CPU settings the BIOS stores (written straight into them; they apply at the next restart). */
export function CpuEditor({ status, onStatus }: { status: BiosStatus; onStatus: (s: BiosStatus) => void }) {
  const { busy, run } = useAction();
  const cpu = status.cpu;
  const [draft, setDraft] = useState<CpuEdits | null>(cpu);
  const [confirm, setConfirm] = useState(false);
  const [restoreAsk, setRestoreAsk] = useState(false);
  useEffect(() => setDraft(cpu), [JSON.stringify(cpu)]);
  if (!cpu || !draft) return status.cpuProblem ? <Notice tone="warn" title="CPU settings can't be changed">{status.cpuProblem}</Notice> : null;

  const changed = (["profile", "pl1", "pl2", "undervoltMv", "allCoreRatio"] as const).some((k) => draft[k] !== cpu[k]);
  const patch = (p: Partial<CpuEdits>) => setDraft({ ...draft, ...p, profile: (p.pl1 || p.pl2) && draft.profile !== "asus" ? "asus" : (p.profile ?? draft.profile) });
  const save = async () => {
    setConfirm(false);
    const r = await run("cpu", () => call<BiosStatus>("bios.applyCpu", { profile: draft.profile, pl1: draft.pl1, pl2: draft.pl2, undervoltMv: draft.undervoltMv, allCoreRatio: draft.allCoreRatio }), "Saved to the BIOS. Restart to apply.");
    if (r) onStatus(r);
  };
  const test = async () => {
    const r = await run("test", () => call<BiosStatus>("bios.testWrite"), "The BIOS accepts changes from RigControl");
    if (r) onStatus(r);
  };
  const restore = async () => {
    setRestoreAsk(false);
    const r = await run("restore", () => call<BiosStatus>("bios.restoreOriginal"), "Original settings put back. Restart to apply.");
    if (r) onStatus(r);
  };
  const profileNote = PROFILES.find((p) => p.value === draft.profile)?.note;

  return (
    <section className="panel" aria-label="Change CPU settings">
      <div className="panel-head">
        <div>
          <h2>Change CPU settings</h2>
          <div className="sub">
            Written into the BIOS's own settings, so they stay until you change them, even without RigControl. They apply at the next restart. Voltage can only go
            down: this chip can wear out from high voltage.
          </div>
        </div>
      </div>
      {status.restartNeeded && (
        <Notice tone="warn" title="Restart to apply">
          {status.lastChanges?.join("; ")}. If the PC doesn't start after the restart, the BIOS usually undoes overclock settings by itself after a few tries;
          otherwise press the board's Clear CMOS button, or open the BIOS and press F5 (load defaults).
        </Notice>
      )}
      {status.writable === false && (
        <Notice tone="warn" title="The BIOS doesn't accept changes from Windows">
          {status.writeProblem}. Change these in the BIOS itself (Del at start, Ai Tweaker).
        </Notice>
      )}
      {status.writable == null && (
        <Notice
          title="First, check that the BIOS accepts changes"
          actions={
            <button className="btn" disabled={busy !== null} onClick={() => void test()}>
              {busy === "test" ? "Checking…" : "Check"}
            </button>
          }
        >
          This writes the settings back exactly as they are and reads them again. Nothing changes.
        </Notice>
      )}
      {cpu.notes.map((n) => (
        <div key={n} className="sub">
          {n}
        </div>
      ))}
      <div className="field">
        <div className="field-label">
          <span>Profile</span>
          <span className="sub">{profileNote}</span>
        </div>
        <div className="field-control">
          <select className="select" value={draft.profile} aria-label="Profile" onChange={(e) => setDraft({ ...draft, profile: e.target.value, ...(e.target.value !== "asus" ? { pl1: 0, pl2: 0 } : {}) })}>
            {PROFILES.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
        </div>
      </div>
      <NumField label="Long-term power limit (PL1)" hint="35 to 253 W. Lower runs cooler and quieter." value={draft.pl1} min={35} max={253} unit="W" autoLabel="Auto" onChange={(v) => patch({ pl1: v })} />
      <NumField label="Short-term power limit (PL2)" hint="35 to 253 W, at least PL1." value={draft.pl2} min={35} max={253} unit="W" autoLabel="Auto" onChange={(v) => patch({ pl2: v })} />
      <NumField
        label="Undervolt"
        hint="Takes up to 150 mV off the core voltage (Offset Mode). Start small (20-30 mV); crashes mean too much."
        value={draft.undervoltMv}
        min={5}
        max={150}
        unit="mV off"
        autoLabel="Off (Auto)"
        onChange={(v) => setDraft({ ...draft, undervoltMv: v })}
      />
      <NumField
        label="All P-cores at"
        hint="One ratio for every P-core (Sync All Cores): 55 = 5.5 GHz. Above about 55 is an overclock for this chip and adds heat and wear."
        value={draft.allCoreRatio}
        min={30}
        max={57}
        unit="× 100 MHz"
        autoLabel="Auto"
        onChange={(v) => setDraft({ ...draft, allCoreRatio: v })}
      />
      <div className="row" style={{ gap: 8 }}>
        <button className="btn primary" disabled={busy !== null || !changed || status.writable === false} onClick={() => setConfirm(true)}>
          Save to BIOS
        </button>
        {changed && (
          <button className="btn" disabled={busy !== null} onClick={() => setDraft(cpu)}>
            Undo
          </button>
        )}
        {status.originalBackup && (
          <button className="btn" disabled={busy !== null || status.writable === false} onClick={() => setRestoreAsk(true)}>
            Put back the original settings
          </button>
        )}
      </div>
      {confirm && (
        <Dialog
          title="Save these CPU settings to the BIOS?"
          onClose={() => setConfirm(false)}
          actions={
            <>
              <button className="btn" onClick={() => setConfirm(false)}>Cancel</button>
              <button className="btn primary" onClick={() => void save()}>Save to BIOS</button>
            </>
          }
        >
          They apply at the next restart and stay until changed. A copy of the current settings is kept, and "Put back the original settings" restores how they
          were before RigControl changed anything.
        </Dialog>
      )}
      {restoreAsk && (
        <Dialog
          title="Put back the original BIOS settings?"
          onClose={() => setRestoreAsk(false)}
          actions={
            <>
              <button className="btn" onClick={() => setRestoreAsk(false)}>Cancel</button>
              <button className="btn primary" onClick={() => void restore()}>Put back</button>
            </>
          }
        >
          Every BIOS setting in this group goes back to how it was before RigControl changed anything. Applies at the next restart.
        </Dialog>
      )}
    </section>
  );
}

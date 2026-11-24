import { useMemo, useRef, useState } from "react";
import { useLive, useQuery } from "../hooks";
import { useModule } from "../appContext";
import { Loading, Meter, Notice } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import { CpuEditor } from "../components/CpuEditor";
import { CpuPower } from "../components/CpuPower";
import { BANDS, fmt, thermalFraction } from "../thermal";
import { fmtTemp, unitLabel, useTempUnit } from "../units";
import type {
  BiosSetting,
  BiosStatus,
  CpuInfo,
  CpuPower as CpuPowerData,
} from "../types";

function valueText(s: BiosSetting): string {
  if (s.label) return s.label;
  if (s.options && s.options.length > 0) return `Unknown (${s.value})`;
  return String(s.value);
}

function SettingsTable({ rows }: { rows: BiosSetting[] }) {
  return (
    <table className="fan-table">
      <thead>
        <tr>
          <th>Setting</th>
          <th>Now</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((s) => (
          <tr key={`${s.offset}`} title={s.help ?? undefined}>
            <td>{s.name}</td>
            <td>
              {valueText(s)}
              {!s.valueKnown && (
                <span className="badge warn" style={{ marginLeft: 8 }}>
                  Not in the map
                </span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function coresText(info: CpuInfo): string {
  if (info.cores <= 0) return info.threads > 0 ? `${info.threads} threads` : "";
  const split =
    info.performanceCores != null && info.efficiencyCores != null
      ? ` (${info.performanceCores} fast, ${info.efficiencyCores} efficient)`
      : "";
  return `${info.cores} cores${split}, ${info.threads} threads`;
}

/** Who the processor is: its own name, and what RigControl's list knows about that kind of chip. */
function Identity({ info }: { info: CpuInfo }) {
  const id =
    info.family > 0
      ? `Family ${info.family}, model ${info.model}, stepping ${info.stepping}`
      : null;
  return (
    <section className="panel" aria-label="Processor">
      <div className="panel-head">
        <div>
          <h2>{info.name}</h2>
          <div className="sub">
            {[info.vendor === "Other" ? null : info.vendor, coresText(info)]
              .filter(Boolean)
              .join(" · ")}
          </div>
        </div>
      </div>
      {info.known ? (
        <div className="field">
          <div className="field-label">
            <span>{info.generation}</span>
            <span className="sub">
              {[
                info.codeName ? `Code name ${info.codeName}` : null,
                info.line,
                id,
              ]
                .filter(Boolean)
                .join(" · ")}
            </span>
          </div>
        </div>
      ) : (
        <div className="field">
          <div className="field-label">
            <span>Not on this version's list</span>
            <span className="sub">
              {info.summary}. RigControl's list of processor generations does
              not have it, so the name above comes from the processor itself.
              {id ? ` ${id}.` : ""}
            </span>
          </div>
        </div>
      )}
      {info.note && (
        <Notice
          tone="warn"
          title={
            info.vendor === "AMD" ? "AMD: built but not tested" : "Not tested"
          }
        >
          {info.note}. The readings and the settings below use Windows and the
          sensors only; nothing is written to the processor itself.
        </Notice>
      )}
    </section>
  );
}

/** Live numbers, straight from the sensors (the same ones the Overview page shows). */
function Readings() {
  const { data: snap } = useLive("sensors.snapshot", "sensors.getSnapshot");
  const unit = useTempUnit();
  const cpu = snap?.cpu;
  // The power bar is scaled to the most this processor has drawn since the page opened (at least 35 W), not to
  // a fixed figure: a laptop chip and a workstation chip are a factor of twenty apart.
  const peakW = useRef(0);
  if (cpu?.powerW != null && cpu.powerW > peakW.current)
    peakW.current = cpu.powerW;
  return (
    <section className="panel" aria-label="Readings">
      <div className="panel-head">
        <div>
          <h2>Right now</h2>
          {snap && !snap.elevated && (
            <div className="sub">
              Temperatures and power need RigControl to run as administrator.
            </div>
          )}
        </div>
      </div>
      {!snap ? (
        <Loading what="the sensors" />
      ) : !cpu ? (
        <div className="empty">
          The sensors have no processor readings on this PC.
        </div>
      ) : (
        <div className="meters">
          <Meter
            label="Temperature"
            value={fmtTemp(cpu.packageTempC, unit)}
            unit={unitLabel(unit)}
            fraction={thermalFraction(cpu.packageTempC, BANDS.cpu)}
            detail={
              cpu.coreMaxTempC != null
                ? `hottest core ${fmtTemp(cpu.coreMaxTempC, unit)}`
                : undefined
            }
          />
          <Meter
            label="Load"
            value={fmt(cpu.loadPercent)}
            unit="%"
            fraction={(cpu.loadPercent ?? 0) / 100}
          />
          <Meter
            label="Speed"
            value={
              cpu.clockMHz != null ? (cpu.clockMHz / 1000).toFixed(2) : "–"
            }
            unit="GHz"
            fraction={Math.min(1, (cpu.clockMHz ?? 0) / 6000)}
          />
          <Meter
            label="Power"
            value={fmt(cpu.powerW)}
            unit="W"
            fraction={Math.min(1, (cpu.powerW ?? 0) / Math.max(35, peakW.current))}
            detail={
              peakW.current > 0
                ? `highest since this page opened ${fmt(peakW.current)} W`
                : undefined
            }
          />
        </div>
      )}
    </section>
  );
}

/** The ASUS BIOS settings. Only drawn on the exact board and BIOS version RigControl has a map for. */
function BiosSection() {
  const q = useQuery<BiosStatus>("bios.getStatus");
  const [filter, setFilter] = useState("");
  const s = q.data;
  const groups = useMemo(() => {
    const f = filter.trim().toLowerCase();
    const rows = (s?.settings ?? []).filter(
      (x) => !f || x.name.toLowerCase().includes(f),
    );
    const by = new Map<string, BiosSetting[]>();
    for (const r of rows) by.set(r.form, [...(by.get(r.form) ?? []), r]);
    return [...by.entries()];
  }, [s, filter]);

  return (
    <>
      <ModuleProblem name="bios" />
      {q.error && (
        <Notice tone="warn" title="BIOS settings could not be read">
          {q.error}
        </Notice>
      )}
      {s && (
        <section className="panel" aria-label="BIOS">
          <div className="panel-head">
            <div>
              <h2>BIOS settings</h2>
              <div className="sub">
                {s.board ?? "Unknown board"}, BIOS {s.biosVersion ?? "unknown"}.
                Read straight from the settings the BIOS menu saves, using a map
                made from ASUS's own BIOS {s.biosVersion} file. Only the
                settings under "Change CPU settings" can be changed.
              </div>
            </div>
          </div>
          {s.problem && (
            <Notice tone="warn" title="Not read">
              {s.problem}
            </Notice>
          )}
          {s.backupPath && (
            <div className="sub faint">
              A copy of these settings is saved at {s.backupPath}
            </div>
          )}
          {s.settings.length > 0 && (
            <input
              className="input"
              style={{ maxWidth: 320 }}
              placeholder="Find a setting"
              aria-label="Find a setting"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
            />
          )}
        </section>
      )}
      {s && s.supported && !s.problem && (
        <CpuEditor status={s} onStatus={q.setData} />
      )}
      {groups.map(([form, rows]) => (
        <section className="panel" key={form} aria-label={form}>
          <div className="panel-head">
            <div>
              <h2>{form}</h2>
              <div className="sub">
                Numbers are shown as the BIOS stores them; their units are not
                confirmed yet.
              </div>
            </div>
          </div>
          <SettingsTable rows={rows} />
        </section>
      ))}
    </>
  );
}

export function Cpu() {
  const info = useQuery<CpuInfo>("cpu.getInfo");
  const power = useQuery<CpuPowerData>("cpu.getPower");
  const bios = useModule("bios");
  // The BIOS part is for one board and BIOS version only; everywhere else it is left out without a word.
  const showBios =
    !!bios && !(bios.message ?? "").startsWith("Not available on this PC");

  return (
    <div className="page">
      <header className="page-head">
        <h1>CPU</h1>
      </header>
      <ModuleProblem name="cpu" />
      {info.error && (
        <Notice tone="warn" title="The processor could not be read">
          {info.error}
        </Notice>
      )}
      {info.data && <Identity info={info.data} />}
      <Readings />
      {power.error && (
        <Notice tone="warn" title="The power plan could not be read">
          {power.error}
        </Notice>
      )}
      {power.data && <CpuPower power={power.data} onPower={power.setData} />}
      {showBios && <BiosSection />}
    </div>
  );
}

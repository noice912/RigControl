import { ExtraPanels } from "../extras";
import { fmtTemp, unitLabel, useTempUnit } from "../units";
import { useMemo, useState } from "react";
import { useLive } from "../hooks";
import { useApp } from "../appContext";
import { BANDS, fmt, thermalColor, thermalFraction, type Band } from "../thermal";
import { Meter, Notice, Switch } from "../components/controls";
import type { CoolingStatus, SensorSnapshot } from "../types";

function Thermal(props: {
  label: string;
  detail?: string | null;
  tempC: number | null | undefined;
  band: Band;
  stats: { label: string; value: string }[];
}) {
  const color = thermalColor(props.tempC, props.band);
  const unit = useTempUnit();
  return (
    <div className="thermal">
      <div className="thermal-label">
        <span>{props.label}</span>
        {props.detail && <span title={props.detail}>{props.detail}</span>}
      </div>
      <div className="thermal-value" style={{ color }} aria-label={`${props.label} ${fmtTemp(props.tempC, unit)} degrees`}>
        {fmtTemp(props.tempC, unit)}
        <span className="unit">{unitLabel(unit)}</span>
      </div>
      <div className="gauge" aria-hidden="true">
        <div style={{ width: `${thermalFraction(props.tempC, props.band) * 100}%`, background: color }} />
      </div>
      <div className="thermal-stats">
        {props.stats.map((s) => (
          <span key={s.label}>
            {s.label} <b>{s.value}</b>
          </span>
        ))}
      </div>
    </div>
  );
}

type FanRow = {
  key: string;
  name: string;
  source: string;
  rpm: number | null;
  duty: number | null;
  control: string;
  idle: boolean;
};

function useFanRows(snap: SensorSnapshot | null, cooling: CoolingStatus | null): FanRow[] {
  return useMemo(() => {
    const rows: FanRow[] = [];
    const blocked = (cooling?.blockedBy.length ?? 0) > 0;
    for (const d of cooling?.devices ?? []) {
      for (const c of d.channels) {
        rows.push({
          key: `${d.id}/${c.id}`,
          name: c.name,
          source: d.name,
          rpm: c.rpm,
          duty: c.dutyPercent,
          control: blocked
            ? `Set by ${cooling!.blockedBy[0]}`
            : c.mode === "curve"
              ? `Curve on ${c.storedOnDevice ? "cooler" : "RigControl"}`
              : c.mode === "fixed"
                ? `Fixed ${c.fixedDutyPercent ?? c.dutyPercent ?? "–"}%`
                : "Not set by RigControl",
          idle: false,
        });
      }
    }
    if (snap?.gpu && (snap.gpu.fanRpm !== null || snap.gpu.fanPercent !== null)) {
      rows.push({
        key: "gpu",
        name: "Graphics card fans",
        source: snap.gpu.name,
        rpm: snap.gpu.fanRpm,
        duty: snap.gpu.fanPercent,
        control: "Graphics driver",
        idle: false,
      });
    }
    for (const f of snap?.fans ?? []) {
      rows.push({
        key: f.id,
        name: f.name,
        source: f.hardware,
        rpm: f.rpm,
        duty: null,
        control: f.controlId ? "Motherboard" : "Read only",
        idle: f.rpm === null || f.rpm === 0,
      });
    }
    return rows;
  }, [snap, cooling]);
}

export function Overview() {
  const unit = useTempUnit();
  const { info, restartAsAdmin } = useApp();
  const { data: snap, error } = useLive("sensors.snapshot", "sensors.getSnapshot");
  const { data: cooling } = useLive("cooling.status", "cooling.getDevices");
  const [showIdle, setShowIdle] = useState(false);
  const fans = useFanRows(snap, cooling);
  const kraken = cooling?.devices.find((d) => d.liquidTempC !== null || d.channels.some((c) => c.kind === "pump")) ?? null;
  const pump = kraken?.channels.find((c) => c.kind === "pump");
  const shownFans = showIdle ? fans : fans.filter((f) => !f.idle);
  const idleCount = fans.length - shownFans.length;
  const maxRpm = Math.max(1, ...fans.map((f) => f.rpm ?? 0));

  return (
    <div className="page">
      <div className="page-head">
        <h1>Overview</h1>
        {snap && <span className="sub">{snap.board?.name ?? info?.machineName}</span>}
      </div>

      {error && !snap && <Notice tone="error" title="Sensor readings are not available">{error}</Notice>}

      <section className="thermal-hero" aria-label="Temperatures">
        <Thermal
          label="Processor"
          detail={snap?.cpu?.name ?? null}
          tempC={snap?.cpu?.packageTempC}
          band={BANDS.cpu}
          stats={[
            { label: "Load", value: `${fmt(snap?.cpu?.loadPercent)}%` },
            { label: "Clock", value: snap?.cpu?.clockMHz ? `${(snap.cpu.clockMHz / 1000).toFixed(2)} GHz` : "–" },
            { label: "Power", value: `${fmt(snap?.cpu?.powerW)} W` },
          ]}
        />
        <Thermal
          label="Graphics"
          detail={snap?.gpu?.name ?? null}
          tempC={snap?.gpu?.tempC}
          band={BANDS.gpu}
          stats={[
            { label: "Hotspot", value: `${fmtTemp(snap?.gpu?.hotspotTempC, unit)}°` },
            { label: "Load", value: `${fmt(snap?.gpu?.loadPercent)}%` },
            { label: "Power", value: `${fmt(snap?.gpu?.powerW)} W` },
          ]}
        />
        <Thermal
          label="Liquid"
          detail={kraken?.name ?? "Kraken"}
          tempC={kraken?.liquidTempC}
          band={BANDS.liquid}
          stats={
            cooling?.blockedBy.length && kraken?.liquidTempC == null
              ? [{ label: "Read by", value: cooling.blockedBy[0]! }]
              : [
                  { label: "Pump", value: pump?.rpm != null ? `${pump.rpm} rpm` : "–" },
                  { label: "Duty", value: pump?.dutyPercent != null ? `${pump.dutyPercent}%` : "–" },
                ]
          }
        />
      </section>

      {snap && !snap.elevated && (
        <Notice
          tone="warn"
          title="Some readings need administrator rights"
          actions={
            <button className="btn" onClick={() => void restartAsAdmin()}>
              Restart as administrator
            </button>
          }
        >
          {snap.notes[0] ?? "Processor temperatures and motherboard fans can only be read when RigControl runs as administrator."}
        </Notice>
      )}

      <section className="panel" aria-label="Load">
        <div className="meters">
          <Meter label="Processor load" value={fmt(snap?.cpu?.loadPercent)} unit="%" fraction={(snap?.cpu?.loadPercent ?? 0) / 100} />
          <Meter label="Graphics load" value={fmt(snap?.gpu?.loadPercent)} unit="%" fraction={(snap?.gpu?.loadPercent ?? 0) / 100} />
          <Meter
            label="Memory"
            value={fmt(snap?.memory.usedGB, 1)}
            unit={`of ${fmt(snap?.memory.totalGB, 0)} GB`}
            fraction={(snap?.memory.loadPercent ?? 0) / 100}
          />
          <Meter
            label="Video memory"
            value={snap?.gpu?.vramUsedMB != null ? (snap.gpu.vramUsedMB / 1024).toFixed(1) : "–"}
            unit={snap?.gpu?.vramTotalMB ? `of ${Math.round(snap.gpu.vramTotalMB / 1024)} GB` : "GB"}
            fraction={snap?.gpu?.vramUsedMB != null && snap.gpu.vramTotalMB ? snap.gpu.vramUsedMB / snap.gpu.vramTotalMB : 0}
          />
        </div>
      </section>

      <ExtraPanels slot="overview" />

      <section className="panel" aria-label="Fans">
        <div className="panel-head">
          <div>
            <h2>Every fan and pump</h2>
            <div className="sub">From the Kraken, the NZXT controller, the graphics card and the motherboard, in one place.</div>
          </div>
          {idleCount > 0 || showIdle ? (
            <Switch checked={showIdle} onChange={setShowIdle} label="Show unused headers" />
          ) : null}
        </div>
        {shownFans.length === 0 ? (
          <div className="empty">No fan readings yet.</div>
        ) : (
          <table className="fan-table">
            <thead>
              <tr>
                <th>Fan</th>
                <th>Device</th>
                <th className="duty-cell">Speed</th>
                <th className="num">RPM</th>
                <th>Controlled by</th>
              </tr>
            </thead>
            <tbody>
              {shownFans.map((f) => (
                <tr key={f.key}>
                  <td>{f.name}</td>
                  <td className="source-tag">{f.source}</td>
                  <td className="duty-cell">
                    <div className="bar" title={f.duty != null ? `${f.duty}% duty` : undefined}>
                      <div style={{ width: `${f.duty != null ? f.duty : ((f.rpm ?? 0) / maxRpm) * 100}%` }} />
                    </div>
                  </td>
                  <td className="num">{f.rpm != null ? f.rpm.toLocaleString() : "–"}</td>
                  <td className="source-tag">{f.control}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {snap && snap.notes.length > (snap.elevated ? 0 : 1) && (
        <Notice title="About these readings">
          {snap.notes.slice(snap.elevated ? 0 : 1).map((n) => (
            <div key={n}>{n}</div>
          ))}
        </Notice>
      )}
    </div>
  );
}

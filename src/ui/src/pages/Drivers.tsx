import { useEffect, useState } from "react";
import { call, on } from "../bridge";
import { useAction, useLive } from "../hooks";
import { useApp } from "../appContext";
import { Notice } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import type { DriverAction, DriverDownload, DriverRow, DriversStatus } from "../types";

const STATE_TEXT: Record<string, string> = {
  update: "Update available",
  current: "Up to date",
  newer: "Yours is newer",
  missing: "Not installed",
  unknown: "Can't compare",
};

function stateClass(state: string): string {
  return state === "update" ? "badge warn" : state === "current" || state === "newer" ? "badge ok" : "badge";
}

function when(iso: string | null): string {
  if (!iso) return "never";
  return new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

export function Drivers() {
  const { data: status, setData, error } = useLive("drivers.status", "drivers.getStatus");
  const { info, restartAsAdmin } = useApp();
  const { busy, run } = useAction();
  const [download, setDownload] = useState<DriverDownload | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());

  useEffect(() => on("drivers.download", setDownload), []);
  useEffect(() => setPicked(new Set((status?.windowsUpdate ?? []).filter((u) => !u.needsLicense).map((u) => u.id))), [status?.checkedAt]);

  const check = async () => {
    const r = await run("check", () => call<DriversStatus>("drivers.check", undefined, 300000));
    if (r) setData(r);
  };
  const installAsus = async (row: DriverRow) => {
    setDownload(null);
    const r = await run(row.key, () => call<DriverAction>("drivers.installAsus", { fileId: row.fileId }, 3600000), "The installer has started. Follow its steps.");
    if (r) setData(r.status);
    setDownload(null);
  };
  const installWu = async () => {
    const r = await run("wu", () => call<DriverAction>("drivers.installWindowsUpdate", { ids: [...picked] }, 3600000));
    if (r) setData(r.status);
    if (r) window.alert(r.message);
  };

  const checking = status?.checking || busy === "check";
  const updates = (status?.rows.filter((r) => r.state === "update").length ?? 0) + (status?.windowsUpdate?.length ?? 0);
  const working = status?.busy ?? null;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Drivers</h1>
          <p>Driver updates for your motherboard's parts from ASUS, and any driver updates Windows Update has for this PC.</p>
        </div>
        <button className="btn primary" disabled={checking || working !== null} onClick={() => void check()}>
          {checking ? "Checking…" : "Check for updates"}
        </button>
      </div>

      <ModuleProblem name="drivers" />
      {error && (
        <Notice tone="error" title="Drivers are not available">
          {error}
        </Notice>
      )}
      {info && !info.isElevated && (
        <Notice
          tone="warn"
          title="Installing drivers needs administrator rights"
          actions={
            <button className="btn" onClick={() => void restartAsAdmin()}>
              Restart as administrator
            </button>
          }
        >
          You can check for updates now; installing them needs RigControl running as administrator.
        </Notice>
      )}
      {working && (
        <Notice title={working}>
          {download && download.name === working
            ? `Downloading… ${download.receivedMB} MB${download.totalMB ? ` of ${download.totalMB} MB` : ""}${download.percent != null ? ` (${download.percent}%)` : ""}`
            : "Working…"}
        </Notice>
      )}

      {status && (
        <>
          <section className="panel" aria-label="Motherboard drivers from ASUS">
            <div className="panel-head">
              <div>
                <h2>Motherboard drivers from ASUS</h2>
                <div className="sub">
                  {status.board ?? "Motherboard"}. Last checked {when(status.checkedAt)}.
                  {status.checkedAt ? (updates > 0 ? ` ${updates} update${updates === 1 ? "" : "s"} available.` : " Nothing to update.") : ""}
                </div>
              </div>
            </div>
            {status.asusProblem && (
              <Notice tone="warn" title="ASUS's list is not available">
                {status.asusProblem}
              </Notice>
            )}
            {!status.checkedAt && !checking ? (
              <div className="empty">Click "Check for updates". RigControl asks ASUS's support site and Windows Update; nothing is downloaded yet.</div>
            ) : status.rows.length === 0 ? (
              <div className="empty">{checking ? "Checking…" : "No parts to show."}</div>
            ) : (
              <div className="stack" style={{ gap: 0 }}>
                {status.rows.map((r) => (
                  <div className="field" key={r.key}>
                    <div className="field-label">
                      <span>
                        {r.name} <span className={stateClass(r.state)}>{STATE_TEXT[r.state] ?? r.state}</span>
                      </span>
                      <span className="sub">
                        {r.device ? `${r.device}: ${r.installedVersion}` : "No device found"}
                        {r.availableVersion ? ` · ASUS: ${r.availableVersion}${r.released ? ` (${r.released}${r.size ? `, ${r.size}` : ""})` : ""}` : ""}
                      </span>
                      {r.note && <span className="sub faint">{r.note}</span>}
                    </div>
                    <div className="field-control">
                      {r.fileId && (r.state === "update" || r.state === "missing" || r.state === "unknown") && (
                        <button
                          className={r.state === "update" ? "btn primary" : "btn"}
                          disabled={working !== null || checking || !info?.isElevated}
                          onClick={() => void installAsus(r)}
                        >
                          {busy === r.key ? "Working…" : r.state === "update" ? "Download and install" : "Install anyway"}
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            )}
            <p className="sub faint">
              Downloads come only from ASUS's servers, are checked against the checksum ASUS publishes, and RigControl only starts an
              installer signed by ASUS, Intel or Realtek. The graphics card driver is on the Graphics page.
            </p>
          </section>

          <section className="panel" aria-label="Driver updates from Windows Update">
            <div className="panel-head">
              <div>
                <h2>From Windows Update</h2>
                <div className="sub">Drivers Microsoft offers for this PC, outside the normal monthly updates.</div>
              </div>
              {(status.windowsUpdate?.length ?? 0) > 0 && (
                <button className="btn primary" disabled={picked.size === 0 || working !== null || checking || !info?.isElevated} onClick={() => void installWu()}>
                  {busy === "wu" ? "Installing…" : `Install ${picked.size} selected`}
                </button>
              )}
            </div>
            {status.windowsUpdateProblem ? (
              <Notice tone="warn" title="Windows Update could not be asked">
                {status.windowsUpdateProblem}
              </Notice>
            ) : !status.checkedAt ? (
              <div className="empty">Not checked yet.</div>
            ) : (status.windowsUpdate?.length ?? 0) === 0 ? (
              <div className="empty">Windows Update has no driver updates for this PC.</div>
            ) : (
              <div className="stack" style={{ gap: 6 }}>
                {status.windowsUpdate!.map((u) => (
                  <label key={u.id} className="row" style={{ gap: 10, alignItems: "flex-start" }}>
                    <input
                      type="checkbox"
                      checked={picked.has(u.id)}
                      disabled={u.needsLicense}
                      onChange={(e) =>
                        setPicked((cur) => {
                          const next = new Set(cur);
                          if (e.target.checked) next.add(u.id);
                          else next.delete(u.id);
                          return next;
                        })
                      }
                    />
                    <span>
                      {u.title}
                      <span className="sub">
                        {" "}
                        {[u.driverClass, u.date, `${u.sizeMB} MB`].filter(Boolean).join(" · ")}
                        {u.needsLicense ? " · asks for licence terms: install it from Windows Update in Settings" : ""}
                      </span>
                    </span>
                  </label>
                ))}
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}

import { call } from "../bridge";
import { useAction, useLive } from "../hooks";
import { useApp } from "../appContext";
import { goTo } from "../nav";
import type { UpdateStatus } from "../types";
import { Notice, Switch } from "./controls";

function useUpdates() {
  return useLive("updates.status", "updates.getStatus");
}

function when(iso: string | null) {
  return iso ? new Date(iso).toLocaleString() : "never";
}

function mb(bytes: number) {
  return `${Math.round(bytes / 1024 / 1024)} MB`;
}

/** Settings > Updates: automatic checks, Check now, and the update button when one is found. */
export function UpdatesPanel() {
  const { restartAsAdmin } = useApp();
  const { data: s, setData } = useUpdates();
  const { busy, run } = useAction();

  const check = async () => {
    const r = await run("check", () => call<UpdateStatus>("updates.check", undefined, 90000));
    if (r) setData(r);
  };
  const setAuto = async (autoCheck: boolean) => {
    const r = await run("auto", () => call<UpdateStatus>("updates.setSettings", { autoCheck }));
    if (r) setData(r);
  };
  const install = () => void run("install", () => call("updates.install", undefined, 15 * 60000));

  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          <h2>Updates</h2>
          <div className="sub">
            RigControl {s?.current ?? ""}. Only updates signed with RigControl's own key are offered. Last checked:{" "}
            {when(s?.lastCheck ?? null)}.
          </div>
        </div>
        <button className="btn small" disabled={!s || s.checking || busy !== null} onClick={() => void check()}>
          {s?.checking ? "Checking…" : "Check now"}
        </button>
      </div>
      <div className="field">
        <div className="field-label">
          <span>Check for updates automatically</span>
          <span className="sub">A minute after RigControl starts, then every 6 hours. Only GitHub is asked.</span>
        </div>
        <div className="field-control">
          <Switch checked={s?.autoCheck ?? true} disabled={!s || busy !== null} onChange={(v) => void setAuto(v)} />
        </div>
      </div>
      {s?.error && <Notice tone="warn" title="The last check did not work">{s.error}</Notice>}
      {s && !s.available && !s.error && s.lastCheck && (
        <div className="sub">{s.note ?? "RigControl is up to date."}</div>
      )}
      {s?.available && (
        <Notice
          title={`RigControl ${s.available.version} is available`}
          actions={
            s.canInstall ? (
              s.elevated ? (
                <button className="btn primary" disabled={busy !== null || !!s.installing} onClick={install}>
                  {s.installing
                    ? `${s.installing}${s.progress != null ? ` ${Math.round(s.progress * 100)}%` : ""}…`
                    : `Update to ${s.available.version}`}
                </button>
              ) : (
                <button className="btn" onClick={() => void restartAsAdmin()}>
                  Restart as administrator
                </button>
              )
            ) : undefined
          }
        >
          {s.canInstall
            ? s.elevated
              ? `${mb(s.available.size)}. RigControl closes, updates itself and starts again. Not while a recording is running.`
              : "Installing an update needs RigControl to run as administrator."
            : "This copy of RigControl isn't installed, so it can't update itself. Install RigControl with its setup to get updates automatically."}
          {s.available.notes && <div className="sub" style={{ whiteSpace: "pre-wrap", marginTop: 6 }}>{s.available.notes}</div>}
        </Notice>
      )}
    </section>
  );
}

/** A banner on every page while an update is waiting. */
export function UpdateBanner({ onSettings }: { onSettings?: boolean }) {
  const { data: s } = useUpdates();
  if (!s?.available || onSettings) return null;
  return (
    <div style={{ marginBottom: 12 }}>
      <Notice
        title={`RigControl ${s.available.version} is available`}
        actions={
          <button className="btn small" onClick={() => goTo("settings")}>
            {s.canInstall ? "Update" : "Details"}
          </button>
        }
      >
        {s.canInstall ? "Open Settings to install it." : "Install RigControl with its setup to get updates automatically."}
      </Notice>
    </div>
  );
}

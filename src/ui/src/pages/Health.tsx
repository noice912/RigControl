import { useAction } from "../hooks";
import { rescanHealth, useHealth } from "../health";
import { openFirstRun } from "../nav";
import { Loading, Notice } from "../components/controls";
import { FindingGroups } from "../components/HealthFindings";

function when(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const time = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  return d.toDateString() === new Date().toDateString() ? `today at ${time}` : `${d.toLocaleDateString()} at ${time}`;
}

/** "memoryIntegrity" -> "Memory integrity". */
function words(key: string): string {
  const s = key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_-]+/g, " ")
    .toLowerCase();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** What each checked item is called on the page. Anything not listed is shown by its own name in words. */
const SYSTEM_LABELS: Record<string, string> = {
  elevated: "RigControl runs as administrator",
  fastStartup: "Windows Fast Startup is on",
  memoryIntegrity: "Memory integrity is on",
  driverBlocklist: "Windows blocks known unsafe drivers",
  controlledFolderAccess: "Controlled folder access is on",
  pendingRestart: "Windows is waiting for a restart",
  gameBarRecording: "Game Bar recording is on",
  gameBarBackgroundRecording: "Game Bar records in the background",
  gameBarLogoButton: "The controller's logo button opens Game Bar",
  gameMode: "Game Mode is on",
  dynamicLighting: "Windows Dynamic Lighting is on",
  windowsUpdateDrivers: "Windows Update installs drivers",
  sessionCount: "Windows users signed in",
  otherSessionRunsRigControl: "Another Windows user is running RigControl",
};

function shown(v: boolean | number | string | null): string {
  if (v === null || v === "") return "Not known";
  if (typeof v === "boolean") return v ? "Yes" : "No";
  return String(v);
}

export function Health() {
  const { report, error } = useHealth();
  const { busy, run } = useAction();

  const system = Object.entries(report?.system ?? {});

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Health</h1>
          <p>Other programs and Windows settings that get in RigControl's way, and what you can do about each one.</p>
        </div>
        <div className="row" style={{ gap: 12, alignItems: "center" }}>
          {report && <span className="sub">Last checked {when(report.scannedAt)}</span>}
          <button className="btn" disabled={busy !== null} onClick={() => void run("scan", rescanHealth)}>
            {busy === "scan" ? "Checking…" : "Check again"}
          </button>
        </div>
      </div>

      {!report && !error && <Loading what="your PC" />}
      {!report && error && (
        <Notice tone="warn" title="The health check is not answering">
          {error} Press Check again in a moment.
        </Notice>
      )}

      {report && report.findings.length === 0 && (
        <section className="panel">
          <div className="empty">
            <strong>Nothing is getting in RigControl's way.</strong>
            <div className="sub">No other program or Windows setting was found that stops lights, fans, recording, sound or your Discord status.</div>
          </div>
        </section>
      )}

      {report && report.findings.length > 0 && <FindingGroups findings={report.findings} panels onFixed={rescanHealth} />}

      {report && (
        <section className="panel">
          {system.length > 0 && (
            <details>
              <summary className="finding-summary">What was checked on this PC</summary>
              <div style={{ marginTop: 10 }}>
                {system.map(([k, v]) => (
                  <div className="field" key={k}>
                    <div className="field-label">
                      <span>{SYSTEM_LABELS[k] ?? words(k)}</span>
                    </div>
                    <div className="field-control">{shown(v)}</div>
                  </div>
                ))}
              </div>
            </details>
          )}
          <div className="spread">
            <span className="sub">RigControl only reports these. It never changes Windows security settings and never closes a program without asking.</span>
            <button className="btn small" onClick={openFirstRun}>
              Go through first-time setup again
            </button>
          </div>
        </section>
      )}
    </div>
  );
}

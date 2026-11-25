import { call } from "../bridge";
import { useAction, useQuery } from "../hooks";
import { NOT_ON_THIS_SYSTEM, notOnThisSystem, useApp } from "../appContext";
import { Switch, Segmented } from "../components/controls";
import type { AppSettings, LogLine, ModuleState } from "../types";
import { setTempUnit } from "../units";
import { BuiltInApps } from "../components/BuiltInApps";
import { Licences } from "../components/Licences";
import { UpdatesPanel } from "../components/Updates";
import { GitHubAccountPanel, ReviewPanel, useGitHub } from "../components/GitHubAccount";
import { DeveloperSettings } from "../components/DeveloperSettings";

const STATE: Record<ModuleState, { label: string; color: string }> = {
  ready: { label: "Working", color: "var(--ok)" },
  degraded: { label: "Working with limits", color: "var(--warm)" },
  failed: { label: "Not available", color: "var(--hot)" },
  starting: { label: "Starting", color: "var(--coolant)" },
  notStarted: { label: "Not started", color: "var(--faint)" },
  stopped: { label: "Stopped", color: "var(--faint)" },
};

export function Settings() {
  const { info, modules, restartAsAdmin, can, notWindows } = useApp();
  // What the Windows app has and the Linux and macOS app does not. Nothing is hidden before the app has said.
  const hasTray = can("host.tray");
  const audioNotHere = (audio: { message: string | null } | null) =>
    !!audio?.message?.startsWith(NOT_ON_THIS_SYSTEM);
  const settings = useQuery<AppSettings>("app.getSettings");
  const audio = useQuery<{ state: string; message: string | null }>(
    "app.getAudioStatus",
  );
  const logs = useQuery<LogLine[]>("app.getLogs", { max: 200 });
  const { busy, run } = useAction();
  const github = useGitHub();

  const change = async (patch: Partial<AppSettings>) => {
    // Turning "start with Windows" on or off can show a Windows permission prompt, so allow time for it.
    const r = await run("set", () =>
      call<AppSettings>("app.setSettings", patch, 120000),
    );
    if (r?.temperatureUnit) setTempUnit(r.temperatureUnit);
    if (r) settings.setData(r);
  };

  const s = settings.data;

  return (
    <div className="page">
      <div className="page-head">
        <h1>Settings</h1>
        {info && <span className="sub">RigControl {info.version}</span>}
      </div>

      <section className="panel">
        <h2>Starting and closing</h2>
        <div>
          {hasTray && (
          <div className="field">
            <div className="field-label">
              <span>Keep running in the tray when I close the window</span>
              <span className="sub">
                Fan curves, lighting and soundboard hotkeys keep working. Exit
                from the tray icon.
              </span>
            </div>
            <div className="field-control">
              <Switch
                checked={s?.minimizeToTray ?? true}
                disabled={!s || busy !== null}
                onChange={(v) => void change({ minimizeToTray: v })}
              />
            </div>
          </div>
          )}
          {can("host.autostart") && (
          <div className="field">
            {notWindows ? (
              <div className="field-label">
                <span>Start when I sign in</span>
                <span className="sub">
                  RigControl opens its window when you sign in to this
                  computer.
                </span>
              </div>
            ) : (
            <div className="field-label">
              <span>Start with Windows, as administrator</span>
              <span className="sub">
                Starts in the tray when you sign in, with full access to sensors
                and fans and no prompt each time. Windows asks for permission
                once, when you switch this on or off.
              </span>
            </div>
            )}
            <div className="field-control">
              <Switch
                checked={s?.startWithWindows ?? false}
                disabled={!s || busy !== null}
                onChange={(v) => void change({ startWithWindows: v })}
              />
            </div>
          </div>
          )}
          {hasTray && (
          <div className="field">
            <div className="field-label">
              <span>Start hidden in the tray</span>
              <span className="sub">
                Applies every time RigControl starts, not just with Windows.
              </span>
            </div>
            <div className="field-control">
              <Switch
                checked={s?.startMinimized ?? false}
                disabled={!s || busy !== null}
                onChange={(v) => void change({ startMinimized: v })}
              />
            </div>
          </div>
          )}
          <div className="field">
            <div className="field-label">
              <span>Temperatures</span>
              <span className="sub">
                How temperatures are shown in RigControl, on the cooler's screen
                and in the overlay.
              </span>
            </div>
            <div className="field-control">
              <Segmented
                label="Temperature unit"
                value={s?.temperatureUnit ?? "F"}
                disabled={!s || busy !== null}
                options={[
                  { value: "F" as const, label: "°F" },
                  { value: "C" as const, label: "°C" },
                ]}
                onChange={(v) => void change({ temperatureUnit: v })}
              />
            </div>
          </div>
        </div>
      </section>

      <section className="panel">
        <div className="spread">
          <div>
            <h2>Quit RigControl</h2>
            <div className="sub">
              {hasTray
                ? "Closing the window only hides RigControl in the tray. This closes it fully: a recording is finished, fans are handed back."
                : "Closes RigControl, the same as closing its window. Lighting effects stop and your lights are put back as they were."}
            </div>
          </div>
          <button
            className="btn"
            onClick={() => void call("app.quit").catch(() => undefined)}
          >
            Quit
          </button>
        </div>
      </section>

      {can("host.elevation") && (
      <section className="panel">
        <div className="panel-head">
          <div>
            <h2>Administrator rights</h2>
            <div className="sub">
              {info?.isElevated
                ? "RigControl is running as administrator, so every feature is available."
                : "Processor temperatures, motherboard fans and the graphics power limit need RigControl to run as administrator."}
            </div>
          </div>
          {info && !info.isElevated && (
            <button
              className="btn primary"
              onClick={() => void restartAsAdmin()}
            >
              Restart as administrator
            </button>
          )}
        </div>
      </section>
      )}

      {/* Updates, the GitHub account and reviews (the Windows app). Developer only for an account that may push to RigControl. */}
      {info && can("host.updates") && (
        <>
          <UpdatesPanel />
          <GitHubAccountPanel />
          <ReviewPanel />
          {github.data?.maintainer && <DeveloperSettings key={github.data.login ?? ""} />}
        </>
      )}

      {/* Asked for only once the app has said it carries helper programs (the Windows app does). */}
      {info && can("host.helpers") && <BuiltInApps />}

      {/* The texts travel inside the Windows app (app.getLicences). */}
      {info && can("host.helpers") && <Licences />}

      <section className="panel">
        <h2>What's working</h2>
        <div>
          {modules.map((m) => (
            <div className="field" key={m.name}>
              <div className="field-label">
                <span>{m.displayName}</span>
                {m.message && <span className="sub">{m.message}</span>}
              </div>
              <div className="field-control">
                <span
                  className="status-dot"
                  style={{
                    background: notOnThisSystem(m)
                      ? "var(--faint)"
                      : STATE[m.state].color,
                  }}
                />
                <span>
                  {notOnThisSystem(m)
                    ? "Not on this system"
                    : STATE[m.state].label}
                </span>
              </div>
            </div>
          ))}
          {audio.data && (
            <div className="field">
              <div className="field-label">
                <span>Audio</span>
                {audio.data.message && (
                  <span className="sub">{audio.data.message}</span>
                )}
              </div>
              <div className="field-control">
                <span
                  className="status-dot"
                  style={{
                    background:
                      audio.data.state === "running"
                        ? "var(--ok)"
                        : audioNotHere(audio.data)
                          ? "var(--faint)"
                          : "var(--warm)",
                  }}
                />
                <span>
                  {audio.data.state === "running"
                    ? "Working"
                    : audioNotHere(audio.data)
                      ? "Not on this system"
                      : "Not available"}
                </span>
              </div>
            </div>
          )}
        </div>
      </section>

      <section className="panel">
        <div className="panel-head">
          <h2>Activity log</h2>
          <span className="row">
            <button className="btn small" onClick={() => void logs.reload()}>
              Refresh
            </button>
            <button
              className="btn small"
              onClick={() => void call("app.openLogFolder").catch(() => {})}
            >
              Open log folder
            </button>
          </span>
        </div>
        <div className="log" tabIndex={0} aria-label="Recent log lines">
          {(logs.data ?? []).length === 0
            ? "Nothing logged yet."
            : [...(logs.data ?? [])].reverse().map((l, i) => (
                <div key={i} className={l.level}>
                  {new Date(l.time).toLocaleTimeString()} [{l.source}]{" "}
                  {l.message}
                </div>
              ))}
        </div>
        {info && (
          <div className="sub faint">
            Settings and logs are kept in {info.dataDirectory}
          </div>
        )}
      </section>
    </div>
  );
}

import { useEffect, useState } from "react";
import { call } from "../bridge";
import { useAction, useLive } from "../hooks";
import { Notice, Segmented, Slider, Switch } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import { HealthNotice } from "../components/HealthFindings";
import { CaptureCardSound } from "../components/CaptureCardSound";
import type { CaptureSettings, CaptureStatus } from "../types";

function elapsed(since: string | null, now: number): string {
  if (!since) return "00:00";
  const s = Math.max(0, Math.floor((now - new Date(since).getTime()) / 1000));
  const h = Math.floor(s / 3600);
  const mm = String(Math.floor((s % 3600) / 60)).padStart(2, "0");
  const ss = String(s % 60).padStart(2, "0");
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

function fileName(path: string | null): string | null {
  return path ? (path.split(/[\\/]/).pop() ?? path) : null;
}

const REPLAY_LENGTHS = [
  { value: 30, label: "30 s" },
  { value: 60, label: "1 min" },
  { value: 120, label: "2 min" },
  { value: 300, label: "5 min" },
  { value: 600, label: "10 min" },
];

function HotkeyField(props: { label: string; value: string; disabled: boolean; onSave: (v: string) => void }) {
  const [text, setText] = useState(props.value);
  useEffect(() => setText(props.value), [props.value]);
  const commit = () => {
    const v = text.trim();
    if (v && v !== props.value) props.onSave(v);
    else setText(props.value);
  };
  return (
    <div className="field">
      <div className="field-label">{props.label}</div>
      <div className="field-control">
        <input
          className="select"
          style={{ width: 160 }}
          value={text}
          aria-label={props.label}
          disabled={props.disabled}
          onChange={(e) => setText(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
        />
      </div>
    </div>
  );
}

export function Capture() {
  const { data: status, setData, error } = useLive("capture.status", "capture.getStatus");
  const { busy, run } = useAction();
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!status?.recording) return;
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [status?.recording]);

  const act = async (key: string, method: string, params?: unknown, success?: string, timeout = 30000) => {
    const r = await run(key, () => call<CaptureStatus>(method, params, timeout), success);
    if (r) setData(r);
  };
  const set = (patch: Partial<CaptureSettings>, success?: string) => act("settings", "capture.setSettings", patch, success);

  const chooseFolder = async () => {
    const picked = await call<{ path: string | null }>("app.pickFolder", { title: "Choose where videos are saved" }).catch(() => null);
    if (picked?.path) await set({ folder: picked.path });
  };

  const s = status?.settings;
  const locked = busy !== null || !status?.available;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Recording</h1>
          <p>Record your screen and keep an instant replay of the last few minutes, encoded on the graphics card.</p>
        </div>
      </div>

      <ModuleProblem name="capture" />
      <HealthNotice modules={["capture"]} />
      {error && (
        <Notice tone="error" title="Recording is not available">
          {error}
        </Notice>
      )}
      {status && !status.available && status.problem && (
        <Notice tone="error" title="Recording is not available">
          {status.problem}
        </Notice>
      )}
      {status?.hotkeyProblems.map((p) => (
        <Notice key={p} tone="warn" title="A shortcut is not working">
          {p}
        </Notice>
      ))}

      <CaptureCardSound />

      {status && s && (
        <>
          <section className="panel" aria-label="Record">
            <div className="panel-head">
              <div>
                <h2>{status.recording ? `Recording  ${elapsed(status.recordingSince, now)}` : "Record"}</h2>
                <div className="sub">
                  {status.recording
                    ? fileName(status.recordingFile)
                    : `Press ${s.recordHotkey} in a game to start and stop, or use the button.`}
                </div>
              </div>
              {status.recording ? (
                <button className="btn primary" disabled={busy !== null} onClick={() => void act("rec", "capture.stopRecording", undefined, "Recording saved")}>
                  {busy === "rec" ? "Saving…" : "Stop and save"}
                </button>
              ) : (
                <button className="btn primary" disabled={locked} onClick={() => void act("rec", "capture.startRecording")}>
                  Start recording
                </button>
              )}
            </div>
            {status.lastSaved && (
              <p className="sub">
                Last saved: {fileName(status.lastSaved)}
              </p>
            )}
            <div className="field">
              <div className="field-label">
                <span>Videos folder</span>
                <span className="sub">{s.folder}</span>
              </div>
              <div className="field-control" style={{ display: "flex", gap: 8 }}>
                <button className="btn" disabled={busy !== null} onClick={() => void act("open", "capture.openFolder")}>
                  Open
                </button>
                <button className="btn" disabled={busy !== null} onClick={() => void chooseFolder()}>
                  Change…
                </button>
              </div>
            </div>
          </section>

          <section className="panel" aria-label="Instant replay">
            <div className="panel-head">
              <div>
                <h2>Instant replay</h2>
                <div className="sub">
                  {status.replayRunning
                    ? `On. Press ${s.replayHotkey} to save the last ${REPLAY_LENGTHS.find((l) => l.value === s.replaySeconds)?.label ?? `${s.replaySeconds} s`}.`
                    : "Off. Nothing is being recorded."}
                </div>
              </div>
              {status.replayRunning && (
                <button className="btn primary" disabled={busy !== null} onClick={() => void act("replay", "capture.saveReplay", undefined, "Replay saved", 180000)}>
                  {busy === "replay" ? "Saving…" : "Save replay now"}
                </button>
              )}
            </div>
            <Switch
              checked={s.replayEnabled}
              disabled={locked}
              onChange={(v) => void set({ replayEnabled: v }, v ? "Instant replay is on" : "Instant replay is off")}
              label="Keep an instant replay while RigControl is running"
            />
            <div className="field">
              <div className="field-label">
                <span>Length</span>
                <span className="sub">The replay is kept in a temporary buffer on your disk and deleted when you switch this off or close RigControl.</span>
              </div>
              <div className="field-control">
                <Segmented label="Replay length" value={s.replaySeconds} disabled={locked} options={REPLAY_LENGTHS} onChange={(v) => void set({ replaySeconds: v })} />
              </div>
            </div>
          </section>

          <section className="panel" aria-label="Video settings">
            <div className="panel-head">
              <div>
                <h2>Video</h2>
                <div className="sub">Used for both recordings and instant replay.</div>
              </div>
            </div>
            <div className="field">
              <div className="field-label">Frames per second</div>
              <div className="field-control">
                <Segmented
                  label="Frames per second"
                  value={s.fps}
                  disabled={locked || status.recording}
                  options={[30, 60, 120].map((v) => ({ value: v, label: String(v) }))}
                  onChange={(v) => void set({ fps: v })}
                />
              </div>
            </div>
            <div className="field">
              <div className="field-label">
                <span>Quality</span>
                <span className="sub">Higher looks better and makes bigger files. 30 Mbps is about 225 MB per minute.</span>
              </div>
              <div className="field-control">
                <Slider
                  label="Video quality"
                  min={5}
                  max={100}
                  value={s.bitrateMbps}
                  disabled={locked || status.recording}
                  format={(v) => `${v} Mbps`}
                  onCommit={(v) => void set({ bitrateMbps: v })}
                />
              </div>
            </div>
            <div className="field">
              <div className="field-label">
                <span>Format</span>
                <span className="sub">H.264 plays everywhere. HEVC gives the same quality in smaller files.</span>
              </div>
              <div className="field-control">
                <Segmented
                  label="Video format"
                  value={s.codec}
                  disabled={locked || status.recording}
                  options={[
                    { value: "h264", label: "H.264" },
                    { value: "hevc", label: "HEVC" },
                  ]}
                  onChange={(v) => void set({ codec: v })}
                />
              </div>
            </div>
            <div className="field">
              <div className="field-label">Screen</div>
              <div className="field-control">
                <Segmented
                  label="Screen to record"
                  value={s.display}
                  disabled={locked || status.recording}
                  options={[0, 1, 2].map((v) => ({ value: v, label: `Screen ${v + 1}` }))}
                  onChange={(v) => void set({ display: v })}
                />
              </div>
            </div>
            <Switch checked={s.systemAudio} disabled={locked || status.recording} onChange={(v) => void set({ systemAudio: v })} label="Record what the PC is playing" />
          </section>

          <section className="panel" aria-label="Overlay and shortcuts">
            <div className="panel-head">
              <div>
                <h2>Overlay and shortcuts</h2>
                <div className="sub">
                  A small display in the top-right corner shows when you are recording and when something is saved. It shows over
                  windowed and borderless games; a game in exclusive full screen hides it.
                </div>
              </div>
            </div>
            <Switch checked={s.overlayStats} disabled={busy !== null} onChange={(v) => void set({ overlayStats: v })} label="Also show GPU and CPU temperature and load" />
            <HotkeyField label="Start / stop recording" value={s.recordHotkey} disabled={busy !== null} onSave={(v) => void set({ recordHotkey: v })} />
            <HotkeyField label="Save instant replay" value={s.replayHotkey} disabled={busy !== null} onSave={(v) => void set({ replayHotkey: v })} />
            <HotkeyField label="Show / hide temperatures" value={s.overlayHotkey} disabled={busy !== null} onSave={(v) => void set({ overlayHotkey: v })} />
          </section>
        </>
      )}
    </div>
  );
}

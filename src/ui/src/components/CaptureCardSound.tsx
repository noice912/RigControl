import { call } from "../bridge";
import { useAction, useLive, useQuery } from "../hooks";
import { Notice, Slider, Switch } from "./controls";
import { ModuleProblem } from "./ModuleProblem";
import type { ListenDevices, ListenSettings, ListenStatus } from "../types";

/** "Capture card sound": plays the capture card's input on the headphones, boosted. Module "listen". */
export function CaptureCardSound() {
  const { data: status, setData, error } = useLive("listen.status", "listen.getStatus");
  const devices = useQuery<ListenDevices>("listen.listDevices");
  const { busy, run } = useAction();

  // Empty ids mean "automatic" / "Windows' default" to the backend.
  const set = async (patch: Partial<Record<keyof ListenSettings, unknown>>) => {
    const r = await run("listen", () => call<ListenStatus>("listen.setSettings", patch));
    if (r) setData(r);
  };

  const s = status?.settings;
  const level = status?.state === "running" ? Math.min(1, Math.max(0, status.level)) : 0;
  const inputs = devices.data?.inputs ?? [];
  const outputs = devices.data?.outputs ?? [];

  return (
    <section className="panel" aria-label="Capture card sound">
      <div className="panel-head">
        <div>
          <h2>Capture card sound</h2>
          <div className="sub">Plays the PlayStation's sound from the capture card on your headphones. Nothing is recorded.</div>
        </div>
      </div>
      <ModuleProblem name="listen" />
      {error && (
        <Notice tone="error" title="Capture card sound is not available">
          {error}
        </Notice>
      )}
      {s && (
        <>
          <Switch
            checked={s.enabled}
            disabled={busy !== null}
            onChange={(v) => void set({ enabled: v })}
            label="Play the capture card's sound"
          />
          {s.enabled && status?.problem && (
            <Notice tone="warn" title="Waiting">
              {status.problem}
            </Notice>
          )}
          {status?.state === "running" && (
            <p className="sub">
              Playing {status.inputName} on {status.outputName}.
            </p>
          )}
          <div className="field">
            <div className="field-label">Input</div>
            <div className="field-control">
              <select
                className="select"
                aria-label="Input"
                value={s.inputId ?? ""}
                disabled={busy !== null}
                onFocus={() => void devices.reload()}
                onChange={(e) => void set({ inputId: e.target.value })}
              >
                <option value="">Automatic (the capture card)</option>
                {inputs.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                    {d.captureCard ? " (capture card)" : ""}
                  </option>
                ))}
                {s.inputId && !inputs.some((d) => d.id === s.inputId) && <option value={s.inputId}>Not connected</option>}
              </select>
            </div>
          </div>
          <div className="field">
            <div className="field-label">Output</div>
            <div className="field-control">
              <select
                className="select"
                aria-label="Output"
                value={s.outputId ?? ""}
                disabled={busy !== null}
                onFocus={() => void devices.reload()}
                onChange={(e) => void set({ outputId: e.target.value })}
              >
                <option value="">Windows' default output</option>
                {outputs.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                    {d.isDefault ? " (default)" : ""}
                  </option>
                ))}
                {s.outputId && !outputs.some((d) => d.id === s.outputId) && <option value={s.outputId}>Not connected</option>}
              </select>
            </div>
          </div>
          <div className="field">
            <div className="field-label">
              <span>Boost</span>
              <span className="sub">Loud parts are turned down smoothly, so a big boost never crackles.</span>
            </div>
            <div className="field-control">
              <Slider
                label="Boost"
                min={100}
                max={600}
                step={10}
                value={s.gainPercent}
                format={(v) => `${v}%`}
                onCommit={(v) => void set({ gainPercent: v })}
              />
            </div>
          </div>
          <div className="field">
            <div className="field-label">Level</div>
            <div className="field-control" style={{ minWidth: 160 }}>
              <div className="bar" role="meter" aria-label="Sound level" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(level * 100)}>
                <div style={{ width: `${Math.round(level * 100)}%` }} />
              </div>
            </div>
          </div>
        </>
      )}
    </section>
  );
}

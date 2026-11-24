import { useEffect, useState } from "react";
import { call } from "../bridge";
import { useAction, useQuery } from "../hooks";
import { Notice, Switch } from "./controls";
import type { PanelState } from "../types";

/** Settings for the in-game panel (Ctrl+Alt+G), on the Games page. */
export function PanelSettings() {
  const q = useQuery<PanelState>("overlay.getState");
  const { busy, run } = useAction();
  const [hotkey, setHotkey] = useState("");
  const s = q.data;
  useEffect(() => setHotkey(s?.hotkey ?? ""), [s?.hotkey]);
  if (!s) return null;
  const save = async (patch: Record<string, unknown>) => {
    const r = await run("panel", () => call<PanelState>("overlay.setSettings", patch));
    if (r) q.setData(r);
  };
  return (
    <section className="panel" aria-label="In-game panel">
      <div className="panel-head">
        <div>
          <h2>In-game panel</h2>
          <div className="sub">
            Music, volume, games, Discord and Phone Link over the game. It shows over windowed and borderless games, not exclusive full screen; nothing is
            added to the game itself.
          </div>
        </div>
        <Switch checked={s.enabled} disabled={busy !== null} onChange={(v) => void save({ enabled: v })} label="On" />
      </div>
      {s.hotkeyProblem && <Notice tone="warn" title="The shortcut isn't working">{s.hotkeyProblem}</Notice>}
      <div className="field">
        <div className="field-label">
          <span>Shortcut</span>
          <span className="sub">For example Ctrl+Alt+G.</span>
        </div>
        <div className="field-control" style={{ display: "flex", gap: 8 }}>
          <input className="input" style={{ width: 140 }} value={hotkey} aria-label="Panel shortcut" onChange={(e) => setHotkey(e.target.value)} />
          <button className="btn" disabled={busy !== null || hotkey.trim() === s.hotkey} onClick={() => void save({ hotkey: hotkey.trim() })}>
            Save
          </button>
        </div>
      </div>
      <Switch
        checked={s.controller}
        disabled={busy !== null || !s.enabled}
        onChange={(v) => void save({ controller: v })}
        label="The controller's Xbox / PS button opens it, and the controller moves around it"
      />
      {s.controller && (
        <div className="stack" style={{ gap: 6, marginLeft: 24 }}>
          <Switch
            checked={s.gameBarButtonOff}
            disabled={busy !== null || !s.enabled}
            onChange={(v) => void save({ gameBarButtonOff: v })}
            label="Stop that button opening Xbox Game Bar (a Windows setting; put back when you untick this)"
          />
          <Switch
            checked={s.pauseRemotePlay}
            disabled={busy !== null || !s.enabled}
            onChange={(v) => void save({ pauseRemotePlay: v })}
            label="Pause PS Remote Play while the panel is open, so the PlayStation doesn't get the controller"
          />
          <div className="sub faint">
            The game still gets the same button presses. In Steam, turn off Settings &gt; Controller &gt; "Guide button focuses Steam" if Steam takes the button.
          </div>
        </div>
      )}
    </section>
  );
}

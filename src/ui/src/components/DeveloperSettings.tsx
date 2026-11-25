import { useState } from "react";
import { call } from "../bridge";
import { useAction, useQuery } from "../hooks";
import { goTo } from "../nav";
import type { DevStatus } from "../types";
import { Notice } from "./controls";

/**
 * Settings > Developer. Only drawn for a GitHub account that may push to RigControl's repository (the app refuses the
 * calls for anyone else). Registers this PC's Windows Hello key and takes the token made for it.
 */
export function DeveloperSettings() {
  const status = useQuery<DevStatus>("dev.getStatus");
  const { busy, run } = useAction();
  const [token, setToken] = useState("");
  const s = status.data;

  const register = async () => {
    const r = await run("register", () => call<DevStatus>("dev.registerPc", undefined, 180000), "This PC is registered.");
    if (r) status.setData(r);
  };
  const save = async () => {
    const r = await run("token", () => call<DevStatus>("dev.setToken", { token }), "Saved. Open the Developer page to unlock it.");
    if (r) {
      status.setData(r);
      setToken("");
    }
  };
  const forget = async () => {
    const r = await run("forget", () => call<DevStatus>("dev.forget"));
    if (r) status.setData(r);
  };

  if (status.error || !s) return null;

  return (
    <section className="panel">
      <h2>Developer</h2>
      <div className="sub" style={{ marginBottom: 10 }}>
        Dev mode is tied to this PC's security chip through Windows Hello. Each time the Developer page opens, Windows
        Hello asks for you.
      </div>
      {!s.supported ? (
        <Notice tone="warn" title="Windows Hello is not set up">{s.notSupported}</Notice>
      ) : (
        <div className="stack">
          {s.keyProblem && <Notice tone="warn" title="Windows Hello">{s.keyProblem}</Notice>}
          <div className="spread">
            <span>{s.registered ? `This PC (${s.pcName}) is registered.` : `Register this PC (${s.pcName}) for dev mode.`}</span>
            <button className="btn small" disabled={busy !== null} onClick={() => void register()}>
              {s.registered ? "Register again" : "Register this PC"}
            </button>
          </div>
          {s.request && !s.enrolled && (
            <div className="stack">
              <span className="sub">Send this registration request to get a token for this PC:</span>
              <div className="row">
                <code style={{ wordBreak: "break-all", userSelect: "all", flex: 1 }}>{s.request}</code>
                <button className="btn small" onClick={() => void navigator.clipboard?.writeText(s.request ?? "").catch(() => {})}>
                  Copy
                </button>
              </div>
              <label className="stack">
                <span>Token for this PC</span>
                <textarea className="input" rows={3} value={token} onChange={(e) => setToken(e.target.value)} placeholder="RCDEV2..." />
              </label>
              <div className="row">
                <button className="btn primary" disabled={token.trim().length === 0 || busy !== null} onClick={() => void save()}>
                  Save token
                </button>
              </div>
            </div>
          )}
          {s.tokenProblem && <Notice tone="warn" title="The saved token does not work here">{s.tokenProblem}</Notice>}
          {s.enrolled && (
            <div className="spread">
              <span>Dev mode is set up on this PC{s.subject ? ` for ${s.subject}` : ""}.</span>
              <span className="row">
                <button className="btn small" onClick={() => goTo("developer")}>
                  Open Developer
                </button>
                <button className="btn small" disabled={busy !== null} onClick={() => void forget()}>
                  Remove from this PC
                </button>
              </span>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

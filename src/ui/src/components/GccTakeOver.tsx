import { call } from "../bridge";
import { useAction, useQuery } from "../hooks";
import { Notice } from "./controls";
import type { GccState, LightingStatus } from "../types";

/** GIGABYTE Control Center overrides the graphics card's lights: let the user hand them to RigControl. */
export function GccTakeOver({
  onStatus,
  cardLeftOut,
}: {
  onStatus: (s: LightingStatus) => void;
  cardLeftOut: boolean;
}) {
  const q = useQuery<GccState>("lighting.getGcc");
  const { busy, run } = useAction();
  const s = q.data;
  if (!s || !s.installed) return null;
  const takeOver = async () => {
    const r = await run(
      "gcc",
      () =>
        call<{ gcc: GccState; status: LightingStatus }>(
          "lighting.takeOverGcc",
          undefined,
          30000,
        ),
      "GIGABYTE Control Center closed",
    );
    if (r) {
      q.setData(r.gcc);
      onStatus(r.status);
    }
  };
  const giveBack = async () => {
    const r = await run(
      "gccBack",
      () =>
        call<{ gcc: GccState; status: LightingStatus }>(
          "lighting.giveBackGcc",
          undefined,
          30000,
        ),
      "GIGABYTE Control Center runs the graphics card again",
    );
    if (r) {
      q.setData(r.gcc);
      onStatus(r.status);
    }
  };
  if ((s.running || s.startsWithWindows) && cardLeftOut) {
    return (
      <div className="row" style={{ gap: 8, alignItems: "center" }}>
        <span className="sub">
          GIGABYTE Control Center runs the graphics card's lights; RigControl
          leaves the card out (this card's light chip locks up when OpenRGB
          drives it for long).
        </span>
        <button
          className="btn small ghost"
          disabled={busy !== null}
          onClick={() => void takeOver()}
        >
          {busy === "gcc" ? "Taking over…" : "Take over anyway"}
        </button>
      </div>
    );
  }
  if (s.running || s.startsWithWindows) {
    return (
      <Notice
        tone="warn"
        title="GIGABYTE Control Center controls the graphics card's lights"
        actions={
          <button
            className="btn primary"
            disabled={busy !== null}
            onClick={() => void takeOver()}
          >
            {busy === "gcc" ? "Taking over…" : "Take over"}
          </button>
        }
      >
        It keeps overriding the colours RigControl sends to the graphics card.
        Take over closes it and stops it starting with Windows (RigControl
        already runs the card's fans). You can let it start again here later.
      </Notice>
    );
  }
  if (s.disabledByRigControl) {
    return (
      <div className="row" style={{ gap: 8, alignItems: "center" }}>
        <span className="sub">
          GIGABYTE Control Center is closed and doesn't start with Windows
          (RigControl took over the graphics card's lights).
        </span>
        <button
          className="btn small"
          disabled={busy !== null}
          onClick={() => void giveBack()}
        >
          {busy === "gccBack"
            ? "Giving it back…"
            : "Give the graphics card back to GCC"}
        </button>
      </div>
    );
  }
  return null;
}

import { useState } from "react";
import { call } from "../bridge";
import { useAction, useQuery } from "../hooks";
import type { NanoleafInfo } from "../types";

/** Pairs a Nanoleaf directly, so each of its panels takes its own part of the effect (not one colour through the hub). */
export function NanoleafPair() {
  const q = useQuery<NanoleafInfo>("lighting.getNanoleaf");
  const { busy, run } = useAction();
  const [found, setFound] = useState<string[] | null>(null);
  const n = q.data;
  if (!n) return null;

  const find = async () => {
    const r = await run("find", () =>
      call<NanoleafInfo>("lighting.findNanoleaf", undefined, 30000),
    );
    if (r) setFound(r.found ?? []);
  };
  const pair = async (ip: string) => {
    const r = await run(
      "pair-" + ip,
      () => call<NanoleafInfo>("lighting.pairNanoleaf", { ip }, 20000),
      "Nanoleaf paired",
    );
    if (r) {
      q.setData(r);
      setFound(null);
    }
  };
  const forget = async () => {
    const r = await run("forget", () =>
      call<NanoleafInfo>("lighting.forgetNanoleaf", undefined, 10000),
    );
    if (r) q.setData(r);
  };

  return (
    <section className="panel" aria-label="Nanoleaf panel by panel">
      <div className="panel-head">
        <div>
          <h2>Nanoleaf, panel by panel</h2>
          <div className="sub">
            {n.paired
              ? `${n.name} at ${n.ip}: ${n.panels} panel${n.panels === 1 ? "" : "s"}. Place it on the Layout above (Per LED on) and each panel takes the part of the effect under it, about 10 times a second.`
              : "Pair your Nanoleaf here once and every panel gets its own part of the effect."}
            {n.paired && n.streaming ? " Playing now." : ""}
            {n.problem ? ` Problem: ${n.problem}` : ""}
          </div>
        </div>
        {n.paired ? (
          <button
            className="btn small ghost"
            disabled={busy !== null}
            onClick={() => void forget()}
          >
            Forget
          </button>
        ) : (
          <button
            className="btn small"
            disabled={busy !== null}
            onClick={() => void find()}
          >
            {busy === "find" ? "Looking…" : "Find my Nanoleaf"}
          </button>
        )}
      </div>
      {!n.paired && found && (
        <div className="stack">
          {found.length === 0 ? (
            <div className="sub">
              No Nanoleaf answered on this network. Check it's on the same Wi-Fi
              as the PC.
            </div>
          ) : (
            <>
              <div className="sub">
                Hold the Nanoleaf's power button for 5 to 7 seconds until its
                lights flash, then press Pair within 30 seconds.
              </div>
              {found.map((ip) => (
                <div className="field" key={ip}>
                  <div className="field-label">Nanoleaf at {ip}</div>
                  <div className="field-control">
                    <button
                      className="btn small"
                      disabled={busy !== null}
                      onClick={() => void pair(ip)}
                    >
                      {busy === "pair-" + ip ? "Pairing…" : "Pair"}
                    </button>
                  </div>
                </div>
              ))}
            </>
          )}
        </div>
      )}
    </section>
  );
}

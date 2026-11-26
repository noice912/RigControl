import { useEffect, useState } from "react";
import { call } from "../bridge";
import { useAction, useLive } from "../hooks";
import type { LinkStatus } from "../types";
import { Notice, useConfirm } from "./controls";

function when(iso: string | null) {
  if (!iso) return "never";
  const d = new Date(iso);
  const mins = Math.round((Date.now() - d.getTime()) / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins} min ago`;
  return d.toLocaleString();
}

/** Minutes and seconds until `iso`, ticking every second. */
function useCountdown(iso: string | null) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!iso) return;
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [iso]);
  if (!iso) return null;
  const left = Math.max(0, Math.floor((new Date(iso).getTime() - now) / 1000));
  return { left, text: `${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")}` };
}

/** Settings > Phone: link a phone with a code or QR, and the list of linked phones. */
export function PhoneLinkPanel() {
  const { data: s, setData, error } = useLive("link.status", "link.getStatus");
  const { busy, run } = useAction();
  const { confirm, element: confirmDialog } = useConfirm();
  const countdown = useCountdown(s?.pairing?.expiresAt ?? null);

  const start = async () => {
    const r = await run("pair", () => call<LinkStatus>("link.startPairing"));
    if (r) setData(r);
  };
  const cancel = async () => {
    const r = await run("cancel", () => call<LinkStatus>("link.cancelPairing"));
    if (r) setData(r);
  };
  const remove = async (id: string, name: string) => {
    const ok = await confirm({
      title: `Remove "${name}"?`,
      body: "That phone can no longer see or control this PC. To use it again, link it again with a new code.",
      confirmLabel: "Remove",
      danger: true,
    });
    if (!ok) return;
    const r = await run("remove", () => call<LinkStatus>("link.removePhone", { id }));
    if (r) setData(r);
  };

  const resetKeys = async () => {
    const ok = await confirm({
      title: "Make new link keys?",
      body:
        "This PC's stored link keys can't be read, so no phone can connect. Making new keys removes the phones linked " +
        "now; link each one again with a new code.",
      confirmLabel: "Make new keys",
      danger: true,
    });
    if (!ok) return;
    const r = await run("resetKeys", () => call<LinkStatus>("link.resetKeys", { confirm: true }));
    if (r) setData(r);
  };

  const pairing = s?.pairing ?? null;
  const expired = countdown !== null && countdown.left === 0;

  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          <h2>Phone</h2>
          <div className="sub">
            See temperatures, fans and recording on your phone, and switch lights, recording and your Discord status
            from it. Phone and PC both connect out to a relay that passes on end-to-end encrypted messages; it can read
            none of them, and no port is opened on this PC.
          </div>
        </div>
        {s?.relaySet && !pairing && !s.keysUnreadable && (
          <button className="btn primary" disabled={busy !== null} onClick={() => void start()}>
            Link a phone
          </button>
        )}
      </div>

      {error && <Notice tone="error" title="The phone link isn't available">{error}</Notice>}
      {s && !s.relaySet && (
        <Notice tone="warn" title="Relay address not set in this build">
          {s.message}
        </Notice>
      )}
      {s?.keysUnreadable && (
        <Notice tone="warn" title="The link keys can't be read">
          {s.message}{" "}
          <button className="btn small" disabled={busy !== null} onClick={() => void resetKeys()}>
            Make new keys
          </button>
        </Notice>
      )}
      {s?.pairingError && <Notice tone="warn" title="Not linked">{s.pairingError}</Notice>}

      {pairing && (
        <div className="phone-pair">
          <img
            className="phone-qr"
            alt="QR code for linking a phone"
            width={180}
            height={180}
            src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(pairing.qrSvg)}`}
          />
          <div>
            <div className="sub">In the RigControl phone app, choose Link a PC and scan this code, or type:</div>
            <div className="phone-code" aria-label={`Pairing code ${pairing.groups.join(" ")}`}>
              {pairing.groups.map((g, i) => (
                <span key={i}>{g}</span>
              ))}
            </div>
            <div className="sub">
              {expired
                ? "This code has run out."
                : `Works once, for ${countdown?.text ?? "10:00"} more. Only show it to the phone you want to link.`}
            </div>
            <div className="row">
              {expired && (
                <button className="btn primary" disabled={busy !== null} onClick={() => void start()}>
                  New code
                </button>
              )}
              <button className="btn" disabled={busy !== null} onClick={() => void cancel()}>
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}

      {s && (
        <div>
          {s.phones.length === 0 ? (
            <div className="sub faint">No phone linked. Nothing connects to the relay until you link one.</div>
          ) : (
            s.phones.map((p) => (
              <div className="field" key={p.id}>
                <div className="field-label">
                  <span>{p.name}</span>
                  <span className="sub">
                    {p.online ? "Connected now" : `Last seen ${when(p.lastSeen)}`} · linked {new Date(p.added).toLocaleDateString()}
                  </span>
                </div>
                <div className="field-control">
                  <button className="btn small" disabled={busy !== null} onClick={() => void remove(p.id, p.name)}>
                    Remove
                  </button>
                </div>
              </div>
            ))
          )}
          {s.relaySet && (s.phones.length > 0 || pairing) && (
            <div className="sub faint">
              {s.message} Relay: {s.relay}
            </div>
          )}
        </div>
      )}
      {confirmDialog}
    </section>
  );
}

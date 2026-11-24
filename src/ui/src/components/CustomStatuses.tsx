import { useEffect, useState } from "react";
import { call } from "../bridge";
import { useAction } from "../hooks";
import { Notice, Switch } from "./controls";
import type { CustomStatus, PresenceStatus } from "../types";

const TYPES: { value: number; label: string }[] = [
  { value: 0, label: "Playing" },
  { value: 2, label: "Listening to" },
  { value: 3, label: "Watching" },
  { value: 5, label: "Competing in" },
];

function blank(): CustomStatus {
  return {
    id: Math.random().toString(36).slice(2, 14),
    enabled: false,
    type: 0,
    name: "",
    details: "",
    state: "",
    large_image: "",
    large_text: "",
    small_image: "",
    small_text: "",
    button1_label: "",
    button1_url: "",
    button2_label: "",
    button2_url: "",
    elapsed: true,
    app_id: "",
  };
}

function label(s: CustomStatus): string {
  return s.name.trim() || s.details.trim() || s.state.trim() || "Untitled";
}

function Text(props: { label: string; value: string; onChange: (v: string) => void; max?: number; placeholder?: string; hint?: string }) {
  return (
    <div className="field">
      <div className="field-label">
        <span>{props.label}</span>
        {props.hint && <span className="sub">{props.hint}</span>}
      </div>
      <div className="field-control">
        <input className="input" style={{ width: 280 }} value={props.value} maxLength={props.max} placeholder={props.placeholder} aria-label={props.label} onChange={(e) => props.onChange(e.target.value)} />
      </div>
    </div>
  );
}

/** Your own cards: text, pictures, buttons (RichPresence's "Custom status" tab). */
export function CustomStatusesPanel({ status, onStatus }: { status: PresenceStatus; onStatus: (s: PresenceStatus) => void }) {
  const { busy, run } = useAction();
  const [items, setItems] = useState<CustomStatus[]>(status.settings.customStatuses);
  const [mode, setMode] = useState(status.settings.customMode);
  const [open, setOpen] = useState<string | null>(null);
  const saved = JSON.stringify({ i: status.settings.customStatuses, m: status.settings.customMode });
  useEffect(() => {
    setItems(status.settings.customStatuses);
    setMode(status.settings.customMode);
  }, [saved]);
  const dirty = JSON.stringify({ i: items, m: mode }) !== saved;

  const patch = (id: string, p: Partial<CustomStatus>) => setItems((list) => list.map((x) => (x.id === id ? { ...x, ...p } : x)));
  const move = (i: number, d: number) =>
    setItems((list) => {
      const j = i + d;
      if (j < 0 || j >= list.length) return list;
      const copy = [...list];
      const a = copy[i]!;
      copy[i] = copy[j]!;
      copy[j] = a;
      return copy;
    });
  const save = async () => {
    const r = await run("custom", () => call<PresenceStatus>("presence.setCustomStatuses", { items, mode }), "Custom statuses saved");
    if (r) onStatus(r);
  };
  const ticked = items.filter((x) => x.enabled).length;

  return (
    <section className="panel" aria-label="Custom statuses">
      <div className="panel-head">
        <div>
          <h2>Custom statuses</h2>
          <div className="sub">
            Cards of your own: text, pictures and buttons. Without your own Discord app id, two can show at once; or merge the ticked ones into one card.
          </div>
        </div>
        <button className="btn" disabled={busy !== null || items.length >= 20} onClick={() => { const b = blank(); setItems([...items, b]); setOpen(b.id); }}>
          Add
        </button>
      </div>
      <div className="field">
        <div className="field-label">Show ticked ones</div>
        <div className="field-control">
          <select className="select" value={mode} aria-label="Custom status mode" onChange={(e) => setMode(e.target.value)}>
            <option value="separate">As separate cards</option>
            <option value="merge">Merged into one card</option>
          </select>
        </div>
      </div>
      {status.customSkipped && status.customSkipped.length > 0 && (
        <Notice tone="warn" title="Not enough cards">
          No free card for {status.customSkipped.join(", ")}. Give it its own Discord app id, or merge your statuses into one card.
        </Notice>
      )}
      {items.length === 0 && <div className="empty">No custom statuses yet.</div>}
      {items.map((s, i) => {
        const err = status.customErrors?.[s.id];
        return (
          <div key={s.id} className="stack" style={{ gap: 8, borderTop: "1px solid var(--line, rgba(255,255,255,0.08))", paddingTop: 10 }}>
            <div className="spread">
              <Switch checked={s.enabled} onChange={(v) => patch(s.id, { enabled: v })} label={label(s)} />
              <span className="row" style={{ gap: 6 }}>
                <button className="btn small" disabled={i === 0} onClick={() => move(i, -1)} aria-label="Move up">↑</button>
                <button className="btn small" disabled={i === items.length - 1} onClick={() => move(i, 1)} aria-label="Move down">↓</button>
                <button className="btn small" onClick={() => setOpen(open === s.id ? null : s.id)}>{open === s.id ? "Close" : "Edit"}</button>
                <button className="btn small" onClick={() => setItems(items.filter((x) => x.id !== s.id))}>Delete</button>
              </span>
            </div>
            {err && <Notice tone="warn" title="Discord refused this card">{err}</Notice>}
            {open === s.id && (
              <div className="stack" style={{ gap: 6 }}>
                <div className="field">
                  <div className="field-label">Type</div>
                  <div className="field-control">
                    <select className="select" value={s.type} aria-label="Type" onChange={(e) => patch(s.id, { type: Number(e.target.value) })}>
                      {TYPES.map((t) => (
                        <option key={t.value} value={t.value}>{t.label}</option>
                      ))}
                    </select>
                  </div>
                </div>
                <Text label="Title" value={s.name} max={128} onChange={(v) => patch(s.id, { name: v })} />
                <Text label="First line" value={s.details} max={128} onChange={(v) => patch(s.id, { details: v })} />
                <Text label="Second line" value={s.state} max={128} onChange={(v) => patch(s.id, { state: v })} />
                <Text label="Big picture" value={s.large_image} placeholder="https://…" hint="An https link, or a picture name from your own Discord app." onChange={(v) => patch(s.id, { large_image: v })} />
                <Text label="Big picture text" value={s.large_text} max={128} onChange={(v) => patch(s.id, { large_text: v })} />
                <Text label="Small picture" value={s.small_image} placeholder="https://…" onChange={(v) => patch(s.id, { small_image: v })} />
                <Text label="Small picture text" value={s.small_text} max={128} onChange={(v) => patch(s.id, { small_text: v })} />
                <Text label="Button 1" value={s.button1_label} max={32} onChange={(v) => patch(s.id, { button1_label: v })} />
                <Text label="Button 1 link" value={s.button1_url} placeholder="https://…" onChange={(v) => patch(s.id, { button1_url: v })} />
                <Text label="Button 2" value={s.button2_label} max={32} onChange={(v) => patch(s.id, { button2_label: v })} />
                <Text label="Button 2 link" value={s.button2_url} placeholder="https://…" onChange={(v) => patch(s.id, { button2_url: v })} />
                <Text label="Your Discord app id" value={s.app_id} hint="Optional. Gives this status its own card." onChange={(v) => patch(s.id, { app_id: v })} />
                <Switch checked={s.elapsed} onChange={(v) => patch(s.id, { elapsed: v })} label="Show how long it has been on" />
              </div>
            )}
          </div>
        );
      })}
      <div className="row" style={{ gap: 8 }}>
        <button className="btn primary" disabled={busy !== null || !dirty} onClick={() => void save()}>
          Save
        </button>
        {dirty && (
          <button className="btn" disabled={busy !== null} onClick={() => { setItems(status.settings.customStatuses); setMode(status.settings.customMode); }}>
            Undo
          </button>
        )}
        <span className="sub">{ticked} ticked{status.customShown ? `, ${status.customShown} showing` : ""}.</span>
      </div>
    </section>
  );
}

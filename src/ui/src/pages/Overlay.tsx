import { useCallback, useEffect, useState } from "react";
import { call, on } from "../bridge";
import { useLive } from "../hooks";
import type { PanelAudio, PanelState, PresenceStatus } from "../types";

/** The panel over the game (Ctrl+Alt+G), RichPresence's overlay rebuilt. Shown in its own window. */

function Widget(props: { title: string; children: React.ReactNode; grow?: boolean }) {
  return (
    <section className="panel" style={{ padding: 12, gap: 8, display: "flex", flexDirection: "column", minHeight: 0, flex: props.grow ? 1 : undefined }}>
      <div className="sub" style={{ textTransform: "uppercase", letterSpacing: 0.6, fontSize: 12 }}>{props.title}</div>
      {props.children}
    </section>
  );
}

function Btn(props: { onClick: () => void; children: React.ReactNode; primary?: boolean; disabled?: boolean; title?: string; wide?: boolean }) {
  return (
    <button
      className={`btn ${props.primary ? "primary" : ""}`}
      data-pad=""
      disabled={props.disabled}
      title={props.title}
      onClick={props.onClick}
      style={{ flex: props.wide ? 1 : undefined, minHeight: 36, minWidth: 0, paddingLeft: 6, paddingRight: 6, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}
    >
      {props.children}
    </button>
  );
}

function run(p: Promise<unknown>) {
  p.catch(() => {});
}

function Music({ s }: { s: PresenceStatus | null }) {
  const m = s?.music;
  return (
    <Widget title="Music">
      <div style={{ fontSize: 18, fontWeight: 600, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{m?.title || "Nothing playing"}</div>
      <div className="sub" style={{ whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
        {m ? `${m.artist}${m.player ? ` · ${m.player}` : ""}` : s?.enabled ? "Apple Music or Spotify shows here" : "Turn on Discord status to see the song here"}
      </div>
      <div style={{ color: "var(--accent, #4cc2ff)", minHeight: 40, fontSize: 15 }}>{s?.lyricLine ?? ""}</div>
      <div className="row" style={{ gap: 8 }}>
        <Btn wide onClick={() => run(call("overlay.mediaKey", { key: "prev" }))} title="Previous">⏮</Btn>
        <Btn wide onClick={() => run(call("overlay.mediaKey", { key: "play" }))} title="Play / pause">⏯</Btn>
        <Btn wide onClick={() => run(call("overlay.mediaKey", { key: "next" }))} title="Next">⏭</Btn>
      </div>
    </Widget>
  );
}

function Presence({ s, setS }: { s: PresenceStatus | null; setS: (v: PresenceStatus) => void }) {
  if (!s) return null;
  const toggleCustom = async (id: string) => {
    const items = s.settings.customStatuses.map((x) => (x.id === id ? { ...x, enabled: !x.enabled } : x));
    const r = await call<PresenceStatus>("presence.setCustomStatuses", { items, mode: s.settings.customMode }).catch(() => null);
    if (r) setS(r);
  };
  return (
    <Widget title="Discord status">
      <div className="sub" style={{ minHeight: 20 }}>{s.blocked ?? s.status}</div>
      <div className="row" style={{ gap: 8 }}>
        <Btn wide primary={!s.enabled} onClick={async () => { const r = await call<PresenceStatus>("presence.setSettings", { enabled: !s.enabled }).catch(() => null); if (r) setS(r); }}>
          {s.enabled ? "Stop showing" : "Show on Discord"}
        </Btn>
      </div>
      {s.settings.customStatuses.length > 0 && (
        <div className="stack" style={{ gap: 4, overflow: "auto" }}>
          {s.settings.customStatuses.map((c) => (
            <button key={c.id} className="btn" data-pad="" onClick={() => void toggleCustom(c.id)} style={{ justifyContent: "flex-start", textAlign: "left" }}>
              {c.enabled ? "☑" : "☐"} {c.name || c.details || c.state || "Untitled"}
            </button>
          ))}
        </div>
      )}
    </Widget>
  );
}

const STORES: { name: string; items: [string, string][] }[] = [
  { name: "Steam", items: [["steam.library", "Library"], ["steam.bigpicture", "Big Picture"], ["steam.friends", "Friends"]] },
  { name: "Xbox", items: [["xbox.app", "Xbox app"], ["xbox.cloud", "Cloud"], ["xbox.profile", "Profile"]] },
  { name: "PlayStation", items: [["ps.remoteplay", "Remote Play"], ["ps.library", "Library"], ["ps.store", "Store"]] },
];

function Stores() {
  return (
    <Widget title="Steam, Xbox & PlayStation">
      {STORES.map((r) => (
        <div key={r.name} style={{ display: "flex", gap: 6, alignItems: "center", flexWrap: "nowrap" }}>
          <span className="sub" style={{ width: 76, flexShrink: 0 }}>{r.name}</span>
          {r.items.map(([id, label]) => (
            <Btn key={id} wide onClick={() => run(call("overlay.open", { id }))}>{label}</Btn>
          ))}
        </div>
      ))}
    </Widget>
  );
}

function Volume({ audio, setAudio }: { audio: PanelAudio | null; setAudio: (a: PanelAudio) => void }) {
  const set = async (pid: number, patch: { volume?: number; muted?: boolean }) => {
    const r = await call<PanelAudio>("overlay.setVolume", { pid, ...patch }).catch(() => null);
    if (r) setAudio(r);
  };
  const Row = ({ pid, name, volume, muted }: { pid: number; name: string; volume: number; muted: boolean }) => (
    <div className="stack" style={{ gap: 2 }}>
      <div className="spread">
        <span style={{ whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", maxWidth: 230 }}>{name}</span>
        <button className="btn small" data-pad="" onClick={() => void set(pid, { muted: !muted })}>{muted ? "Unmute" : "Mute"}</button>
      </div>
      <input
        type="range"
        min={0}
        max={100}
        value={volume}
        data-pad=""
        aria-label={`${name} volume`}
        onChange={(e) => void set(pid, { volume: Number(e.target.value) })}
        style={{ width: "100%" }}
      />
    </div>
  );
  return (
    <Widget title="Volume" grow>
      {!audio ? (
        <div className="sub">Reading…</div>
      ) : (
        <div className="stack" style={{ gap: 10, overflowY: "auto", overflowX: "hidden", paddingRight: 4 }}>
          <Row pid={0} name="PC" volume={audio.master} muted={audio.masterMuted} />
          {audio.apps.map((a) => (
            <Row key={a.pid} pid={a.pid} name={a.name} volume={a.volume} muted={a.muted} />
          ))}
        </div>
      )}
    </Widget>
  );
}

function Games({ s }: { s: PresenceStatus | null }) {
  const games = s?.games ?? [];
  return (
    <Widget title="Games" grow>
      {games.length === 0 && <div className="sub">No games found yet.</div>}
      <div className="stack" style={{ gap: 6, overflow: "auto" }}>
        {games.map((g) => (
          <button
            key={g.id}
            className="btn"
            data-pad=""
            disabled={!g.canPlay || g.running}
            onClick={() => { run(call("presence.play", { id: g.id })); run(call("overlay.hide")); }}
            style={{ justifyContent: "space-between", display: "flex", textAlign: "left" }}
          >
            <span style={{ whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>{g.name}</span>
            <span className="sub">{g.running ? "Running" : g.store}</span>
          </button>
        ))}
      </div>
    </Widget>
  );
}

function DiscordAndPhone({ state, refresh }: { state: PanelState | null; refresh: () => void }) {
  const [number, setNumber] = useState("");
  const [note, setNote] = useState<string | null>(null);
  const phone = (kind: string) =>
    call("overlay.phone", { kind, number })
      .then(() => setNote(null))
      .catch((e: Error) => setNote(e.message));
  return (
    <>
      <Widget title="Discord">
        <div className="sub">Discord's own web app inside the panel: messages, groups and calls. Closing the panel keeps a call going.</div>
        {state?.discordProblem && <div className="sub" style={{ color: "var(--warn, #f0b232)" }}>{state.discordProblem}</div>}
        <Btn primary onClick={() => run(call("overlay.openDiscord").then(refresh))}>Open Discord</Btn>
        {state?.discordSignedIn && (
          <Btn onClick={() => run(call("overlay.forgetDiscord").then(refresh))} title="Closes Discord here and deletes its sign-in and cookies from this PC">
            Sign out and forget
          </Btn>
        )}
      </Widget>
      <Widget title="Phone">
        <input className="input" value={number} placeholder="Phone number" aria-label="Phone number" inputMode="tel" onChange={(e) => setNumber(e.target.value)} />
        <div className="row" style={{ gap: 8 }}>
          <Btn wide onClick={() => void phone("call")}>Call</Btn>
          <Btn wide onClick={() => void phone("text")}>Text</Btn>
          <Btn wide onClick={() => void phone("open")}>Phone Link</Btn>
        </div>
        {note && <div className="sub">{note}</div>}
      </Widget>
    </>
  );
}

/**
 * Controller navigation (RichPresence's Move-OvFocus): the nearest control in the pressed direction, by distance
 * along the direction plus 3x the sideways distance. Left/right on a slider changes it instead.
 */
function padMove(what: string) {
  const all = Array.from(document.querySelectorAll<HTMLElement>("[data-pad]")).filter(
    (el) => !(el as HTMLButtonElement).disabled && el.offsetParent !== null,
  );
  if (all.length === 0) return;
  const cur = document.activeElement instanceof HTMLElement && all.includes(document.activeElement) ? document.activeElement : null;
  const mark = (el: HTMLElement) => {
    document.querySelectorAll(".pad-focus").forEach((x) => x.classList.remove("pad-focus"));
    el.classList.add("pad-focus");
    el.focus({ preventScroll: false });
    el.scrollIntoView({ block: "nearest" });
  };
  if (!cur) {
    mark(all[0]!);
    return;
  }
  if (what === "Accept") {
    cur.click();
    return;
  }
  if (cur instanceof HTMLInputElement && cur.type === "range" && (what === "Left" || what === "Right")) {
    const v = Math.max(0, Math.min(100, Number(cur.value) + (what === "Right" ? 5 : -5)));
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!;
    setter.call(cur, String(v));
    cur.dispatchEvent(new Event("input", { bubbles: true }));
    return;
  }
  const r = cur.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  let best: HTMLElement | null = null;
  let bestScore = Infinity;
  for (const el of all) {
    if (el === cur) continue;
    const b = el.getBoundingClientRect();
    const x = b.left + b.width / 2, y = b.top + b.height / 2;
    const dx = x - cx, dy = y - cy;
    let along: number, side: number;
    if (what === "Up") { along = -dy; side = Math.abs(dx); }
    else if (what === "Down") { along = dy; side = Math.abs(dx); }
    else if (what === "Left") { along = -dx; side = Math.abs(dy); }
    else { along = dx; side = Math.abs(dy); }
    if (along <= 4) continue;
    const score = along + 3 * side;
    if (score < bestScore) { bestScore = score; best = el; }
  }
  if (best) mark(best);
}

const HINTS: Record<string, string> = {
  widgets: "Controller: d-pad moves, A / Cross picks, B / Circle closes",
  pointer: "Stick: pointer · A / Cross: click · X / Square: right-click · Right stick: scroll · Y / Triangle: keyboard · B / Circle: back",
  keyboard: "D-pad: move · A / Cross: type · X / Square: delete · Y / Triangle: space · Start / Options: Enter · B / Circle: close keyboard",
};

function Clock() {
  const [t, setT] = useState(() => new Date());
  useEffect(() => {
    const id = window.setInterval(() => setT(new Date()), 1000);
    return () => window.clearInterval(id);
  }, []);
  return <span style={{ fontSize: 20, fontWeight: 600 }}>{t.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>;
}

export function Overlay() {
  const { data: s, setData: setS } = useLive("presence.status", "presence.getStatus");
  const [audio, setAudio] = useState<PanelAudio | null>(null);
  const [state, setState] = useState<PanelState | null>(null);
  const [wide, setWide] = useState(window.innerWidth >= 1500);
  const [more, setMore] = useState(false);
  const [discordOpen, setDiscordOpen] = useState(false);
  const [mode, setMode] = useState("widgets");

  const refresh = useCallback(() => {
    call<PanelState>("overlay.getState").then(setState).catch(() => {});
  }, []);
  const readAudio = useCallback(() => {
    call<PanelAudio>("overlay.getAudio").then(setAudio).catch(() => {});
  }, []);

  useEffect(() => {
    refresh();
    readAudio();
    const id = window.setInterval(readAudio, 2000);
    // The layout follows the room the page really has, so a panel window of the wrong size can't cut columns off.
    const fit = () => setWide(window.innerWidth >= 1500);
    window.addEventListener("resize", fit);
    const offShown = on("overlay.shown", (d) => {
      setWide(d.wide && window.innerWidth >= 1500);
      refresh();
      readAudio();
      call<PresenceStatus>("presence.getStatus").then(setS).catch(() => {});
    });
    const offDiscord = on("overlay.discord", (d) => {
      setDiscordOpen(d.open);
      refresh();
    });
    const offPad = on("overlay.pad", (d) => padMove(d.what));
    const offMode = on("overlay.mode", (d) => setMode(d.mode));
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") run(call("overlay.hide"));
    };
    window.addEventListener("keydown", onKey);
    return () => {
      window.clearInterval(id);
      offShown();
      offDiscord();
      offPad();
      offMode();
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", fit);
    };
  }, [refresh, readAudio, setS]);

  const colStyle = { display: "flex", flexDirection: "column" as const, gap: 12, minHeight: 0, flex: 1 };
  return (
    <div style={{ height: "100vh", display: "flex", flexDirection: "column", background: "var(--bg)", overflow: "hidden" }}>
      <div className="spread" style={{ height: 56, padding: "0 12px", alignItems: "center", background: "rgba(255,255,255,0.03)" }}>
        <span style={{ fontSize: 20, fontWeight: 600 }}>RigControl</span>
        <span className="sub" style={{ flex: 1, textAlign: "center", padding: "0 12px", whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }}>
          {state?.controller && HINTS[mode] && mode !== "widgets" ? HINTS[mode] : `${state?.hotkey ?? "Ctrl+Alt+G"} or Esc to close${state?.controller ? ". " + HINTS.widgets : ""}`}
        </span>
        <span className="row" style={{ gap: 10, alignItems: "center" }}>
          <Clock />
          <Btn primary={!discordOpen} onClick={() => run(call(discordOpen ? "overlay.closeDiscord" : "overlay.openDiscord").then(refresh))}>
            {discordOpen ? "Back" : "Discord"}
          </Btn>
          {!wide && <Btn onClick={() => setMore(!more)}>{more ? "Games" : "More"}</Btn>}
          <Btn onClick={() => run(call("overlay.hide"))} title="Close">✕</Btn>
        </span>
      </div>
      <div style={{ flex: 1, display: "flex", gap: 12, padding: 12, minHeight: 0 }}>
        <div style={colStyle}>
          <Music s={s} />
          <Presence s={s} setS={setS} />
          <Stores />
        </div>
        <div style={colStyle}>
          <Volume audio={audio} setAudio={setAudio} />
        </div>
        {(wide || !more) && (
          <div style={colStyle}>
            <Games s={s} />
          </div>
        )}
        {(wide || more) && (
          <div style={colStyle}>
            <DiscordAndPhone state={state} refresh={refresh} />
          </div>
        )}
      </div>
    </div>
  );
}

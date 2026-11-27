import { useEffect, useMemo, useState } from "react";
import { call } from "../bridge";
import { useAction, useLive } from "../hooks";
import { useApp } from "../appContext";
import { Notice, Switch } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import { HealthNotice } from "../components/HealthFindings";
import { CustomStatusesPanel } from "../components/CustomStatuses";
import { PanelSettings } from "../components/PanelSettings";
import type { PresenceGame, PresenceStatus } from "../types";

const artCache = new Map<string, string | null>();

function GameArt({ game }: { game: PresenceGame }) {
  const [url, setUrl] = useState<string | null>(artCache.get(game.id) ?? null);
  useEffect(() => {
    if (!game.hasArt || artCache.has(game.id)) return;
    let live = true;
    call<{ dataUrl: string } | null>("presence.getArt", { id: game.id })
      .then((r) => {
        artCache.set(game.id, r?.dataUrl ?? null);
        if (live) setUrl(r?.dataUrl ?? null);
      })
      .catch(() => artCache.set(game.id, null));
    return () => {
      live = false;
    };
  }, [game.id, game.hasArt]);
  return (
    <div
      style={{
        height: 96,
        borderRadius: 8,
        background: url ? `center / cover no-repeat url(${url})` : "var(--surface-2, rgba(255,255,255,0.05))",
        display: "grid",
        placeItems: "center",
        fontSize: 13,
        opacity: game.enabled ? 1 : 0.5,
      }}
      aria-hidden
    >
      {!url && <span className="sub">{game.store}</span>}
    </div>
  );
}

function GameTile({ game, onStatus }: { game: PresenceGame; onStatus: (s: PresenceStatus) => void }) {
  const { busy, run } = useAction();
  const play = () => void run("play", () => call("presence.play", { id: game.id }), `Starting ${game.name}`);
  const toggle = async (v: boolean) => {
    const r = await run("toggle", () => call<PresenceStatus>("presence.setGameEnabled", { id: game.id, enabled: v }));
    if (r) onStatus(r);
  };
  return (
    <div className="panel" style={{ padding: 10, gap: 8, display: "flex", flexDirection: "column" }}>
      <GameArt game={game} />
      <div className="spread" style={{ alignItems: "baseline" }}>
        <strong style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={game.name}>
          {game.name}
        </strong>
        {game.running && <span className="badge ok">Running</span>}
      </div>
      <div className="sub">
        {game.store}
        {game.matched ? " · official Discord game" : ""}
      </div>
      <div className="spread">
        <Switch checked={game.enabled} disabled={busy !== null} onChange={(v) => void toggle(v)} label="On Discord" />
        <button className="btn small" disabled={!game.canPlay || busy !== null || game.running} onClick={play}>
          Play
        </button>
      </div>
    </div>
  );
}

export function Games() {
  const { data: s, setData, error } = useLive("presence.status", "presence.getStatus");
  // Parts the Linux and macOS app does not have are left out. On Windows every one of these is true.
  const { info, can, notWindows } = useApp();
  const hasMedia = can("presence.media");
  const hasGames = can("presence.games");
  const hasWatching = can("presence.watching");
  const hasExtension = can("presence.extension");
  const hasPanel = !!info && can("host.overlay");
  const { busy, run } = useAction();
  const [filter, setFilter] = useState("");
  const [uid, setUid] = useState("");
  useEffect(() => setUid(s?.settings.genshinUid ?? ""), [s?.settings.genshinUid]);

  const games = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (s?.games ?? []).filter((g) => !f || g.name.toLowerCase().includes(f));
  }, [s?.games, filter]);

  const save = async (patch: Record<string, unknown>) => {
    const r = await run("settings", () => call<PresenceStatus>("presence.setSettings", patch));
    if (r) setData(r);
  };
  const [extPath, setExtPath] = useState<string | null>(null);
  const getExtension = async () => {
    const r = await run("ext", () => call<{ path: string }>("presence.getExtension"));
    if (r) setExtPath(r.path);
  };
  const disconnectSpotify = async () => {
    const r = await run("spotify", () => call<PresenceStatus>("presence.disconnectSpotify"), "Spotify disconnected");
    if (r) setData(r);
  };
  const rescan = async () => {
    const r = await run("scan", () => call<PresenceStatus>("presence.rescan"));
    if (r) setData(r);
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>Games</h1>
      </header>
      <ModuleProblem name="presence" />
      {/* "Waiting for RichPresence to close" below already says it. */}
      <HealthNotice modules={["presence", "overlay"]} exclude={s?.blocked ? ["presence.richpresence"] : undefined} />
      {error && <Notice tone="warn" title="Discord status is not answering">{error}</Notice>}
      {s && (
        <>
          <section className="panel" aria-label="Discord status">
            <div className="panel-head">
              <div>
                <h2>Discord status</h2>
                <div className="sub">
                  {hasGames && hasMedia
                    ? "Shows the game you're playing, your Apple Music or Spotify song and the app you're using on your Discord profile."
                    : "Shows your own statuses and a well-known program you have open on your Discord profile."}
                </div>
              </div>
              <Switch checked={s.enabled} disabled={busy !== null} onChange={(v) => void save({ enabled: v })} label="Show on Discord" />
            </div>
            {s.setupNote && <Notice tone="warn" title={s.setUp ? "Some cards can't be shown" : "Not set up in this build"}>{s.setupNote}</Notice>}
            {s.blocked && <Notice tone="warn" title="Waiting for RichPresence to close">{s.blocked}</Notice>}
            {s.attachedUser && (
              <div className="sub">
                <span className="status-dot" style={{ background: "var(--ok)", marginRight: 6 }} aria-hidden="true" />
                {s.enabled ? `Attached to ${s.attachedUser.name}'s Discord` : `Discord found, signed in as ${s.attachedUser.name}. Turn on Show on Discord to show your activity there.`}
              </div>
            )}
            {s.attachNote && <Notice title="Discord account changed">{s.attachNote}</Notice>}
            {!s.attachedUser && !s.blocked && s.discordReason && s.enabled && (
              <Notice tone={s.discordState !== "notRunning" ? "warn" : undefined} title="Not attached to your Discord">
                {s.discordReason}
              </Notice>
            )}
            {/* Switched off: no notice for a feature that is not in use. Only what would stop it working once it is switched on. */}
            {!s.attachedUser && !s.blocked && s.discordReason && !s.enabled && s.discordState !== "notRunning" && <div className="sub">{s.discordReason}</div>}
            {!s.attachedUser && !s.blocked && s.discordState === "found" && <div className="sub">Discord is running. Waiting for it to say which account is signed in.</div>}
            <div>
              <strong>{s.status}</strong>
            </div>
            {s.watching && <div className="sub">Watching {s.watching}</div>}
            {s.music && (
              <div className="sub">
                {s.music.playing ? "Playing" : "Paused"} in {s.music.player}
                {s.musicSource === "phone" ? " on your phone" : s.musicSource === "spotify" ? " (your Spotify account)" : ""}: {s.music.title}
                {s.music.artist ? ` · ${s.music.artist}` : ""}
                {s.lyricLine ? ` · “${s.lyricLine}”` : ""}
              </div>
            )}
            {s.spotifyConnected && (
              <div className="row" style={{ gap: 8, alignItems: "center" }}>
                <span className="sub">Spotify: connected from your phone</span>
                <button className="btn small" disabled={busy !== null} onClick={() => void disconnectSpotify()}>
                  Disconnect
                </button>
              </div>
            )}
            {s.spotifyProblem && <div className="sub">{s.spotifyProblem}.</div>}
            {s.importedFromRichPresence && <div className="sub faint">Your RichPresence settings were copied in on {new Date(s.importedFromRichPresence).toLocaleDateString()}.</div>}
          </section>

          <section className="panel" aria-label="What to show">
            <div className="panel-head">
              <div>
                <h2>What to show</h2>
              </div>
            </div>
            {hasMedia && (
              <>
                <Switch checked={s.settings.showMusic} disabled={busy !== null} onChange={(v) => void save({ showMusic: v })} label="Apple Music" />
                <Switch checked={s.settings.showSpotify} disabled={busy !== null} onChange={(v) => void save({ showSpotify: v })} label="Spotify" />
                <Switch checked={s.settings.showLyrics} disabled={busy !== null} onChange={(v) => void save({ showLyrics: v })} label="The current lyric line" />
                <Switch checked={s.settings.showAlbumArt} disabled={busy !== null} onChange={(v) => void save({ showAlbumArt: v })} label="Album art" />
              </>
            )}
            <Switch
              checked={s.settings.showCurrentApp}
              disabled={busy !== null}
              onChange={(v) => void save({ showCurrentApp: v })}
              label={notWindows ? "A well-known program I have open (its name only, never a window title)" : "The app I'm using, with its window or tab title"}
            />
            {hasWatching && (
              <Switch checked={s.settings.showWatching} disabled={busy !== null} onChange={(v) => void save({ showWatching: v })} label="What I'm watching (Netflix, Hulu, Disney+, Prime Video, Max, Crunchyroll, Paramount+, Peacock, Apple TV+, Plex)" />
            )}
            {hasWatching && s.settings.showWatching && (
              <div className="stack" style={{ gap: 6, marginLeft: 24 }}>
                <Switch checked={s.settings.showWatchTitle} disabled={busy !== null} onChange={(v) => void save({ showWatchTitle: v })} label="With the show or movie name" />
                <Switch checked={s.settings.showWatchPoster} disabled={busy !== null} onChange={(v) => void save({ showWatchPoster: v })} label="With its poster (looked up on TVMaze and Cinemeta)" />
                <Switch checked={s.settings.showWatchCasual} disabled={busy !== null} onChange={(v) => void save({ showWatchCasual: v })} label="Also YouTube and Twitch" />
                {hasExtension && (
                  <div className="row" style={{ gap: 8, alignItems: "center" }}>
                    <button className="btn small" disabled={busy !== null} onClick={() => void getExtension()}>
                      Get the browser extension
                    </button>
                    <span className="sub">Adds the show, episode and progress bar. Chrome or Edge.</span>
                  </div>
                )}
                {hasExtension && extPath && (
                  <div className="sub">
                    Copied to {extPath} (opened in Explorer). In Chrome or Edge open the extensions page (chrome://extensions or edge://extensions),
                    turn on Developer mode, press Load unpacked and pick that folder. If you already have it loaded, press its reload button on that page to get the newest version.
                  </div>
                )}
                {hasExtension && (
                <div className="sub faint">
                  {s.extensionLinked
                    ? "The RichPresence browser extension can report the exact show, episode and position here."
                    : "The browser extension link isn't open yet. RigControl keeps trying (it uses one of the ports 47610 to 47619 on this PC)."}
                </div>
                )}
              </div>
            )}
            {hasGames && (
            <>
            <Switch
              checked={s.settings.officialToDiscord}
              disabled={busy !== null}
              onChange={(v) => void save({ officialToDiscord: v })}
              label={`Let Discord show games it knows first, then show mine after ${s.settings.officialDelayMinutes} min (keeps Recent Activity and streaks)`}
            />
            <div className="field">
              <div className="field-label">
                <span>Genshin Impact UID</span>
                <span className="sub">Adds your name, Adventure Rank and World Level from your public enka.network profile.</span>
              </div>
              <div className="field-control" style={{ display: "flex", gap: 8 }}>
                <input className="input" style={{ width: 140 }} value={uid} inputMode="numeric" aria-label="Genshin UID" onChange={(e) => setUid(e.target.value)} />
                <button className="btn" disabled={busy !== null || uid.trim() === s.settings.genshinUid} onClick={() => void save({ genshinUid: uid.trim() })}>
                  Save
                </button>
              </div>
            </div>
            </>
            )}
          </section>

          <CustomStatusesPanel status={s} onStatus={setData} />

          {/* The in-game panel belongs to the Windows app; elsewhere it is not asked for. */}
          {hasPanel && <PanelSettings />}

          {hasGames && (
          <section className="panel" aria-label="Game library">
            <div className="panel-head">
              <div>
                <h2>Library</h2>
                <div className="sub">
                  {s.games.length} games from Steam, Epic, GOG, Riot, HoYoPlay, Xbox and your Games folders.
                  {s.scannedAt ? ` Last looked ${new Date(s.scannedAt).toLocaleString()}.` : ""} Games start without administrator rights.
                </div>
              </div>
              <button className="btn" disabled={busy !== null || s.scanning} onClick={() => void rescan()}>
                {s.scanning ? "Looking…" : "Look for games"}
              </button>
            </div>
            {s.games.length > 6 && <input className="input" style={{ maxWidth: 320 }} placeholder="Find a game" aria-label="Find a game" value={filter} onChange={(e) => setFilter(e.target.value)} />}
            {s.games.length === 0 && <div className="empty">{s.scanning ? "Looking for games…" : "No games found yet."}</div>}
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))", gap: 12 }}>
              {games.map((g) => (
                <GameTile key={g.id} game={g} onStatus={setData} />
              ))}
            </div>
          </section>
          )}
        </>
      )}
    </div>
  );
}

import { useState } from "react";
import { call } from "../bridge";
import { useAction, useLive, useQuery } from "../hooks";
import { Notice, Switch } from "./controls";
import { ModuleProblem } from "./ModuleProblem";
import type { LearnedSong, RadioStatus } from "../types";

/** In-game radio (module "radio"): songs recognised in the capture card's sound go to the Discord music card. */
export function RadioPanel() {
  const { data: status, setData, error } = useLive("radio.status", "radio.getStatus");
  const songs = useQuery<LearnedSong[]>("radio.listSongs", undefined, [status?.learnedCount]);
  const { busy, run } = useAction();
  const [title, setTitle] = useState("");
  const [artist, setArtist] = useState("");
  const [album, setAlbum] = useState("");
  const [editing, setEditing] = useState<LearnedSong | null>(null);

  const setEnabled = async (enabled: boolean) => {
    const r = await run("radio", () => call<RadioStatus>("radio.setSettings", { enabled }));
    if (r) setData(r);
  };

  const setLyrics = async (lyrics: boolean) => {
    const r = await run("lyrics", () => call<RadioStatus>("radio.setSettings", { lyrics }));
    if (r) setData(r);
  };

  const name = async () => {
    const r = await run(
      "name",
      () => call<RadioStatus>("radio.nameSong", { title, artist, album }),
      `Learned "${title.trim()}". RigControl will recognise it from now on.`,
    );
    if (r) {
      setData(r);
      setTitle("");
      setArtist("");
      setAlbum("");
      void songs.reload();
    }
  };

  const saveEdit = async () => {
    if (!editing) return;
    const r = await run("rename", () =>
      call<LearnedSong[]>("radio.renameSong", { id: editing.id, title: editing.title, artist: editing.artist, album: editing.album }),
    );
    if (r) {
      songs.setData(r);
      setEditing(null);
    }
  };

  const forget = async (song: LearnedSong) => {
    const r = await run("forget", () => call<LearnedSong[]>("radio.forgetSong", { id: song.id }), `Forgot "${song.title}".`);
    if (r) songs.setData(r);
  };

  const list = songs.data ?? [];
  const canName = title.trim() !== "" && artist.trim() !== "" && busy === null;

  return (
    <section className="panel" aria-label="In-game radio">
      <div className="panel-head">
        <div>
          <h2>In-game radio</h2>
          <div className="sub">
            Recognises songs in the capture card's sound (for example a game's radio) and shows them as your music on
            Discord. It listens on this PC only; nothing is recorded or sent anywhere.
          </div>
        </div>
      </div>
      <ModuleProblem name="radio" />
      {error && (
        <Notice tone="error" title="In-game radio is not available">
          {error}
        </Notice>
      )}
      {status && (
        <>
          <Switch
            checked={status.enabled}
            disabled={busy !== null}
            onChange={(v) => void setEnabled(v)}
            label="Recognise songs from the capture card"
          />
          {status.enabled && status.problem && (
            <Notice tone="warn" title="Waiting">
              {status.problem}
            </Notice>
          )}
          {status.enabled && (
            <>
              <Switch
                checked={status.lyrics.enabled}
                disabled={busy !== null}
                onChange={(v) => void setLyrics(v)}
                label="Also recognise the GTA 6 station songs by their lyrics"
              />
              <div className="sub">
                Works with no naming: RigControl listens for sung words and looks them up in the stations' lyrics. The
                first time, it downloads the songs' lyrics from lrclib.net and a speech recognition model (190 MB) from
                Hugging Face. The listening itself stays on this PC. Songs without words still need naming once.
              </div>
              {status.lyrics.enabled && status.lyrics.detail && (
                <Notice tone={status.lyrics.state === "problem" ? "error" : status.lyrics.state === "ready" ? "info" : "warn"}
                        title={status.lyrics.state === "ready" ? "Lyrics recognition is on" : status.lyrics.state === "problem" ? "Lyrics recognition stopped" : "Getting ready"}>
                  {status.lyrics.detail}
                </Notice>
              )}
              {status.lyrics.enabled && status.lyrics.state === "ready" && status.lyrics.lastHeard && (
                <p className="sub faint">Last heard: "{status.lyrics.lastHeard}"</p>
              )}
            </>
          )}
          {status.state === "listening" && (
            <p className="sub">
              {status.now
                ? `Now: ${status.now.title} - ${status.now.artist}`
                : status.sound
                  ? "Listening. Nothing recognised right now."
                  : "Listening. The capture card is quiet."}
            </p>
          )}
          {status.state === "listening" && status.sound && !status.now && (
            <div className="radio-name">
              <div className="sub">
                A song you know is playing? Name it once while it plays, and RigControl recognises it by itself from
                then on.
              </div>
              <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
                <input className="input" aria-label="Song title" placeholder="Title" maxLength={256} value={title} onChange={(e) => setTitle(e.target.value)} />
                <input className="input" aria-label="Artist" placeholder="Artist" maxLength={256} value={artist} onChange={(e) => setArtist(e.target.value)} />
                <input className="input" aria-label="Album (optional)" placeholder="Album (optional)" maxLength={256} value={album} onChange={(e) => setAlbum(e.target.value)} />
                <button className="btn primary" disabled={!canName} onClick={() => void name()}>
                  Name this song
                </button>
              </div>
            </div>
          )}
          <details className="radio-songs">
            <summary>Recognised songs ({list.length})</summary>
            {list.length === 0 && <div className="sub">No songs learned yet.</div>}
            <ul className="list">
              {list.map((song) =>
                editing?.id === song.id ? (
                  <li key={song.id} className="row" style={{ gap: 8, flexWrap: "wrap" }}>
                    <input className="input" aria-label="Song title" maxLength={256} value={editing.title} onChange={(e) => setEditing({ ...editing, title: e.target.value })} />
                    <input className="input" aria-label="Artist" maxLength={256} value={editing.artist} onChange={(e) => setEditing({ ...editing, artist: e.target.value })} />
                    <input className="input" aria-label="Album" maxLength={256} value={editing.album} onChange={(e) => setEditing({ ...editing, album: e.target.value })} />
                    <button className="btn small primary" disabled={busy !== null} onClick={() => void saveEdit()}>
                      Save
                    </button>
                    <button className="btn small" onClick={() => setEditing(null)}>
                      Cancel
                    </button>
                  </li>
                ) : (
                  <li key={song.id} className="row" style={{ gap: 8, alignItems: "center" }}>
                    <span style={{ flex: 1 }}>
                      {song.title} - {song.artist}
                      {song.album ? <span className="sub"> ({song.album})</span> : null}
                    </span>
                    <button className="btn small" disabled={busy !== null} onClick={() => setEditing(song)}>
                      Rename
                    </button>
                    <button className="btn small" disabled={busy !== null} onClick={() => void forget(song)}>
                      Forget
                    </button>
                  </li>
                ),
              )}
            </ul>
          </details>
        </>
      )}
    </section>
  );
}

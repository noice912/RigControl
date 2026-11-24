import { useEffect } from "react";
import { call } from "../bridge";
import { useAction, useLive } from "../hooks";
import { Slider, Switch } from "./controls";
import { LedLayoutPanel } from "./LedLayout";
import { EffectLibrary } from "./EffectLibrary";
import { NanoleafPair } from "./NanoleafPair";
import type { LightSync } from "../types";

/** Lights follow the screen or a capture card (RichPresence's light sync, as a Lighting mode). */
export function LightSyncPanel() {
  const { data: s, setData } = useLive("lighting.sync", "lighting.getSync");
  const { busy, run } = useAction();
  useEffect(() => {
    call<LightSync>("lighting.listSyncSources", undefined, 15000)
      .then(setData)
      .catch(() => {});
  }, [setData]);
  if (!s) return null;
  const save = async (patch: Record<string, unknown>, done?: string) => {
    const r = await run(
      "sync",
      () => call<LightSync>("lighting.setSync", patch, 30000),
      done,
    );
    if (r) setData(r);
  };
  return (
    <>
      <section className="panel" aria-label="Lighting effects">
        <div className="panel-head">
          <div>
            <h2>Lighting effects</h2>
            <div className="sub">
              Pick an effect and every light plays it together. Each effect
              paints a picture; with Per LED on, each light placed on the Layout
              below takes the part of the picture under it, so waves and ripples
              move across your setup. Turning it off puts your lights back as
              they were; changing a light by hand also stops it.
            </div>
          </div>
          <Switch
            checked={s.enabled}
            disabled={busy !== null}
            onChange={(v) => void save({ enabled: v })}
            label="On"
          />
        </div>
        <div className="row" style={{ gap: 10, alignItems: "center" }}>
          {s.enabled && (
            <span
              aria-hidden
              style={{
                width: 22,
                height: 22,
                borderRadius: 6,
                background: s.color ?? "#000",
                display: "inline-block",
                border: "1px solid rgba(255,255,255,0.2)",
              }}
            />
          )}
          <span className="sub">
            {s.status}
            {s.enabled && s.captureSize ? ` · ${s.captureSize}` : ""}
          </span>
        </div>
        <EffectLibrary sync={s} busy={busy !== null} save={save} />
        {s.effect === "screen" && (
          <>
            <div className="field">
              <div className="field-label">
                <span>Match my lights to</span>
                <span className="sub">
                  A capture card needs camera access for desktop apps on in
                  Windows' privacy settings. Exclusive full screen and some HDR
                  games read as black.
                </span>
              </div>
              <div className="field-control">
                <select
                  className="select"
                  value={s.source}
                  aria-label="Colour source"
                  disabled={busy !== null}
                  onChange={(e) => void save({ source: e.target.value })}
                >
                  <option value="">The screen</option>
                  {s.sources.map((d) => (
                    <option key={d.id} value={d.id} disabled={!d.present}>
                      {d.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <div className="field">
              <div className="field-label">
                <span>Vivid colours</span>
                <span className="sub">
                  The picture's colour at full strength, so every light shows it
                  clearly (a dim average looks different on each kind of light).
                </span>
              </div>
              <div className="field-control">
                <Switch
                  checked={s.vivid}
                  disabled={busy !== null}
                  onChange={(v) => void save({ vivid: v })}
                  label="On"
                />
              </div>
            </div>
          </>
        )}
        <div className="fx-group-name" style={{ marginTop: 14 }}>
          Sound on top of any effect
        </div>
        <div className="field">
          <div className="field-label">
            <span>Brightness follows the bass</span>
            <span className="sub">
              The lights pulse with the bass of whatever the PC is playing
              (games, music, videos), on top of the effect.
              {s.bassProblem ? ` Can't hear it: ${s.bassProblem}.` : ""}
            </span>
          </div>
          <div className="field-control">
            <Switch
              checked={s.bass}
              disabled={busy !== null}
              onChange={(v) => void save({ bass: v })}
              label="On"
            />
          </div>
        </div>
        {s.bass && (
          <div className="field">
            <div className="field-label">
              <span>Bass strength</span>
              <span className="sub">How dark the lights go between hits.</span>
            </div>
            <div className="field-control">
              <Slider
                label="Bass strength"
                min={10}
                max={100}
                value={s.bassStrength}
                disabled={busy !== null}
                format={(v) => `${v}%`}
                onCommit={(v) => void save({ bassStrength: v })}
              />
            </div>
          </div>
        )}
        <div className="field">
          <div className="field-label">
            <span>Mid range shifts the colour</span>
            <span className="sub">
              Vocals, guitars and synths turn the colour round the colour wheel
              (red toward orange and yellow, and so on).
            </span>
          </div>
          <div className="field-control">
            <Switch
              checked={s.mid}
              disabled={busy !== null}
              onChange={(v) => void save({ mid: v })}
              label="On"
            />
          </div>
        </div>
        {s.mid && (
          <div className="field">
            <div className="field-label">
              <span>Mid strength</span>
              <span className="sub">
                How far a full hit turns the colour (100% = a sixth of the
                wheel).
              </span>
            </div>
            <div className="field-control">
              <Slider
                label="Mid strength"
                min={10}
                max={100}
                value={s.midStrength}
                disabled={busy !== null}
                format={(v) => `${v}%`}
                onCommit={(v) => void save({ midStrength: v })}
              />
            </div>
          </div>
        )}
        <div className="field">
          <div className="field-label">
            <span>Treble flashes</span>
            <span className="sub">
              Hi-hats, cymbals and snares flash the lights toward white.
            </span>
          </div>
          <div className="field-control">
            <Switch
              checked={s.treble}
              disabled={busy !== null}
              onChange={(v) => void save({ treble: v })}
              label="On"
            />
          </div>
        </div>
        {s.treble && (
          <div className="field">
            <div className="field-label">
              <span>Treble strength</span>
              <span className="sub">How white a full hit makes them.</span>
            </div>
            <div className="field-control">
              <Slider
                label="Treble strength"
                min={10}
                max={100}
                value={s.trebleStrength}
                disabled={busy !== null}
                format={(v) => `${v}%`}
                onCommit={(v) => void save({ trebleStrength: v })}
              />
            </div>
          </div>
        )}
        {(s.bass || s.mid || s.treble) && (
          <div className="field">
            <div className="field-label">
              <span>Gain</span>
              <span className="sub">
                How strongly the lights react to the sound. Higher: softer hits
                count too. Lower: only the big hits.
              </span>
            </div>
            <div className="field-control">
              <Slider
                label="Audio gain"
                min={25}
                max={400}
                step={25}
                value={s.audioGain}
                disabled={busy !== null}
                format={(v) => `${v}%`}
                onCommit={(v) => void save({ audioGain: v })}
              />
            </div>
          </div>
        )}
        <div className="field">
          <div className="field-label">Effect brightness</div>
          <div className="field-control">
            <Slider
              label="Sync brightness"
              min={1}
              max={100}
              value={s.brightness}
              disabled={busy !== null}
              format={(v) => `${v}%`}
              onCommit={(v) => void save({ brightness: v })}
            />
          </div>
        </div>
      </section>
      <LedLayoutPanel syncOn={s.enabled} />
      <NanoleafPair />
    </>
  );
}

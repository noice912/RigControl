import { ExtraPanels, extraScenes } from "../extras";
import { LightSyncPanel } from "../components/LightSync";
import { GccTakeOver } from "../components/GccTakeOver";
import { useToast } from "../components/Toast";
import { useState } from "react";
import { call, errorMessage } from "../bridge";
import { useAction, useLive, useQuery } from "../hooks";
import { Notice, Slider, useConfirm } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import { HealthNotice } from "../components/HealthFindings";
import type { LightDevice, LightingStatus, Scene } from "../types";

/** The Health finding for each program this page already names in its own "also controlling lights" notice. */
const LIGHT_FINDINGS: Record<string, string> = {
  "Armoury Crate lighting": "lighting.armoury-crate",
  "Armoury Crate": "lighting.armoury-crate",
  "NZXT CAM": "lighting.nzxt-cam",
  "Razer Synapse": "lighting.razer-synapse",
  "Corsair iCUE": "lighting.icue",
  SignalRGB: "lighting.signalrgb",
};

const SWATCHES = [
  "#FFFFFF",
  "#FF2A2A",
  "#FF8A00",
  "#FFD400",
  "#3DFF6E",
  "#00E5FF",
  "#2F6BFF",
  "#9B4DFF",
  "#FF3DCB",
];

function normalise(hex: string): string {
  const h = hex.trim().replace(/^#/, "");
  if (/^[0-9a-f]{6}$/i.test(h)) return `#${h.toUpperCase()}`;
  if (/^[0-9a-f]{3}$/i.test(h))
    return `#${h
      .split("")
      .map((c) => c + c)
      .join("")
      .toUpperCase()}`;
  return "";
}

function ColorChooser(props: {
  value: string;
  onChange: (hex: string) => void;
}) {
  const [text, setText] = useState(props.value);
  return (
    <div className="stack">
      <label className="color-well" style={{ background: props.value }}>
        <input
          type="color"
          value={props.value.toLowerCase()}
          aria-label="Pick a colour"
          onChange={(e) => {
            const v = normalise(e.target.value);
            props.onChange(v);
            setText(v);
          }}
        />
        <span>{props.value}</span>
      </label>
      <div className="row">
        {SWATCHES.map((s) => (
          <button
            key={s}
            className="swatch"
            style={{ background: s }}
            aria-label={`Colour ${s}`}
            aria-pressed={props.value === s}
            onClick={() => {
              props.onChange(s);
              setText(s);
            }}
          />
        ))}
        <input
          className="input"
          style={{ width: 100, marginLeft: "auto" }}
          value={text}
          aria-label="Colour as hex"
          onChange={(e) => {
            setText(e.target.value);
            const v = normalise(e.target.value);
            if (v) props.onChange(v);
          }}
        />
      </div>
    </div>
  );
}

type LightZone = LightDevice["zones"][number];

/** Lian Li fan hubs count 16 lights for each fan, whatever the fan really has. */
const isLianLiHub = (name: string) => /lian li/i.test(name) && /hub/i.test(name);

/**
 * How many lights hang on a header. Only sent when the user presses Set: OpenRGB keeps the number, and a wrong one
 * leaves lights dark or lights ones that aren't there.
 */
function HeaderSize(props: {
  device: LightDevice;
  zone: LightZone;
  onStatus: (s: LightingStatus) => void;
}) {
  const { device: d, zone: z } = props;
  const [text, setText] = useState(String(z.ledCount));
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const toast = useToast();
  const leds = /^\d+$/.test(text.trim()) ? Number(text.trim()) : null;
  const hub = isLianLiHub(d.name);
  const max = Math.min(z.ledsMax, 2048);

  const set = async () => {
    if (leds === null) return;
    setSaving(true);
    setProblem(null);
    try {
      const r = await call<LightingStatus>(
        "lighting.setZoneSize",
        { deviceId: d.id, zone: z.id, leds },
        20000,
      );
      props.onStatus(r);
      toast.show(
        `${z.name}: ${leds} light${leds === 1 ? "" : "s"}`,
        "ok",
      );
    } catch (err) {
      setProblem(errorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="field">
      <div className="field-label">
        <span>Lights on this header</span>
        <span className="sub">
          Tell RigControl how many lights are plugged into this header: count
          the LEDs of the strip, or add up every fan on the chain. OpenRGB
          remembers the number.
          {hub
            ? " This hub counts 16 lights for each fan, so use a multiple of 16."
            : ""}
        </span>
        {z.ledCount === 0 && (
          <span className="sub header-empty">
            No lights set for this header yet.
          </span>
        )}
        {problem && (
          <span className="sub header-empty" role="alert">
            {problem}
          </span>
        )}
      </div>
      <div className="field-control header-size">
        <input
          className="input num"
          type="number"
          min={z.ledsMin}
          max={max}
          step={hub ? 16 : 1}
          value={text}
          aria-label={`Lights on ${z.name}`}
          disabled={saving}
          onChange={(e) => setText(e.target.value)}
        />
        <span className="sub">
          {z.ledsMin} to {max}
        </span>
        <button
          className="btn small"
          disabled={saving || leds === null || leds === z.ledCount}
          onClick={() => void set()}
        >
          {saving ? "Setting…" : "Set"}
        </button>
      </div>
    </div>
  );
}

function DeviceRow(props: {
  device: LightDevice;
  color: string;
  onStatus: (s: LightingStatus) => void;
}) {
  const d = props.device;
  const { busy, run } = useAction();
  const [open, setOpen] = useState(false);
  const mode = d.modes.find((m) => m.id === d.activeModeId) ?? null;
  const [speed, setSpeed] = useState<number | null>(null);
  const [brightness, setBrightness] = useState<number | null>(null);

  const setMode = async (
    modeId: number,
    extra: { speed?: number; brightness?: number } = {},
  ) => {
    const m = d.modes.find((x) => x.id === modeId);
    const colors =
      m && m.colorCount > 0
        ? Array.from({ length: m.colorCount }, () => props.color)
        : undefined;
    const r = await run("mode", () =>
      call<LightingStatus>("lighting.setMode", {
        deviceId: d.id,
        modeId,
        colors,
        ...extra,
      }),
    );
    if (r) props.onStatus(r);
  };

  return (
    <div className="device-row">
      <span
        className="swatch"
        style={{ background: d.color ?? "transparent", cursor: "default" }}
        aria-hidden="true"
      />
      <div>
        <div>{d.name}</div>
        <div className="sub">
          {d.type}
          {d.ledCount ? `, ${d.ledCount} LEDs` : ""}
          {d.leftOut
            ? ", run by GIGABYTE Control Center (RigControl leaves it alone)"
            : mode
              ? `, ${mode.name}`
              : ""}
          {!d.leftOut && d.zones.some((z) => z.resizable && z.ledCount === 0)
            ? ". A header has no lights set yet: press More to set how many."
            : ""}
        </div>
      </div>
      {!d.leftOut && (
        <div className="row">
          <button
            className="btn small"
            disabled={busy !== null}
            onClick={() =>
              void run("color", () =>
                call<LightingStatus>("lighting.setColor", {
                  deviceIds: [d.id],
                  color: props.color,
                }),
              ).then((r) => r && props.onStatus(r))
            }
          >
            Use this colour
          </button>
          <button
            className="btn small"
            aria-expanded={open}
            onClick={() => setOpen(!open)}
          >
            {open ? "Less" : "More"}
          </button>
        </div>
      )}
      {open && (
        <div className="device-detail">
          <div className="field">
            <div className="field-label">Effect</div>
            <div className="field-control">
              <select
                className="select"
                value={d.activeModeId}
                disabled={busy !== null}
                aria-label={`${d.name} effect`}
                onChange={(e) => void setMode(Number(e.target.value))}
              >
                {d.modes.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          {mode?.supportsSpeed &&
            mode.speedMin != null &&
            mode.speedMax != null && (
              <div className="field">
                <div className="field-label">Speed</div>
                <div className="field-control">
                  <Slider
                    label={`${d.name} speed`}
                    min={Math.min(mode.speedMin, mode.speedMax)}
                    max={Math.max(mode.speedMin, mode.speedMax)}
                    value={
                      speed ?? Math.round((mode.speedMin + mode.speedMax) / 2)
                    }
                    onCommit={(v) => {
                      setSpeed(v);
                      void setMode(mode.id, { speed: v });
                    }}
                  />
                </div>
              </div>
            )}
          {mode?.supportsBrightness &&
            mode.brightnessMin != null &&
            mode.brightnessMax != null && (
              <div className="field">
                <div className="field-label">Brightness</div>
                <div className="field-control">
                  <Slider
                    label={`${d.name} brightness`}
                    min={mode.brightnessMin}
                    max={mode.brightnessMax}
                    value={brightness ?? mode.brightnessMax}
                    onCommit={(v) => {
                      setBrightness(v);
                      void setMode(mode.id, { brightness: v });
                    }}
                  />
                </div>
              </div>
            )}
          {d.zones
            .filter((z) => d.zones.length > 1 || z.resizable)
            .map((z) => (
              <div key={z.id}>
                <div className="field">
                  <div className="field-label">
                    <span>{z.name}</span>
                    <span className="sub">
                      {z.resizable && z.ledCount === 0
                        ? "No lights set for this header yet"
                        : `${z.ledCount} LED${z.ledCount === 1 ? "" : "s"}`}
                    </span>
                  </div>
                  <div className="field-control">
                    {d.zones.length > 1 && (
                      <button
                        className="btn small"
                        disabled={busy !== null}
                        onClick={() =>
                          void run("zone", () =>
                            call<LightingStatus>("lighting.setZoneColor", {
                              deviceId: d.id,
                              zoneId: z.id,
                              color: props.color,
                            }),
                          ).then((r) => r && props.onStatus(r))
                        }
                      >
                        Use this colour here
                      </button>
                    )}
                  </div>
                </div>
                {z.resizable && (
                  <HeaderSize
                    key={z.ledCount}
                    device={d}
                    zone={z}
                    onStatus={props.onStatus}
                  />
                )}
              </div>
            ))}
        </div>
      )}
    </div>
  );
}

function Scenes(props: {
  onStatus: (s: LightingStatus) => void;
  disabled: boolean;
}) {
  const scenes = useQuery<Scene[]>("lighting.listScenes");
  const { busy, run } = useAction();
  const [name, setName] = useState("");
  const { confirm, element } = useConfirm();
  const toast = useToast();

  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          <h2>Scenes</h2>
          <div className="sub">
            Save how every device looks right now, and bring it back in one
            click.
          </div>
        </div>
      </div>
      <div className="row">
        <input
          className="input"
          style={{ flex: 1, minWidth: 200 }}
          placeholder="Name, e.g. Gaming"
          value={name}
          onChange={(e) => setName(e.target.value)}
          aria-label="Scene name"
        />
        <button
          className="btn primary"
          disabled={props.disabled || busy !== null || name.trim().length === 0}
          onClick={() =>
            void run(
              "save",
              () => call<Scene[]>("lighting.saveScene", { name: name.trim() }),
              `Saved "${name.trim()}"`,
            ).then((r) => {
              if (r) extraScenes.saved(name.trim());
              if (r) {
                scenes.setData(r);
                setName("");
              }
            })
          }
        >
          Save scene
        </button>
      </div>
      {(scenes.data ?? []).length === 0 ? (
        <div className="sub">No scenes saved yet.</div>
      ) : (
        (scenes.data ?? []).map((s) => (
          <div className="spread" key={s.name}>
            <span>
              {s.name}{" "}
              <span className="faint">
                saved {new Date(s.savedAt).toLocaleDateString()}
              </span>
            </span>
            <span className="row">
              <button
                className="btn small"
                disabled={props.disabled || busy !== null}
                onClick={() =>
                  void run(
                    "apply",
                    async () => {
                      const r = await call<LightingStatus>(
                        "lighting.applyScene",
                        { name: s.name },
                      );
                      // What an add-on saved with this scene, if anything. Its trouble must not spoil the PC lighting.
                      await extraScenes
                        .apply(s.name)
                        .catch((err) => toast.show(errorMessage(err), "error"));
                      return r;
                    },
                    `"${s.name}" applied`,
                  ).then((r) => r && props.onStatus(r))
                }
              >
                Apply
              </button>
              <button
                className="btn small ghost"
                disabled={busy !== null}
                onClick={async () => {
                  if (
                    await confirm({
                      title: `Delete "${s.name}"?`,
                      body: "The scene is removed. Your lights don't change.",
                      confirmLabel: "Delete scene",
                      danger: true,
                    })
                  ) {
                    const r = await run("del", () =>
                      call<Scene[]>("lighting.deleteScene", { name: s.name }),
                    );
                    if (r) scenes.setData(r);
                    if (r) extraScenes.deleted(s.name);
                  }
                }}
              >
                Delete
              </button>
            </span>
          </div>
        ))
      )}
      {element}
    </section>
  );
}

export function Lighting() {
  const {
    data: status,
    setData,
    error,
  } = useLive("lighting.changed", "lighting.getStatus");
  const { busy, run } = useAction();
  const [color, setColor] = useState("#00E5FF");

  const o = status?.openRgb;

  const install = async () => {
    const r = await run("install", () =>
      call<{ started: boolean; message: string }>("lighting.installOpenRgb"),
    );
    if (r) {
      await run(
        "status",
        () => call<LightingStatus>("lighting.getStatus").then(setData),
        r.message,
      );
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <h1>Lighting</h1>
        {o?.connected && (
          <span className="sub">
            {status!.devices.length} devices through OpenRGB
            {o.version ? ` ${o.version}` : ""}
          </span>
        )}
      </div>

      {(!o || o.connected) && <ModuleProblem name="lighting" />}
      {/* The notices below already name these programs (and GIGABYTE Control Center has its own take-over box). */}
      <HealthNotice
        modules={["lighting"]}
        exclude={[
          ...(status?.conflicts ?? []).map((c) => LIGHT_FINDINGS[c]).filter((id): id is string => !!id),
          ...(o?.connected ? ["lighting.gcc"] : []),
        ]}
      />
      {error && !status && (
        <Notice tone="error" title="Lighting is not available">
          {error}
        </Notice>
      )}

      {o && !o.installed && (
        <Notice
          title="Lighting needs OpenRGB"
          actions={
            <>
              <button
                className="btn primary"
                disabled={busy !== null}
                onClick={() => void install()}
              >
                Get OpenRGB
              </button>
              <button
                className="btn"
                disabled={busy !== null}
                onClick={() =>
                  void run("status", () =>
                    call<LightingStatus>("lighting.getStatus").then(setData),
                  )
                }
              >
                I've installed it
              </button>
            </>
          }
        >
          OpenRGB is the free engine RigControl uses to talk to lights: the
          motherboard and its light strips, the NZXT light controllers, the
          Razer keyboard and mouse, the graphics card, and (with one extra
          driver) the memory. Install it once from its own site; after that
          RigControl starts it hidden every time, so you never open it. The ring
          on the Kraken, the SSD's light and the monitor are not supported by
          it.
        </Notice>
      )}
      {o && o.installed && !o.connected && (
        <Notice
          tone={o.message ? "warn" : "info"}
          title={
            o.running ? "Connecting to OpenRGB…" : "OpenRGB is not running"
          }
          actions={
            <button
              className="btn primary"
              disabled={busy !== null}
              onClick={() =>
                void run("start", () =>
                  call<LightingStatus>(
                    "lighting.startOpenRgb",
                    undefined,
                    60000,
                  ).then(setData),
                )
              }
            >
              {busy === "start" ? "Starting…" : "Start OpenRGB"}
            </button>
          }
        >
          {o.message ??
            "RigControl normally starts it by itself, hidden. Press Start if it is not running."}
        </Notice>
      )}
      {o?.connected && (
        <GccTakeOver
          onStatus={setData}
          cardLeftOut={status?.devices.some((d) => d.leftOut) ?? false}
        />
      )}
      {status &&
        status.conflicts.filter((c) => c !== "GIGABYTE Control Center").length >
          0 && (
          <Notice
            tone="warn"
            title={`${status.conflicts.filter((c) => c !== "GIGABYTE Control Center").join(", ")} ${status.conflicts.filter((c) => c !== "GIGABYTE Control Center").length > 1 ? "are" : "is"} also controlling lights`}
          >
            They can overwrite the colours you set here. Turn off their lighting
            control, or close them.
          </Notice>
        )}

      {o?.connected && (
        <>
          <section className="panel">
            <div className="panel-head">
              <div>
                <h2>Everything at once</h2>
                <div className="sub">
                  Pick a colour, then send it to every device or just the ones
                  you choose below.
                </div>
              </div>
            </div>
            <div
              className="grid-2"
              style={{
                gridTemplateColumns: "minmax(260px, 1fr) minmax(220px, 0.8fr)",
              }}
            >
              <ColorChooser value={color} onChange={setColor} />
              <div className="stack" style={{ justifyContent: "flex-end" }}>
                <button
                  className="btn primary"
                  disabled={busy !== null || status!.devices.length === 0}
                  onClick={() =>
                    void run(
                      "all",
                      () =>
                        call<LightingStatus>("lighting.setColor", { color }),
                      "Colour sent to every device",
                    ).then((r) => r && setData(r))
                  }
                >
                  Apply to every device
                </button>
                <button
                  className="btn"
                  disabled={busy !== null || status!.devices.length === 0}
                  onClick={() =>
                    void run(
                      "off",
                      () => call<LightingStatus>("lighting.off", {}),
                      "Lights off",
                    ).then((r) => r && setData(r))
                  }
                >
                  Turn all lights off
                </button>
              </div>
            </div>
          </section>

        </>
      )}

      {/* Lights on the network need no OpenRGB, so the effects and the network lights show without it too. */}
      {o && <LightSyncPanel openRgb={o.connected} />}

      {o?.connected && (
        <>
          <section className="panel">
            <div className="panel-head">
              <div>
                <h2>Devices</h2>
                <div className="sub">
                  Everything in this list comes through OpenRGB. Only NZXT and
                  Razer devices have been tried on real hardware. Addressable
                  and plain RGB headers, Corsair Commander and Lighting Node,
                  Thermaltake Riing controllers and Lian Li Uni hubs are built
                  in but not tried on real hardware yet. If the maker's own
                  program is running (iCUE, TT RGB Plus, L-Connect, Synapse,
                  NZXT CAM), it may fight RigControl over the same device.
                  RigControl tells you when it sees one and never closes it.
                </div>
              </div>
            </div>
            {status!.devices.length === 0 ? (
              <div className="empty">
                OpenRGB did not find any devices. Some motherboard and RAM
                lighting only shows up when OpenRGB runs as administrator.
              </div>
            ) : (
              <div>
                {status!.devices.map((d) => (
                  <DeviceRow
                    key={d.id}
                    device={d}
                    color={color}
                    onStatus={setData}
                  />
                ))}
              </div>
            )}
          </section>
        </>
      )}

      <ExtraPanels slot="lighting" />
      {o?.installed && <Scenes disabled={!o.connected} onStatus={setData} />}
    </div>
  );
}

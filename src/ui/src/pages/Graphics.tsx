import { useEffect, useMemo, useState } from "react";
import { call, on } from "../bridge";
import { useAction, useQuery } from "../hooks";
import { useApp } from "../appContext";
import { Dialog, Notice, Slider, Switch } from "../components/controls";
import { ModuleProblem } from "../components/ModuleProblem";
import { HealthNotice } from "../components/HealthFindings";
import type { Display, DriverDownloadProgress, DriverProfile, DriverSetting, DriverUpdate, NvidiaInfo, NvidiaOverclock, NvidiaPower } from "../types";

// ---------------------------------------------------------------- driver & update

function DriverPanel({ info }: { info: NvidiaInfo }) {
  const { openUrl } = useApp();
  const { busy, run } = useAction();
  const [update, setUpdate] = useState<DriverUpdate | null>(null);
  const [progress, setProgress] = useState<DriverDownloadProgress | null>(null);
  const gpu = info.gpus[0];

  useEffect(() => on("nvidia.driverDownload", setProgress), []);

  const install = async () => {
    setProgress(null);
    await run(
      "install",
      () => call<{ started: boolean; version: string }>("nvidia.installDriver", undefined, 3600000),
      "NVIDIA's installer has started. Follow its steps to finish the update.",
    );
    setProgress(null);
  };

  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          <h2>{gpu?.name ?? "NVIDIA graphics"}</h2>
          <div className="sub">
            Driver {info.driverVersion ?? "unknown"}
            {gpu?.vramMB ? `, ${Math.round(gpu.vramMB / 1024)} GB video memory` : ""}
          </div>
          {(gpu?.family || gpu?.architecture || gpu?.chip) && (
            <div className="sub">{[gpu.family, gpu.architecture, gpu.chip ? `${gpu.chip} chip` : null].filter(Boolean).join(", ")}</div>
          )}
        </div>
        <button className="btn" disabled={busy !== null} onClick={() => void run("upd", () => call<DriverUpdate>("nvidia.checkDriverUpdate").then(setUpdate))}>
          {busy ? "Checking…" : "Check for a newer driver"}
        </button>
      </div>
      {update &&
        (update.error ? (
          <Notice
            tone="warn"
            title="Could not check for updates"
            actions={
              update.downloadUrl ? (
                <button className="btn" onClick={() => openUrl(update.downloadUrl!)}>
                  Open NVIDIA's driver page
                </button>
              ) : undefined
            }
          >
            {update.error}
          </Notice>
        ) : update.newer ? (
          <Notice
            title={`Driver ${update.latest} is available`}
            actions={
              update.canInstall ? (
                <button className="btn primary" disabled={busy !== null} onClick={() => void install()}>
                  {busy === "install"
                    ? progress
                      ? `Downloading… ${progress.percent ?? 0}%`
                      : "Starting…"
                    : `Download and install ${update.latest}`}
                </button>
              ) : update.downloadUrl ? (
                <button className="btn primary" onClick={() => openUrl(update.downloadUrl!)}>
                  Download driver {update.latest}
                </button>
              ) : undefined
            }
          >
            You have {update.installed}.{" "}
            {update.canInstall
              ? "RigControl downloads it from NVIDIA (about 700 MB), checks NVIDIA signed it, and starts NVIDIA's installer. The screen may flicker while it installs."
              : "The download opens in your browser; run it to update."}
          </Notice>
        ) : (
          <Notice title="Your driver is up to date">
            {update.installed} is the newest Game Ready driver for this card.
          </Notice>
        ))}
    </section>
  );
}

// ---------------------------------------------------------------- power

function PowerPanel() {
  const { info, restartAsAdmin } = useApp();
  const power = useQuery<NvidiaPower>("nvidia.getPower");
  const { run } = useAction();
  const p = power.data;
  if (power.error) return null;
  if (!p || p.minLimitW == null || p.maxLimitW == null || p.limitW == null) return null;

  const set = async (watts: number) => {
    const r = await run("power", () => call<NvidiaPower>("nvidia.setPowerLimit", { watts }), `Power limit set to ${watts} W`);
    if (r) power.setData(r);
  };

  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          <h2>Power limit</h2>
          <div className="sub">
            Drawing {p.powerW != null ? `${Math.round(p.powerW)} W` : "–"} now. Lower limits run cooler and quieter; the default is{" "}
            {p.defaultLimitW != null ? `${Math.round(p.defaultLimitW)} W` : "set by the card"}.
          </div>
        </div>
        {p.defaultLimitW != null && Math.round(p.limitW) !== Math.round(p.defaultLimitW) && (
          <button className="btn" disabled={!p.canSet} onClick={() => void set(Math.round(p.defaultLimitW!))}>
            Back to default
          </button>
        )}
      </div>
      {!p.canSet && (
        <Notice
          tone="warn"
          title="Changing the power limit needs administrator rights"
          actions={
            !info?.isElevated ? (
              <button className="btn" onClick={() => void restartAsAdmin()}>
                Restart as administrator
              </button>
            ) : undefined
          }
        >
          {p.reason ?? "The driver did not allow a change."}
        </Notice>
      )}
      <Slider
        label="Power limit"
        min={Math.round(p.minLimitW)}
        max={Math.round(p.maxLimitW)}
        value={Math.round(p.limitW)}
        disabled={!p.canSet}
        format={(v) => `${v} W`}
        onCommit={(v) => void set(v)}
      />
      <div className="sub faint">The driver resets this to the default when Windows restarts.</div>
    </section>
  );
}

// ---------------------------------------------------------------- overclock

const mhz = (v: number) => `${v > 0 ? "+" : ""}${v} MHz`;

function OverclockPanel() {
  const { info, restartAsAdmin } = useApp();
  const oc = useQuery<NvidiaOverclock>("nvidia.getOverclock");
  const { busy, run } = useAction();
  const o = oc.data;
  const [core, setCore] = useState(0);
  const [mem, setMem] = useState(0);
  useEffect(() => {
    if (o) {
      setCore(o.coreMHz ?? 0);
      setMem(o.memoryMHz ?? 0);
    }
  }, [o?.coreMHz, o?.memoryMHz]);
  if (oc.error || !o) return null;
  if (!o.available) return null;

  const apply = async (patch: Record<string, unknown>, done: string) => {
    const r = await run("oc", () => call<NvidiaOverclock>("nvidia.setOverclock", patch), done);
    if (r) oc.setData(r);
  };
  const reset = async () => {
    const r = await run("oc", () => call<NvidiaOverclock>("nvidia.resetOverclock"), "Clocks back to stock");
    if (r) oc.setData(r);
  };
  const changed = core !== (o.coreMHz ?? 0) || mem !== (o.memoryMHz ?? 0);
  const atStock = (o.coreMHz ?? 0) === 0 && (o.memoryMHz ?? 0) === 0;
  const off = !o.canSet || busy !== null;

  return (
    <section className="panel" aria-label="Overclocking">
      <div className="panel-head">
        <div>
          <h2>Overclocking</h2>
          <div className="sub">
            Clock offsets on top of the card's own boost. Raise them in small steps (core +15 to +30 MHz, memory +100 to +200 MHz) and play for a while
            after each; a game crash, a driver reset or flickering means go back down. Voltage is never changed.
          </div>
        </div>
        {!atStock && (
          <button className="btn" disabled={off} onClick={() => void reset()}>
            Back to stock
          </button>
        )}
      </div>
      {o.notice && (
        <Notice tone="warn" title="Offsets were not applied at start">
          {o.notice}
        </Notice>
      )}
      {!o.canSet && (
        <Notice
          tone="warn"
          title="Overclocking needs administrator rights"
          actions={
            !info?.isElevated ? (
              <button className="btn" onClick={() => void restartAsAdmin()}>
                Restart as administrator
              </button>
            ) : undefined
          }
        >
          {o.reason ?? "The driver did not allow a change."}
        </Notice>
      )}
      <div className="field">
        <div className="field-label">
          <span>Core clock offset</span>
          <span className="sub">Now {mhz(o.coreMHz ?? 0)}</span>
        </div>
        <div className="field-control">
          <Slider label="Core clock offset" min={o.coreMin} max={o.coreMax} step={5} value={core} disabled={off} format={mhz} onChange={setCore} onCommit={setCore} />
        </div>
      </div>
      {o.memoryMHz != null && (
        <div className="field">
          <div className="field-label">
            <span>Memory clock offset</span>
            <span className="sub">Now {mhz(o.memoryMHz)}</span>
          </div>
          <div className="field-control">
            <Slider label="Memory clock offset" min={o.memoryMin} max={o.memoryMax} step={25} value={mem} disabled={off} format={mhz} onChange={setMem} onCommit={setMem} />
          </div>
        </div>
      )}
      <div className="row" style={{ gap: 8 }}>
        <button
          className="btn primary"
          disabled={off || !changed}
          onClick={() => void apply({ coreMHz: core, memoryMHz: o.memoryMHz != null ? mem : undefined }, `Core ${mhz(core)}, memory ${mhz(mem)}`)}
        >
          Apply
        </button>
        {changed && (
          <button
            className="btn"
            disabled={busy !== null}
            onClick={() => {
              setCore(o.coreMHz ?? 0);
              setMem(o.memoryMHz ?? 0);
            }}
          >
            Undo
          </button>
        )}
      </div>
      <Switch
        checked={o.applyAtStart}
        disabled={off}
        onChange={(v) => void apply({ applyAtStart: v }, v ? "Offsets will be applied when RigControl starts" : "Offsets will not be applied at start")}
        label="Apply these offsets when RigControl starts"
      />
      <div className="sub faint">
        If the PC crashes within 3 minutes of RigControl applying them at start, they are not applied the next time. Offsets set by other apps (such as NVIDIA
        App's automatic tuning) show here too.
      </div>
    </section>
  );
}

// ---------------------------------------------------------------- driver settings

function SettingControl(props: { setting: DriverSetting; onSet: (value: number) => void; disabled: boolean }) {
  const s = props.setting;
  if (s.kind === "number") {
    return (
      <span className="row">
        <input
          className="input num"
          type="number"
          min={s.min ?? undefined}
          max={s.max ?? undefined}
          defaultValue={s.value}
          key={s.value}
          disabled={props.disabled}
          aria-label={s.name}
          onBlur={(e) => {
            const v = Number(e.target.value);
            if (Number.isFinite(v) && v !== s.value) props.onSet(v);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") (e.target as HTMLInputElement).blur();
          }}
        />
        {s.unit && <span className="sub">{s.unit}</span>}
      </span>
    );
  }
  const options = s.options ?? [];
  const known = options.some((o) => o.value === s.value);
  return (
    <select className="select" value={s.value} disabled={props.disabled} aria-label={s.name} onChange={(e) => props.onSet(Number(e.target.value))}>
      {!known && <option value={s.value}>Custom ({s.value})</option>}
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

function ProfilePicker(props: { profile: DriverProfile | null; onPick: (p: DriverProfile | null) => void }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<DriverProfile[]>([]);
  const { busy, run } = useAction();

  useEffect(() => {
    if (query.trim().length < 2) {
      setResults([]);
      return;
    }
    const t = window.setTimeout(() => {
      call<DriverProfile[]>("nvidia.listProfiles", { query: query.trim() })
        .then(setResults)
        .catch(() => setResults([]));
    }, 250);
    return () => window.clearTimeout(t);
  }, [query]);

  const addProgram = async () => {
    const picked = await call<{ path: string | null }>("app.pickFile", { title: "Choose the game or program", filter: "Programs (*.exe)|*.exe" }).catch(
      () => ({ path: null }),
    );
    if (!picked.path) return;
    const p = await run("add", () => call<DriverProfile>("nvidia.addProgram", { exePath: picked.path }));
    if (p) {
      props.onPick(p);
      setQuery("");
    }
  };

  return (
    <div className="stack">
      <div className="row">
        <button className="btn" aria-pressed={props.profile === null} onClick={() => props.onPick(null)} disabled={props.profile === null}>
          All programs
        </button>
        <input
          className="input"
          style={{ flex: 1, minWidth: 220 }}
          placeholder="Find a game or program by name"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label="Find a program profile"
        />
        <button className="btn" disabled={busy !== null} onClick={() => void addProgram()}>
          Add a program…
        </button>
      </div>
      {results.length > 0 && (
        <div className="inset" role="listbox" aria-label="Matching profiles" style={{ maxHeight: 220, overflow: "auto", padding: 6 }}>
          {results.map((r) => (
            <button
              key={r.name}
              role="option"
              aria-selected={props.profile?.name === r.name}
              className="nav-item"
              style={{ width: "100%" }}
              onClick={() => {
                props.onPick(r);
                setQuery("");
              }}
            >
              <span>{r.name}</span>
              <span className="faint" style={{ marginLeft: "auto", fontSize: 12 }}>
                {r.modifiedCount > 0 ? `${r.modifiedCount} changed` : r.programs.slice(0, 2).join(", ")}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function DriverSettingsPanel() {
  const [profile, setProfile] = useState<DriverProfile | null>(null);
  const params = profile ? { profile: profile.name } : {};
  const settings = useQuery<DriverSetting[]>("nvidia.getDriverSettings", params, [profile?.name]);
  const { busy, run } = useAction();

  const groups = useMemo(() => {
    const m = new Map<string, DriverSetting[]>();
    for (const s of settings.data ?? []) {
      const list = m.get(s.group) ?? [];
      list.push(s);
      m.set(s.group, list);
    }
    return [...m.entries()];
  }, [settings.data]);

  const replace = (s: DriverSetting) => settings.setData((cur) => (cur ?? []).map((x) => (x.key === s.key ? s : x)));
  const set = async (key: string, value: number) => {
    const r = await run(key, () => call<DriverSetting>("nvidia.setDriverSetting", { key, value, ...params }));
    if (r) replace(r);
  };
  const reset = async (key: string) => {
    const r = await run(key, () => call<DriverSetting>("nvidia.resetDriverSetting", { key, ...params }));
    if (r) replace(r);
  };

  const changed = (settings.data ?? []).filter((s) => !s.isDefault).length;

  // One-click setups: several driver settings at once, for all programs or just the chosen one.
  const QUICK: { id: string; label: string; hint: string; values: Record<string, number> }[] = [
    {
      id: "fast",
      label: "Fastest response",
      hint: "Low latency on, full power, no vertical sync, highest refresh rate.",
      values: { lowLatency: 1, powerMode: 1, vsync: 0x08416747, preferredRefresh: 1, textureQuality: 0x14 },
    },
    {
      id: "pretty",
      label: "Best picture",
      hint: "Sharpest textures and the newest DLSS model in games that have DLSS.",
      values: { textureQuality: 0xfffffff6, dlssOverride: 1, dlssPreset: 0x00ffffff },
    },
  ];
  const applyQuick = async (q: (typeof QUICK)[number]) => {
    const known = new Set((settings.data ?? []).map((s) => s.key));
    await run("quick", async () => {
      for (const [key, value] of Object.entries(q.values)) {
        if (!known.has(key)) continue;
        replace(await call<DriverSetting>("nvidia.setDriverSetting", { key, value, ...params }));
      }
    }, `${q.label} applied${profile ? ` to ${profile.name}` : ""}`);
  };
  const resetAll = async () => {
    await run("quick", async () => {
      for (const s of (settings.data ?? []).filter((x) => !x.isDefault))
        replace(await call<DriverSetting>("nvidia.resetDriverSetting", { key: s.key, ...params }));
    }, "Back to defaults");
  };

  return (
    <section className="panel">
      <div className="panel-head">
        <div>
          <h2>3D settings</h2>
          <div className="sub">
            {profile
              ? `Only for ${profile.name}${profile.programs.length ? ` (${profile.programs.slice(0, 3).join(", ")})` : ""}. Anything left at default follows All programs.`
              : "These apply to every game unless a program has its own setting."}
            {changed > 0 ? ` ${changed} changed from default.` : ""}
          </div>
        </div>
      </div>
      <ProfilePicker profile={profile} onPick={setProfile} />
      {settings.data && (
        <div className="field">
          <div className="field-label">
            <span>Quick setup{profile ? ` for ${profile.name}` : ""}</span>
            <span className="sub">{QUICK.map((q) => `${q.label}: ${q.hint}`).join(" ")}</span>
          </div>
          <div className="field-control" style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {QUICK.map((q) => (
              <button key={q.id} className="btn" disabled={busy !== null} onClick={() => void applyQuick(q)}>
                {q.label}
              </button>
            ))}
            <button className="btn" disabled={busy !== null || changed === 0} onClick={() => void resetAll()}>
              Back to defaults
            </button>
          </div>
        </div>
      )}
      {settings.error ? (
        <Notice tone="error" title="Driver settings could not be read">
          {settings.error}
        </Notice>
      ) : !settings.data ? (
        <div className="empty">Reading driver settings…</div>
      ) : (
        groups.map(([group, list]) => (
          <div key={group} className="stack" style={{ gap: 0 }}>
            <h3 style={{ marginTop: 8 }}>{group}</h3>
            {list.map((s) => (
              <div className="field" key={s.key}>
                <div className="field-label">
                  <span>
                    {s.name}
                    {!s.isDefault && <span className="changed-mark" title="Changed from default" />}
                  </span>
                  <span className="sub">{s.description}</span>
                </div>
                <div className="field-control">
                  {!s.isDefault && (
                    <button className="btn ghost small" disabled={busy !== null} onClick={() => void reset(s.key)}>
                      Use default
                    </button>
                  )}
                  <SettingControl setting={s} disabled={busy !== null} onSet={(v) => void set(s.key, v)} />
                </div>
              </div>
            ))}
          </div>
        ))
      )}
    </section>
  );
}

// ---------------------------------------------------------------- displays

function DisplayCard(props: { display: Display; onChanged: () => void }) {
  const d = props.display;
  const { run, busy } = useAction();
  const resolutions = useMemo(() => {
    const seen = new Map<string, { width: number; height: number }>();
    for (const m of d.modes) seen.set(`${m.width}x${m.height}`, { width: m.width, height: m.height });
    return [...seen.values()].sort((a, b) => b.width * b.height - a.width * a.height);
  }, [d.modes]);
  const [res, setRes] = useState(`${d.width}x${d.height}`);
  const [hz, setHz] = useState(d.refreshHz);
  const [pending, setPending] = useState<null | { token: string; seconds: number }>(null);

  useEffect(() => {
    setRes(`${d.width}x${d.height}`);
    setHz(d.refreshHz);
  }, [d.width, d.height, d.refreshHz]);

  const rates = useMemo(() => {
    const [w, h] = res.split("x").map(Number);
    return [...new Set(d.modes.filter((m) => m.width === w && m.height === h).map((m) => m.refreshHz))].sort((a, b) => b - a);
  }, [res, d.modes]);

  useEffect(() => {
    if (rates.length && !rates.includes(hz)) setHz(rates[0]!);
  }, [rates, hz]);

  useEffect(() => {
    if (!pending) return;
    if (pending.seconds <= 0) {
      setPending(null);
      props.onChanged();
      return;
    }
    const t = window.setTimeout(() => setPending((p) => (p ? { ...p, seconds: p.seconds - 1 } : p)), 1000);
    return () => window.clearTimeout(t);
  }, [pending, props]);

  const changed = res !== `${d.width}x${d.height}` || hz !== d.refreshHz;

  const apply = async () => {
    const [width, height] = res.split("x").map(Number);
    const r = await run("mode", () =>
      call<{ applied: boolean; revertToken: string; revertInSeconds: number }>("nvidia.setDisplayMode", { displayId: d.id, width, height, refreshHz: hz }),
    );
    if (r) setPending({ token: r.revertToken, seconds: r.revertInSeconds });
  };

  const finish = async (keep: boolean) => {
    if (!pending) return;
    const token = pending.token;
    setPending(null);
    await run("confirm", () => call(keep ? "nvidia.confirmDisplayMode" : "nvidia.revertDisplayMode", { revertToken: token }), keep ? "Display mode kept" : "Display mode reverted");
    props.onChanged();
  };

  return (
    <div className="inset stack">
      <div className="spread">
        <h3>
          {d.name}
          {d.primary && <span className="faint" style={{ fontWeight: 400 }}> (main display)</span>}
        </h3>
        <span className="sub">
          {d.width} × {d.height} at {d.refreshHz} Hz
        </span>
      </div>
      <div className="field">
        <div className="field-label">Resolution and refresh rate</div>
        <div className="field-control">
          <select className="select" value={res} onChange={(e) => setRes(e.target.value)} aria-label="Resolution">
            {resolutions.map((r) => (
              <option key={`${r.width}x${r.height}`} value={`${r.width}x${r.height}`}>
                {r.width} × {r.height}
              </option>
            ))}
          </select>
          <select className="select" style={{ minWidth: 110 }} value={hz} onChange={(e) => setHz(Number(e.target.value))} aria-label="Refresh rate">
            {rates.map((r) => (
              <option key={r} value={r}>
                {r} Hz
              </option>
            ))}
          </select>
          <button className="btn primary" disabled={!changed || busy !== null} onClick={() => void apply()}>
            Apply
          </button>
        </div>
      </div>
      {d.vibrance && (
        <div className="field">
          <div className="field-label">
            <span>Digital vibrance</span>
            <span className="sub">Makes colours more saturated. Default is {d.vibrance.defaultValue}.</span>
          </div>
          <div className="field-control">
            <Slider
              label="Digital vibrance"
              min={d.vibrance.min}
              max={d.vibrance.max}
              value={d.vibrance.value}
              onCommit={(v) => void run("vib", () => call<Display>("nvidia.setVibrance", { displayId: d.id, value: v }))}
            />
          </div>
        </div>
      )}
      {pending && (
        <Dialog
          title="Keep this display mode?"
          onClose={() => void finish(false)}
          actions={
            <>
              <button className="btn" onClick={() => void finish(false)}>
                Go back
              </button>
              <button className="btn primary" onClick={() => void finish(true)}>
                Keep it
              </button>
            </>
          }
        >
          <div className="stack">
            <span>If the screen looks wrong or stays black, do nothing: the previous mode comes back automatically.</span>
            <span className="countdown" aria-live="polite">
              {pending.seconds}
            </span>
          </div>
        </Dialog>
      )}
    </div>
  );
}

function DisplaysPanel() {
  const displays = useQuery<Display[]>("nvidia.getDisplays");
  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Displays</h2>
      </div>
      {displays.error ? (
        <Notice tone="error" title="Displays could not be read">
          {displays.error}
        </Notice>
      ) : !displays.data ? (
        <div className="empty">Reading displays…</div>
      ) : (
        displays.data.map((d) => <DisplayCard key={d.id} display={d} onChanged={() => void displays.reload()} />)
      )}
    </section>
  );
}

// ---------------------------------------------------------------- page

export function Graphics() {
  const info = useQuery<NvidiaInfo>("nvidia.getInfo");
  return (
    <div className="page">
      <div className="page-head">
        <h1>Graphics</h1>
      </div>
      <ModuleProblem name="nvidia" />
      <HealthNotice modules={["nvidia"]} />
      {info.error && !info.data ? (
        <Notice tone="error" title="NVIDIA graphics is not available">
          {info.error}
        </Notice>
      ) : !info.data ? (
        <div className="empty">Reading graphics card…</div>
      ) : !info.data.available ? (
        <Notice tone="error" title="No NVIDIA graphics card was found">
          {info.data.limitations[0] ?? "Check that the NVIDIA driver is installed."}
        </Notice>
      ) : (
        <>
          <DriverPanel info={info.data} />
          <PowerPanel />
          <OverclockPanel />
          <DriverSettingsPanel />
          <DisplaysPanel />
          {info.data.limitations.length > 0 && (
            <Notice title="Not in RigControl">
              <ul style={{ margin: "4px 0 0", paddingLeft: 18 }}>
                {info.data.limitations.map((l) => (
                  <li key={l}>{l}</li>
                ))}
              </ul>
            </Notice>
          )}
        </>
      )}
    </div>
  );
}

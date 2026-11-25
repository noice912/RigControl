import { useEffect, useRef, useState } from "react";
import { call, errorMessage } from "../bridge";
import { useQuery } from "../hooks";
import { Slider, Switch, useConfirm } from "./controls";
import { useToast } from "./Toast";
import type {
  FoundLight,
  LightAbility,
  NetworkBrand,
  NetworkLight,
  NetworkLights,
} from "../types";

const UNTESTED = "Not tried on real hardware yet";

/** "9 panels", "60 LEDs", "one colour". */
function howMany(l: NetworkLight): string {
  if (l.kind === "single") return "one colour";
  if (l.points <= 0) return "not read yet";
  return `${l.points} ${l.pointName}${l.points === 1 ? "" : "s"}`;
}

function TestedBadge({ tested }: { tested: boolean }) {
  return tested ? (
    <span className="badge ok">Tried on real hardware</span>
  ) : (
    <span className="badge warn">{UNTESTED}</span>
  );
}

/** The plain reason a control can't be used, or undefined when it can. */
function whyNot(a: LightAbility | undefined): string | undefined {
  if (!a) return "This version of RigControl can't control this kind of light.";
  return a.can ? undefined : (a.why ?? "This light can't do that from here.");
}

/** "Right now: On, 60% bright, colour #FF8800." from what the light last said; null when it said nothing. */
function nowShowing(l: NetworkLight): string | null {
  const parts: string[] = [];
  if (l.on !== null) parts.push(l.on ? "on" : "off");
  if (l.on !== false && l.brightness !== null) parts.push(`${l.brightness}% bright`);
  if (l.on !== false && l.color) parts.push(`colour ${l.color}`);
  return parts.length > 0 ? `Right now: ${parts.join(", ")}.` : null;
}

/**
 * A paired light's own controls: on/off, brightness, one colour, and Give back. A control the brand can't do is
 * disabled, with its reason shown under it. The slider sends when it is let go, the colour once it settles.
 */
function LightControlsRow(props: {
  light: NetworkLight;
  disabled: boolean;
  onData: (r: NetworkLights) => void;
}) {
  const l = props.light;
  const c = l.controls;
  const toast = useToast();
  const [sending, setSending] = useState(false);
  const [colour, setColour] = useState(l.color ?? "#FFFFFF");
  const timer = useRef<number | null>(null);
  useEffect(() => {
    if (l.color) setColour(l.color);
  }, [l.color]);
  useEffect(
    () => () => {
      if (timer.current !== null) window.clearTimeout(timer.current);
    },
    [],
  );

  const run = async (method: string, args: object, done: string) => {
    setSending(true);
    try {
      props.onData(
        await call<NetworkLights>(method, { id: l.id, ...args }, 30000),
      );
      toast.show(done, "ok");
    } catch (err) {
      toast.show(errorMessage(err), "error");
    } finally {
      setSending(false);
    }
  };
  const send = (
    change: { on?: boolean; brightness?: number; color?: string },
    done: string,
  ) => run("lighting.setNetworkLight", change, done);

  const off = props.disabled || sending;
  const reasons = (
    [
      ["Switch", whyNot(c?.power)],
      ["Brightness", whyNot(c?.brightness)],
      ["Colour", whyNot(c?.colour)],
      ["What it shows now", whyNot(c?.readBack)],
      ["Give back", whyNot(c?.giveBack)],
    ] as [string, string | undefined][]
  ).filter((r): r is [string, string] => r[1] !== undefined);
  const showing = nowShowing(l);

  return (
    <div className="netlight-controls" aria-label={`Controls for ${l.name}`}>
      <div
        className="row"
        style={{ gap: 16, flexWrap: "wrap", alignItems: "center" }}
      >
        <Switch
          checked={l.on ?? false}
          disabled={off || !c?.power.can}
          label={l.on === null ? "On or off" : l.on ? "On" : "Off"}
          onChange={(v) =>
            void send(
              { on: v },
              v ? `${l.name} switched on` : `${l.name} switched off`,
            )
          }
        />
        <div style={{ minWidth: 180, flex: "1 1 180px" }}>
          <Slider
            label={`Brightness of ${l.name}`}
            min={1}
            max={100}
            value={l.brightness ?? 100}
            format={(v) => `${v}%`}
            disabled={off || !c?.brightness.can}
            onCommit={(v) =>
              void send({ brightness: v }, `${l.name} set to ${v}%`)
            }
          />
        </div>
        <label className="color-well" style={{ background: colour }}>
          <input
            type="color"
            value={colour.toLowerCase()}
            aria-label={`Colour of ${l.name}`}
            disabled={off || !c?.colour.can}
            onChange={(e) => {
              const v = e.target.value.toUpperCase();
              setColour(v);
              if (timer.current !== null) window.clearTimeout(timer.current);
              timer.current = window.setTimeout(() => {
                timer.current = null;
                void send({ color: v }, `${l.name} set to ${v}`);
              }, 500);
            }}
          />
          <span>{colour}</span>
        </label>
        <button
          className="btn small"
          disabled={off || !c?.giveBack.can || !l.takenOver}
          title={
            c?.giveBack.can && !l.takenOver
              ? "RigControl hasn't changed this light, so there is nothing to give back."
              : undefined
          }
          onClick={() =>
            void run(
              "lighting.giveBackNetworkLight",
              {},
              `${l.name} given back`,
            )
          }
        >
          {sending ? "Sending…" : "Give back"}
        </button>
      </div>
      {showing && <div className="sub">{showing}</div>}
      {l.takenOver && c?.giveBack.can && (
        <div className="sub">
          RigControl has taken this light over. Give back puts it back to what
          it was doing before.
        </div>
      )}
      {reasons.length > 0 && (
        <ul className="netlight-list sub">
          {reasons.map(([what, why]) => (
            <li key={what}>
              <strong>{what}:</strong> {why}
            </li>
          ))}
        </ul>
      )}
      {!l.joinsEverything && (
        <div className="sub">
          Everything at once and Turn all lights off leave this one alone,
          because it can run the lights of a whole house. Use its own controls
          here.
        </div>
      )}
    </div>
  );
}

/**
 * Wi-Fi lights driven straight over the home network (no cloud account). Nothing is searched for until the user
 * presses "Search for lights"; pairing shows the brand's steps first.
 */
export function NetworkLightsPanel(props: {
  /** Told whenever the set of paired lights (or what they are made of) changes, so the layout can load again. */
  onChanged?: (signature: string) => void;
}) {
  const q = useQuery<NetworkLights>("lighting.getNetworkLights");
  const toast = useToast();
  const { confirm, element } = useConfirm();
  const [searching, setSearching] = useState(false);
  /** Brands a search has finished for since the page opened ("" = every brand). */
  const [searched, setSearched] = useState<Set<string>>(new Set());
  /** True when the last search was stopped before every brand was done. */
  const [stopped, setStopped] = useState(false);
  const [searchProblem, setSearchProblem] = useState<string | null>(null);
  const [pairing, setPairing] = useState<string | null>(null);
  const [pairProblem, setPairProblem] = useState<string | null>(null);
  const [forgetting, setForgetting] = useState<string | null>(null);
  const [brandId, setBrandId] = useState("");
  const [address, setAddress] = useState("");
  const [chosen, setChosen] = useState<Record<string, Record<string, string>>>(
    {},
  );
  const working = searching || pairing !== null || forgetting !== null;
  const workingRef = useRef(working);
  workingRef.current = working;

  const n = q.data;
  const setData = q.setData;

  // Problems and "Playing now" change by themselves: ask again every few seconds. This only reads; it never searches.
  useEffect(() => {
    const t = window.setInterval(() => {
      if (workingRef.current) return;
      call<NetworkLights>("lighting.getNetworkLights")
        .then((r) => {
          if (!workingRef.current) setData(r);
        })
        .catch(() => {});
    }, 4000);
    return () => window.clearInterval(t);
  }, [setData]);

  const signature = n
    ? n.paired.map((l) => `${l.id}:${l.kind}:${l.points}`).join(",")
    : null;
  const onChanged = props.onChanged;
  useEffect(() => {
    if (signature !== null) onChanged?.(signature);
  }, [signature, onChanged]);

  if (!n) return null;

  const brand: NetworkBrand | undefined = n.brands.find(
    (b) => b.brand === brandId,
  );
  const isSearching = searching || n.scanning;
  const busy = working || n.scanning;

  const search = async (only?: string) => {
    setSearching(true);
    setSearchProblem(null);
    try {
      const r = await call<NetworkLights>(
        "lighting.scanNetworkLights",
        only ? { brand: only } : {},
        35000,
      );
      setData(r);
      setStopped(r.scanStopped);
      // A stopped search only counts for the brands that were done; it never counts as "every brand searched".
      setSearched((s) => {
        const next = new Set(s);
        if (r.scanStopped) for (const b of r.scanFinished) next.add(b);
        else next.add(only ?? "");
        return next;
      });
    } catch (err) {
      setSearchProblem(errorMessage(err));
    } finally {
      setSearching(false);
    }
  };
  const stop = () =>
    call<NetworkLights>("lighting.cancelNetworkScan")
      .then(setData)
      .catch((err) => toast.show(errorMessage(err), "error"));

  const optionsFor = (b: NetworkBrand) => {
    const out: Record<string, string> = {};
    for (const o of b.options)
      out[o.key] =
        chosen[b.brand]?.[o.key] ?? o.default ?? o.choices[0]?.value ?? "";
    return out;
  };

  const pair = async (b: NetworkBrand, to: string) => {
    setPairing(to);
    setPairProblem(null);
    try {
      const r = await call<NetworkLights>(
        "lighting.pairNetworkLight",
        { brand: b.brand, address: to, options: optionsFor(b) },
        50000,
      );
      setData(r);
      setAddress("");
      toast.show(`${b.name} paired`, "ok");
    } catch (err) {
      setPairProblem(errorMessage(err));
    } finally {
      setPairing(null);
    }
  };

  const forget = async (l: NetworkLight) => {
    const yes = await confirm({
      title: `Forget ${l.name}?`,
      body: "RigControl stops sending colours to it and puts it back as it was. Its place on the layout is kept, so pairing it again puts it back where it was.",
      confirmLabel: "Forget",
    });
    if (!yes) return;
    setForgetting(l.id);
    try {
      setData(
        await call<NetworkLights>(
          "lighting.forgetNetworkLight",
          { id: l.id },
          15000,
        ),
      );
    } catch (err) {
      toast.show(errorMessage(err), "error");
    } finally {
      setForgetting(null);
    }
  };

  const setUp = (f: FoundLight) => {
    setBrandId(f.brand);
    setPairProblem(null);
  };

  const searchedAll = searched.has("");
  const nothing = n.brands.filter(
    (b) =>
      b.canSearch &&
      (searchedAll || searched.has(b.brand)) &&
      !n.found.some((f) => f.brand === b.brand),
  );
  const foundHere = brand
    ? n.found.filter((f) => f.brand === brand.brand && !f.paired)
    : [];
  const brandSearched = brand
    ? searchedAll || searched.has(brand.brand)
    : false;

  return (
    <section className="panel" aria-label="Lights on your network">
      <div className="panel-head">
        <div>
          <h2>Lights on your network</h2>
          <div className="sub">
            Wi-Fi lights join the effects here. RigControl talks to each light
            directly over your own network. It never signs in to a cloud account
            for lights, and it only looks for lights when you press Search for
            lights.
          </div>
          <div className="sub">
            Switch each light, dim it or pick its colour below. An effect drives
            a light only while it plays, and changing a light by hand stops the
            effect. To give each panel or LED its own part of an effect, turn
            Per LED on and place the light on the Layout. Scenes only hold the
            PC's own lights.
          </div>
        </div>
        {isSearching ? (
          <button className="btn small" onClick={() => void stop()}>
            Stop
          </button>
        ) : (
          <button
            className="btn small"
            disabled={busy}
            onClick={() => void search()}
          >
            Search for lights
          </button>
        )}
      </div>

      {n.paired.length === 0 ? (
        <div className="sub">
          No lights paired yet. Search for lights, or pick a brand under Add a
          light.
        </div>
      ) : (
        <div className="netlights">
          {n.paired.map((l) => (
            <div className="netlight" key={l.id}>
              <div className="netlight-main">
                <div>
                  <strong>{l.name}</strong>
                  <TestedBadge tested={l.tested} />
                  {l.streaming && <span className="badge ok">Playing now</span>}
                </div>
                <div className="sub">
                  {l.brandName}
                  {l.model ? ` ${l.model}` : ""} at {l.address} · {howMany(l)}
                </div>
                {l.problem && (
                  <div className="netlight-problem" role="status">
                    {l.problem}
                  </div>
                )}
                {l.note && <div className="sub">{l.note}</div>}
                {!l.placed && (
                  <div className="sub">
                    {!l.followsUnplaced
                      ? `Not on the layout yet. Effects leave it alone until you place it: turn Per LED on, open the Layout and drag it onto the picture. Then each ${l.pointName} takes its own part of the effect.`
                      : `Not on the layout yet. While an effect plays, it shows the one overall colour. Turn Per LED on and place it on the Layout to give each ${l.pointName} its own part of the picture.`}
                  </div>
                )}
                <LightControlsRow light={l} disabled={busy} onData={setData} />
              </div>
              <button
                className="btn small ghost"
                disabled={busy}
                title="Its place on the layout is kept"
                onClick={() => void forget(l)}
              >
                {forgetting === l.id ? "Forgetting…" : "Forget"}
              </button>
            </div>
          ))}
        </div>
      )}

      {(isSearching ||
        searchProblem ||
        stopped ||
        searched.size > 0 ||
        n.found.length > 0 ||
        n.scanProblems.length > 0) && (
        <div className="netlight-block" aria-live="polite">
          <div className="fx-group-name">Search</div>
          {isSearching && (
            <div className="sub">
              Looking for lights on your network… This can take up to half a
              minute.
            </div>
          )}
          {searchProblem && (
            <div className="netlight-problem" role="alert">
              {searchProblem}
            </div>
          )}
          {n.found.map((f) => (
            <div className="netlight" key={`${f.brand}|${f.deviceId}`}>
              <div className="netlight-main">
                <div>
                  <strong>{f.name || f.brandName}</strong>
                  {f.paired && <span className="badge">Already paired</span>}
                </div>
                <div className="sub">
                  {f.brandName}
                  {f.model ? ` ${f.model}` : ""} at {f.address}
                </div>
              </div>
              {!f.paired && (
                <button
                  className="btn small"
                  disabled={busy}
                  onClick={() => setUp(f)}
                >
                  {brandId === f.brand ? "Steps are below" : "Set up"}
                </button>
              )}
            </div>
          ))}
          {!isSearching && stopped && (
            <div className="sub">
              Search stopped.
              {n.found.length > 0
                ? " These lights had answered by then."
                : " No light had answered yet."}{" "}
              Press Search for lights to look again.
            </div>
          )}
          {!isSearching &&
            !stopped &&
            searched.size > 0 &&
            n.found.length === 0 && (
              <div className="sub">No light answered.</div>
            )}
          {!isSearching && !stopped && searched.size > 0 && (
            <div className="sub">
              Missing a light? Check it is switched on and on the same network
              as this PC, and that a VPN isn't hiding your home network. Windows
              Firewall blocks most answers when the network is set to Public:
              the Health page says if yours is. Govee and Yeelight lights also
              need LAN Control switched on in their own app.
            </div>
          )}
          {n.scanProblems.map((p, i) => (
            <div className="sub" key={i}>
              {p}
            </div>
          ))}
          {!isSearching && nothing.some((b) => b.nothingFound) && (
            <details className="netlight-details">
              <summary>
                Brands that found nothing, and what to check (
                {nothing.filter((b) => b.nothingFound).length})
              </summary>
              <ul className="netlight-list">
                {nothing
                  .filter((b) => b.nothingFound)
                  .map((b) => (
                    <li key={b.brand}>
                      <strong>{b.name}:</strong> {b.nothingFound}
                    </li>
                  ))}
              </ul>
            </details>
          )}
        </div>
      )}

      <div className="netlight-block">
        <div className="fx-group-name">Add a light</div>
        <div className="field">
          <div className="field-label">
            <span>Brand</span>
            <span className="sub">
              Pick the brand to see how to pair it. Brands marked "not tried"
              were built from the maker's published instructions and have not
              been tried on a real light yet.
            </span>
          </div>
          <div className="field-control">
            <select
              className="select"
              value={brandId}
              aria-label="Brand of light"
              disabled={pairing !== null}
              onChange={(e) => {
                setBrandId(e.target.value);
                setPairProblem(null);
                setAddress("");
              }}
            >
              <option value="">Choose a brand…</option>
              {n.brands.map((b) => (
                <option key={b.brand} value={b.brand}>
                  {b.name}
                  {b.tested ? "" : " (not tried on real hardware yet)"}
                </option>
              ))}
            </select>
          </div>
        </div>

        {brand && (
          <div className="netlight-add">
            <div>
              <strong>{brand.name}</strong>
              <TestedBadge tested={brand.tested} />
            </div>
            {brand.notes && <div className="sub">{brand.notes}</div>}
            {brand.steps.length > 0 && (
              <ol className="netlight-list">
                {brand.steps.map((s, i) => (
                  <li key={i}>{s}</li>
                ))}
              </ol>
            )}
            {brand.returnsSecret && (
              <div className="sub">
                This light gives RigControl a key when you pair. The key is kept
                protected on this PC for your Windows account and is only ever
                sent to that light.
              </div>
            )}
            {brand.options.map((o) => (
              <div className="field" key={o.key}>
                <div className="field-label">{o.label}</div>
                <div className="field-control">
                  <select
                    className="select"
                    aria-label={o.label}
                    disabled={busy}
                    value={optionsFor(brand)[o.key]}
                    onChange={(e) =>
                      setChosen((c) => ({
                        ...c,
                        [brand.brand]: {
                          ...c[brand.brand],
                          [o.key]: e.target.value,
                        },
                      }))
                    }
                  >
                    {o.choices.map((c) => (
                      <option key={c.value} value={c.value}>
                        {c.label}
                      </option>
                    ))}
                  </select>
                </div>
              </div>
            ))}

            {foundHere.map((f) => (
              <div className="field" key={f.deviceId}>
                <div className="field-label">
                  <span>{f.name || brand.name}</span>
                  <span className="sub">
                    {f.model ? `${f.model} at ` : "At "}
                    {f.address}
                  </span>
                </div>
                <div className="field-control">
                  <button
                    className="btn small primary"
                    disabled={busy}
                    onClick={() => void pair(brand, f.address)}
                  >
                    {pairing === f.address ? "Pairing…" : "Pair"}
                  </button>
                </div>
              </div>
            ))}
            {!brand.canSearch && brand.nothingFound && (
              <div className="sub">{brand.nothingFound}</div>
            )}
            {brand.canSearch && foundHere.length === 0 && (
              <div className="field">
                <div className="field-label">
                  <span>Find it</span>
                  <span className="sub">
                    {brandSearched && !isSearching
                      ? (brand.nothingFound ??
                        `No ${brand.name} light answered.`)
                      : stopped && !isSearching
                        ? `The search was stopped before ${brand.name} was done.`
                        : `Looks for ${brand.name} lights on your network.`}
                  </span>
                </div>
                <div className="field-control">
                  <button
                    className="btn small"
                    disabled={busy}
                    onClick={() => void search(brand.brand)}
                  >
                    {isSearching
                      ? "Searching…"
                      : brandSearched
                        ? "Search again"
                        : `Search for ${brand.name}`}
                  </button>
                </div>
              </div>
            )}
            {brand.manualAddress && (
              <div className="field">
                <div className="field-label">
                  <span>Or type the light's address</span>
                  <span className="sub">
                    The numbers your router or the light's own app shows for it,
                    four groups with dots between them. It must be on your home
                    network.
                  </span>
                </div>
                <div className="field-control">
                  <input
                    className="input"
                    style={{ width: 170 }}
                    value={address}
                    inputMode="decimal"
                    placeholder="Address"
                    aria-label={`${brand.name} address`}
                    disabled={busy}
                    onChange={(e) => setAddress(e.target.value)}
                  />
                  <button
                    className="btn small primary"
                    disabled={busy || address.trim().length === 0}
                    onClick={() => void pair(brand, address.trim())}
                  >
                    {pairing !== null && pairing === address.trim()
                      ? "Pairing…"
                      : "Pair"}
                  </button>
                </div>
              </div>
            )}
            {pairing !== null && (
              <div className="sub" role="status">
                Pairing with the light at {pairing}… This can take up to 45
                seconds. If the steps above ask you to press a button on the
                light, do it now.
              </div>
            )}
            {pairProblem && (
              <div className="netlight-problem" role="alert">
                {pairProblem}
              </div>
            )}
          </div>
        )}
      </div>

      {n.notSupported.length > 0 && (
        <details className="netlight-details">
          <summary>What isn't supported, and why</summary>
          <ul className="netlight-list">
            {n.notSupported.map((g) => (
              <li key={g.brand + g.name}>
                <strong>{g.name}.</strong> {g.why}
              </li>
            ))}
          </ul>
        </details>
      )}
      {element}
    </section>
  );
}

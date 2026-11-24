import { useEffect, useRef, useState } from "react";
import { call, errorMessage } from "../bridge";
import { useQuery } from "../hooks";
import { useConfirm } from "./controls";
import { useToast } from "./Toast";
import type {
  FoundLight,
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
                    {n.brands.find((b) => b.brand === l.brand)
                      ?.followsUnplaced === false
                      ? `Not on the layout yet. RigControl leaves it alone until you place it: turn Per LED on and place it on the Layout, and each ${l.pointName} takes its own part of the picture.`
                      : "Not on the layout yet. Turn Per LED on and place it on the Layout to give it its own part of the picture; until then it follows the one overall colour."}
                  </div>
                )}
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

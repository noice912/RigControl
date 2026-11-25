import { useState } from "react";
import { call, errorMessage } from "../bridge";
import type { LicenceText } from "../types";

/**
 * The terms of use, RigControl's licence and the notices for everything it carries, read from the app itself.
 * Read-only. The texts are only fetched once the user opens the section (they are a few hundred kilobytes).
 */
export function Licences() {
  const [texts, setTexts] = useState<LicenceText[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [shown, setShown] = useState<string | null>(null);

  const open = async () => {
    setLoading(true);
    try {
      const list = await call<LicenceText[]>("app.getLicences");
      setTexts(list);
      setShown(list[0]?.name ?? null);
      setError(null);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  const current = texts?.find((t) => t.name === shown) ?? null;

  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Licences and terms</h2>
        {texts ? (
          <button className="btn small" onClick={() => setTexts(null)}>
            Hide
          </button>
        ) : (
          <button
            className="btn small"
            disabled={loading}
            onClick={() => void open()}
          >
            {loading ? "Opening…" : "Show"}
          </button>
        )}
      </div>
      <div className="sub" style={{ marginBottom: 10 }}>
        RigControl is free software under the GNU General Public License
        version 3. These are its terms of use, its licence and the licences of
        the programs and libraries it carries.
      </div>
      {error && (
        <div className="sub" role="alert">
          {error}
        </div>
      )}
      {texts && (
        <>
          <div
            className="row"
            style={{ flexWrap: "wrap", gap: 6, marginBottom: 10 }}
            role="tablist"
            aria-label="Licence texts"
          >
            {texts.map((t) => (
              <button
                key={t.name}
                role="tab"
                aria-selected={t.name === shown}
                className={"btn small" + (t.name === shown ? " primary" : "")}
                onClick={() => setShown(t.name)}
              >
                {t.name}
              </button>
            ))}
          </div>
          {current && (
            <pre
              className="log"
              tabIndex={0}
              aria-label={current.name}
              style={{ whiteSpace: "pre-wrap", maxHeight: 420, overflow: "auto" }}
            >
              {current.text}
            </pre>
          )}
        </>
      )}
    </section>
  );
}

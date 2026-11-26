import { useEffect, useState } from "react";
import { call, errorMessage, on } from "../bridge";
import { useAction } from "../hooks";
import { useApp } from "../appContext";
import { useGitHub } from "../components/GitHubAccount";
import { Notice } from "../components/controls";
import type { DevStatus, ReviewItem } from "../types";

/**
 * The Developer page: only in the menu for a GitHub account that may push to RigControl, and only usable after
 * Windows Hello confirms it is you (asked every time this page opens, and again before publishing).
 */
export function Developer() {
  const [status, setStatus] = useState<DevStatus | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [unlocking, setUnlocking] = useState(false);

  const unlock = async () => {
    setUnlocking(true);
    setProblem(null);
    try {
      setStatus(await call<DevStatus>("dev.unlock", undefined, 180000));
    } catch (err) {
      setProblem(errorMessage(err));
    } finally {
      setUnlocking(false);
    }
  };

  useEffect(() => {
    void unlock();
    return on("dev.status", setStatus);
  }, []);

  return (
    <div className="page">
      <div className="page-head">
        <h1>Developer</h1>
        {status?.unlocked && status.locksAt && (
          <span className="sub">Locks after 15 minutes without use</span>
        )}
      </div>
      {!status?.unlocked ? (
        <section className="panel">
          <Notice
            tone={problem ? "warn" : "info"}
            title={unlocking ? "Waiting for Windows Hello…" : "Dev mode is locked"}
            actions={
              !unlocking && (
                <button className="btn primary" onClick={() => void unlock()}>
                  Unlock with Windows Hello
                </button>
              )
            }
          >
            {problem ?? "Windows Hello checks it is you before anything here can be used."}
          </Notice>
        </section>
      ) : (
        <>
          <PublishPanel defaultKeyPath={status.defaultKeyPath} />
          <ReviewsPanel />
        </>
      )}
    </div>
  );
}

function PublishPanel({ defaultKeyPath }: { defaultKeyPath: string }) {
  const { openUrl } = useApp();
  const { data: gh } = useGitHub();
  const { busy, run } = useAction();
  const [exePath, setExePath] = useState("");
  const [version, setVersion] = useState("");
  const [notes, setNotes] = useState("");
  const [keyPath, setKeyPath] = useState(defaultKeyPath);
  const [testBuild, setTestBuild] = useState(false);
  const [step, setStep] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  useEffect(() => on("dev.publish", (d) => setStep(d.step)), []);

  const pick = async (filter: string, set: (p: string) => void) => {
    const r = await run("pick", () => call<{ path: string | null }>("app.pickFile", { filter }, 600000));
    if (r?.path) set(r.path);
  };

  const publish = async () => {
    const r = await run("publish", () => call<{ url: string }>("dev.publishUpdate", { exePath, version: version.trim(), notes, keyPath, testBuild }, 60 * 60000), "Published.");
    if (r) setDone(r.url);
  };

  // Three numbers, and for a development build a pre-release part ("1.1.3-rc.1"); the app checks the same again.
  const m = /^v?\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.exec(version.trim());
  const versionProblem = !version.trim()
    ? null
    : !m
      ? "The version must look like 1.2.3, or 1.2.3-rc.1 for a development build."
      : testBuild && !m[1]
        ? "A development build needs a pre-release version, like 1.2.3-rc.1."
        : !testBuild && m[1]
          ? "A version with a pre-release part (-rc.1) can only be published as a development build. Tick the box below or drop the part."
          : null;
  const ready = exePath && m && !versionProblem && keyPath;

  return (
    <section className="panel">
      <h2>Publish an update</h2>
      <div className="sub" style={{ marginBottom: 10 }}>
        Signs the program, makes the release v{version.trim().replace(/^v/, "") || "x.y.z"} on GitHub as a draft, uploads the
        setup file and its signed update information, then publishes it
        {testBuild ? " as a pre-release (only people with development builds on get it)" : " as the latest public build"}. GitHub decides
        whether {gh?.login ?? "the account"} may do this.
      </div>
      <div className="stack">
        <label className="stack">
          <span>Program (RigControl.exe)</span>
          <div className="row">
            <input className="input" style={{ flex: 1 }} value={exePath} onChange={(e) => setExePath(e.target.value)} />
            <button className="btn small" onClick={() => void pick("Programs (*.exe)|*.exe", setExePath)}>
              Choose…
            </button>
          </div>
        </label>
        <label className="stack">
          <span>Version</span>
          <input
            className="input"
            placeholder={testBuild ? "1.1.3-rc.1" : "1.1.2"}
            value={version}
            onChange={(e) => setVersion(e.target.value)}
          />
          {versionProblem && <span className="sub" style={{ color: "var(--hot)" }}>{versionProblem}</span>}
        </label>
        <label className="row" style={{ gap: 10, alignItems: "flex-start" }}>
          <input type="checkbox" checked={testBuild} onChange={(e) => setTestBuild(e.target.checked)} />
          <span>
            Development build (pre-release: only people with development builds on get it)
            <span className="sub">
              {" "}
              The release is marked as a pre-release and its notes say it is a development build. The public build never
              offers it.
            </span>
          </span>
        </label>
        <label className="stack">
          <span>Release notes</span>
          <textarea className="input" rows={5} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
        <label className="stack">
          <span>Signing key file</span>
          <div className="row">
            <input className="input" style={{ flex: 1 }} value={keyPath} onChange={(e) => setKeyPath(e.target.value)} />
            <button className="btn small" onClick={() => void pick("Key files (*.pem)|*.pem|All files (*.*)|*.*", setKeyPath)}>
              Choose…
            </button>
          </div>
        </label>
        <div className="row">
          <button className="btn primary" disabled={!ready || busy !== null} onClick={() => void publish()}>
            {busy === "publish" ? (step ? `${step}…` : "Working…") : "Publish"}
          </button>
          {done && (
            <button className="btn small" onClick={() => openUrl(done)}>
              Open the release
            </button>
          )}
        </div>
        <div className="sub">Windows Hello asks for you again before anything is signed or sent.</div>
      </div>
    </section>
  );
}

function ReviewsPanel() {
  const { openUrl } = useApp();
  const { busy, run } = useAction();
  const [reviews, setReviews] = useState<ReviewItem[] | null>(null);

  const load = async () => {
    const r = await run("load", () => call<ReviewItem[]>("dev.listReviews", undefined, 60000));
    if (r) setReviews(r);
  };

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <section className="panel">
      <div className="panel-head">
        <h2>Reviews</h2>
        <button className="btn small" disabled={busy !== null} onClick={() => void load()}>
          Refresh
        </button>
      </div>
      {reviews === null ? (
        <div className="empty">{busy ? "Reading reviews…" : "No reviews read yet."}</div>
      ) : reviews.length === 0 ? (
        <div className="empty">No reviews yet.</div>
      ) : (
        <div className="stack">
          {reviews.map((r) => (
            <div className="field" key={r.number}>
              <div className="field-label">
                <span>
                  <span style={{ color: "var(--warm)" }}>{"★".repeat(r.stars)}{"☆".repeat(5 - r.stars)}</span>{" "}
                  {r.author ?? "someone"} · RigControl {r.version}
                  {r.createdAt ? ` · ${new Date(r.createdAt).toLocaleDateString()}` : ""}
                  {r.state === "closed" ? " · closed" : ""}
                </span>
                <span className="sub" style={{ whiteSpace: "pre-wrap" }}>{r.text}</span>
              </div>
              <div className="field-control">
                <button className="btn small" onClick={() => openUrl(r.url)}>
                  #{r.number}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

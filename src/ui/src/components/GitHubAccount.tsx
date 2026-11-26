import { useState } from "react";
import { call } from "../bridge";
import { useAction, useLive } from "../hooks";
import { useApp } from "../appContext";
import type { GitHubStatus } from "../types";
import { Dialog, Notice } from "./controls";
import { useRunningVersion } from "./VersionLabel";

/** The GitHub account (sign-in state, never a token), kept up to date by the "github.status" event. */
export function useGitHub() {
  return useLive("github.status", "github.getStatus");
}

/** Sign in, the code to type on github.com while waiting, and sign out. Used by Settings and the review form. */
export function GitHubSignInBox({ status, setStatus }: { status: GitHubStatus | null; setStatus: (s: GitHubStatus) => void }) {
  const { openUrl } = useApp();
  const { busy, run } = useAction();
  const s = status;
  if (!s) return null;
  if (!s.configured) return <div className="sub">{s.notSetUp}</div>;

  if (s.login)
    return (
      <div className="spread">
        <span>
          Signed in as <strong>{s.login}</strong>
        </span>
        <button
          className="btn small"
          disabled={busy !== null}
          onClick={async () => {
            const r = await run("out", () => call<GitHubStatus>("github.signOut"));
            if (r) setStatus(r);
          }}
        >
          Sign out
        </button>
      </div>
    );

  if (s.signingIn && s.userCode)
    return (
      <div className="stack">
        <div>
          On the GitHub page that opened in your browser, enter this code:{" "}
          <strong style={{ fontSize: 20, letterSpacing: 2, userSelect: "all" }}>{s.userCode}</strong>
        </div>
        <div className="row">
          <button className="btn small" onClick={() => s.verificationUri && openUrl(s.verificationUri)}>
            Open GitHub again
          </button>
          <button className="btn small" onClick={() => void navigator.clipboard?.writeText(s.userCode ?? "").catch(() => {})}>
            Copy code
          </button>
          <button
            className="btn small"
            onClick={async () => {
              const r = await run("cancel", () => call<GitHubStatus>("github.cancelSignIn"));
              if (r) setStatus(r);
            }}
          >
            Cancel
          </button>
        </div>
        <div className="sub">RigControl waits here until GitHub says yes. Nothing on this PC listens for it.</div>
      </div>
    );

  return (
    <div className="stack">
      {s.problem && <Notice tone="warn" title="Sign-in did not finish">{s.problem}</Notice>}
      <div className="row">
        <button
          className="btn primary"
          disabled={busy !== null}
          onClick={async () => {
            const r = await run("in", () => call<GitHubStatus>("github.startSignIn", undefined, 60000));
            if (r) setStatus(r);
          }}
        >
          Sign in with GitHub
        </button>
      </div>
    </div>
  );
}

/** Settings > GitHub account. */
export function GitHubAccountPanel() {
  const { data, setData } = useGitHub();
  return (
    <section className="panel">
      <h2>GitHub account</h2>
      <div className="sub" style={{ marginBottom: 10 }}>
        Used to post reviews. RigControl keeps the sign-in protected for your Windows account and only talks to GitHub.
      </div>
      <GitHubSignInBox status={data} setStatus={setData} />
    </section>
  );
}

/** Settings > Write a review: stars and words, posted as a public GitHub issue under the user's GitHub name. */
export function ReviewPanel() {
  const { openUrl } = useApp();
  const running = useRunningVersion();
  const { data: gh, setData } = useGitHub();
  const { busy, run } = useAction();
  const [open, setOpen] = useState(false);
  const [stars, setStars] = useState(0);
  const [text, setText] = useState("");
  const [posted, setPosted] = useState<string | null>(null);

  const send = async () => {
    const r = await run("send", () => call<{ url: string }>("github.submitReview", { stars, text }, 60000), "Thanks! Your review is on GitHub.");
    if (r) {
      setPosted(r.url);
      setOpen(false);
      setText("");
      setStars(0);
    }
  };

  return (
    <section className="panel">
      <div className="spread">
        <div>
          <h2>Write a review</h2>
          <div className="sub">Tell others what you think of RigControl. Reviews are public on GitHub.</div>
        </div>
        <button className="btn" onClick={() => setOpen(true)}>
          Write a review
        </button>
      </div>
      {posted && (
        <div className="sub">
          Posted.{" "}
          <button className="btn small" onClick={() => openUrl(posted)}>
            See it on GitHub
          </button>
        </div>
      )}
      {open && (
        <Dialog
          title="Write a review"
          onClose={() => setOpen(false)}
          actions={
            <>
              <button className="btn" onClick={() => setOpen(false)}>
                Cancel
              </button>
              <button
                className="btn primary"
                disabled={!gh?.login || stars < 1 || text.trim().length === 0 || busy !== null}
                onClick={() => void send()}
              >
                {busy === "send" ? "Posting…" : "Post review"}
              </button>
            </>
          }
        >
          <div className="stack">
            <div role="radiogroup" aria-label="Stars" className="row">
              {[1, 2, 3, 4, 5].map((n) => (
                <button
                  key={n}
                  role="radio"
                  aria-checked={stars === n}
                  aria-label={`${n} star${n === 1 ? "" : "s"}`}
                  className="btn small"
                  style={{ fontSize: 20, color: n <= stars ? "var(--warm)" : "var(--faint)" }}
                  onClick={() => setStars(n)}
                >
                  {n <= stars ? "★" : "☆"}
                </button>
              ))}
            </div>
            <textarea
              className="input"
              rows={5}
              maxLength={4000}
              style={{ width: "100%", resize: "vertical" }}
              placeholder="What works well, what could be better?"
              value={text}
              onChange={(e) => setText(e.target.value)}
              aria-label="Your review"
            />
            <div className="sub">
              Your review will be public on RigControl's GitHub page under your GitHub name, with the RigControl
              version ({running ?? "this one"}). Nothing about your PC is added.
            </div>
            {!gh?.login && <GitHubSignInBox status={gh} setStatus={setData} />}
          </div>
        </Dialog>
      )}
    </section>
  );
}

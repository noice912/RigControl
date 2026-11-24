import { call } from "../bridge";
import { useApp } from "../appContext";
import { useAction, useQuery } from "../hooks";
import { openFirstRun } from "../nav";
import { useConfirm } from "./controls";
import { HelperRow, tidyHelpers } from "./FirstRun";
import type { BundledInfo, FirstRunInfo } from "../types";

/** The helper programs RigControl needs (carried inside its exe), and the old apps it replaces (with their own uninstallers). */
export function BuiltInApps() {
  const q = useQuery<BundledInfo>("app.getBundled");
  const { restartAsAdmin } = useApp();
  const { busy, run } = useAction();
  const { confirm, element } = useConfirm();
  const d = q.data;
  if (!d) return null;
  const s = d.status;
  const h = d.helpers ? tidyHelpers(d.helpers) : null;

  // One button for everything that is missing. Before the user's yes it opens the setup step that lists the
  // programs and their licences; after it, it simply tries again (as administrator).
  const installMissing = async () => {
    if (!h) return;
    if (h.needsYes) {
      openFirstRun();
      return;
    }
    if (!d.elevated) {
      await restartAsAdmin();
      return;
    }
    await run("install", () =>
      call<FirstRunInfo>("app.installHelpers", undefined, 300000),
    );
    void q.reload();
  };

  const uninstall = async (id: string, name: string) => {
    const ok = await confirm({
      title: `Uninstall ${name}?`,
      body: `${name}'s own uninstaller opens and takes you through it. RigControl keeps doing its job, so you won't need it again.`,
      confirmLabel: "Uninstall",
      danger: true,
    });
    if (!ok) return;
    await run(
      "uninstall-" + id,
      () => call("app.uninstallOldApp", { id }, 20000),
      `${name}'s uninstaller has opened`,
    );
    window.setTimeout(() => void q.reload(), 4000);
  };

  return (
    <section className="panel">
      {element}
      <h2>Built in</h2>
      <div className="sub" style={{ marginBottom: 10 }}>
        {h && !h.items.some((i) => i.carried)
          ? "RigControl needs these open-source programs. This build does not carry them, so it uses the ones that are already installed on this PC."
          : h && h.items.some((i) => !i.carried)
            ? "RigControl needs these open-source programs. This build carries some of them inside itself; those are installed together after you said yes on the setup screen. What is already on this PC stays as it is."
            : "RigControl needs these open-source programs and carries them inside itself, so nothing has to be downloaded. They are installed together after you said yes on the setup screen; what is already on this PC stays as it is."}
      </div>
      {h ? (
        <>
          {h.items.map((item) => (
            <HelperRow key={item.id} helper={item}>
              {item.id === "openrgb" && s?.openRgbPath && (
                <span className="sub faint">
                  RigControl's own copy: {s.openRgbPath}
                  {s.openRgbServiceUsesIt
                    ? ". The OpenRGB service runs this copy."
                    : ""}
                </span>
              )}
            </HelperRow>
          ))}
          {h.pending && (
            <div className="field">
              <div className="field-label">
                <span>
                  {h.needsYes
                    ? "RigControl is waiting for your yes"
                    : "Some programs are not set up yet"}
                </span>
                <span className="sub">
                  {h.needsYes
                    ? "The button opens the setup step that lists what will be installed. Nothing is installed before you say yes there."
                    : d.elevated
                      ? "The button tries again now."
                      : "Installing needs administrator rights. The button restarts RigControl as administrator, which then installs what is missing."}
                  {h.serviceWillMove
                    ? " The OpenRGB service on this PC will run RigControl's copy from then on."
                    : ""}
                </span>
              </div>
              <div className="field-control">
                <button
                  className="btn small primary"
                  disabled={busy !== null}
                  onClick={() => void installMissing()}
                >
                  {busy === "install"
                    ? "Installing…"
                    : "Install the missing programs"}
                </button>
              </div>
            </div>
          )}
          {h.problems.length > 0 && (
            <div className="sub" role="status">
              {h.problems.map((p, i) => (
                <div key={i}>{p}</div>
              ))}
            </div>
          )}
          {h.notCarried.length > 0 && (
            <div className="sub">
              Not included in this build of RigControl:{" "}
              {h.notCarried.join(", ")}. What depends on{" "}
              {h.notCarried.length === 1 ? "it" : "them"} will not work until
              you install {h.notCarried.length === 1 ? "it" : "them"} yourself.
            </div>
          )}
          {s && s.notes.length > 0 && (
            <div className="sub" style={{ marginTop: 6 }}>
              {s.notes.map((n, i) => (
                <div key={i}>{n}</div>
              ))}
            </div>
          )}
        </>
      ) : (
        <div className="sub">Starting…</div>
      )}

      <h2 style={{ marginTop: 18 }}>Apps you no longer need</h2>
      {d.oldApps.length === 0 ? (
        <div className="sub">None installed. RigControl does their jobs.</div>
      ) : (
        d.oldApps.map((a) => (
          <div className="field" key={a.id}>
            <div className="field-label">
              <span>
                {a.name}
                {a.version ? ` ${a.version}` : ""}
              </span>
              <span className="sub">{a.why}</span>
            </div>
            <div className="field-control">
              <button
                className="btn small danger"
                disabled={busy !== null || !d.elevated}
                onClick={() => void uninstall(a.id, a.name)}
              >
                {busy === "uninstall-" + a.id ? "Opening…" : "Uninstall"}
              </button>
            </div>
          </div>
        ))
      )}
    </section>
  );
}

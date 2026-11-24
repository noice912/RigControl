import {
  useCallback,
  useEffect,
  useId,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { call, errorMessage } from "../bridge";
import { useApp } from "../appContext";
import { useAction } from "../hooks";
import { rescanHealth, tidyFindings } from "../health";
import { onOpenFirstRun } from "../nav";
import { Notice, Switch, useConfirm } from "./controls";
import { FindingGroups } from "./HealthFindings";
import { useToast } from "./Toast";
import type { FirstRunInfo, HelperProgram, HelpersInfo } from "../types";

type StepId = "scan" | "helpers" | "conflicts" | "autostart";

const TITLES: Record<StepId, string> = {
  scan: "Checking your PC",
  helpers: "RigControl needs these programs",
  conflicts: "Things in the way",
  autostart: "Start with Windows",
};

/** Set just before RigControl restarts as administrator from the helpers step, so the new copy opens on that step. */
const RESUME_KEY = "rigcontrol.setup.resumeHelpers";

function takeResume(): boolean {
  try {
    const set = window.localStorage.getItem(RESUME_KEY) === "1";
    if (set) window.localStorage.removeItem(RESUME_KEY);
    return set;
  } catch {
    return false; // storage unavailable: the setup simply starts at its first step
  }
}

/** Fills in anything the backend left out. With no answer at all there is nothing to ask and nothing to do. */
export function tidyHelpers(
  h: Partial<HelpersInfo> | null | undefined,
): HelpersInfo {
  return {
    items: Array.isArray(h?.items) ? h.items : [],
    consented: h?.consented !== false,
    needsYes: h?.needsYes === true,
    pending: h?.pending === true,
    serviceWillMove: h?.serviceWillMove === true,
    notCarried: Array.isArray(h?.notCarried) ? h.notCarried : [],
    problems: Array.isArray(h?.problems) ? h.problems : [],
  };
}

/** Fills in anything the backend left out, so a half-answer can never break the app. */
function tidy(r: FirstRunInfo): FirstRunInfo {
  return {
    done: r.done === true,
    elevated: r.elevated === true,
    autostart: r.autostart === true,
    restartSuggested: r.restartSuggested === true,
    restartForAutostart: r.restartForAutostart === true,
    restartForEqualiser: r.restartForEqualiser === true,
    otherUsersSignedIn:
      Number(r.otherUsersSignedIn) > 0 ? Number(r.otherUsersSignedIn) : 0,
    accountIsAdministrator: r.accountIsAdministrator !== false,
    otherAccount: r.otherAccount === true,
    programFolderOpen: r.programFolderOpen === true,
    hardware: Array.isArray(r.hardware) ? r.hardware : [],
    features: Array.isArray(r.features) ? r.features : [],
    helpers: tidyHelpers(r.helpers),
    findings: tidyFindings(r.findings),
  };
}

/** One helper program: what it is, its licence and whether it is on this PC already. Nothing to choose. */
export function HelperRow(props: {
  helper: HelperProgram;
  children?: ReactNode;
}) {
  const h = props.helper;
  return (
    <div className="field">
      <div className="field-label">
        <span>{h.name}</span>
        <span className="sub">{h.what}</span>
        <span className="sub faint">Licence: {h.licence}</span>
        {props.children}
      </div>
      <div className="field-control">
        {h.installed ? (
          <span className="badge ok">Already installed</span>
        ) : h.carried ? (
          <span className="badge warn">Not installed yet</span>
        ) : (
          <span className="sub">Not included in this build</span>
        )}
      </div>
    </div>
  );
}

/**
 * The first-time setup, shown over the app until it has been finished. The step "RigControl needs these programs"
 * is also shown by itself on a PC that is set up but where the helper programs still wait for the user's yes.
 * If RigControl can't answer, it simply doesn't show: the app is never blocked by it.
 */
export function FirstRun(props: { onOpenChange?: (open: boolean) => void }) {
  const { restartAsAdmin } = useApp();
  const toast = useToast();
  const { busy, run } = useAction();
  const { confirm, element: confirmElement } = useConfirm();
  const titleId = useId();

  const [d, setD] = useState<FirstRunInfo | null>(null);
  const [open, setOpen] = useState(false);
  const [steps, setSteps] = useState<StepId[]>([
    "scan",
    "conflicts",
    "autostart",
  ]);
  const [step, setStep] = useState(0);
  // "leave": the user wanted to close the setup before saying yes to the helper programs.
  const [phase, setPhase] = useState<"steps" | "leave" | "restart">("steps");
  const [autostart, setAutostart] = useState(true);
  const [restartAt, setRestartAt] = useState<number | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(0);

  const boxRef = useRef<HTMLDivElement>(null);
  const headRef = useRef<HTMLHeadingElement>(null);
  const confirmOpen = useRef(false);
  confirmOpen.current = confirmElement !== null;

  const show = useCallback((r: FirstRunInfo) => {
    const t = tidy(r);
    setD(t);
    // Suggested for a new setup; the current choice when the setup is opened again later. Not suggested where it
    // cannot do its job: a standard Windows account, or RigControl running as someone else's account.
    setAutostart(
      t.done ? t.autostart : t.accountIsAdministrator && !t.otherAccount,
    );
    // The helpers step is only there while the yes is missing. On a PC that is already set up it is the only step.
    const list: StepId[] =
      t.done && t.helpers.needsYes
        ? ["helpers"]
        : t.helpers.needsYes
          ? ["scan", "helpers", "conflicts", "autostart"]
          : ["scan", "conflicts", "autostart"];
    setSteps(list);
    const resume = takeResume();
    setStep(resume && t.elevated ? Math.max(0, list.indexOf("helpers")) : 0);
    setPhase("steps");
    setRestartAt(null);
    setOpen(true);
  }, []);

  useEffect(() => {
    let alive = true;
    call<FirstRunInfo>("app.getFirstRun")
      .then((r) => {
        if (alive && r && (r.done === false || r.helpers?.needsYes === true))
          show(r);
      })
      .catch(() => {
        // No answer: don't show the setup.
      });
    const off = onOpenFirstRun(() => {
      call<FirstRunInfo>("app.getFirstRun")
        .then((r) => alive && show(r))
        .catch((err) => toast.show(errorMessage(err), "error"));
    });
    return () => {
      alive = false;
      off();
    };
  }, [show, toast]);

  const onOpenChange = props.onOpenChange;
  useEffect(() => onOpenChange?.(open), [open, onOpenChange]);

  // Each step starts with the keyboard at its heading.
  useEffect(() => {
    if (open) headRef.current?.focus();
  }, [open, step, phase, restartAt === null]);

  const close = useCallback(() => setOpen(false), []);
  const needsYes = d?.helpers.needsYes === true;
  // Escape puts the setup away for now. It is not marked as finished, so it comes back next time. While the helper
  // programs wait for a yes it asks first (install, or quit); from that question it goes back to the steps.
  // A restart that is counting down is never left running out of sight.
  const escape = useRef(close);
  escape.current =
    restartAt !== null
      ? () => {}
      : phase === "leave"
        ? () => setPhase("steps")
        : needsYes && phase === "steps"
          ? () => setPhase("leave")
          : close;

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      const el = boxRef.current;
      // A question on top handles its own keys: this setup's own, or one a step inside it opened (a fix's "are you sure").
      if (
        !el ||
        confirmOpen.current ||
        el.querySelector('[role="dialog"], [role="alertdialog"]')
      )
        return;
      if (e.key === "Escape") {
        e.preventDefault();
        escape.current();
        return;
      }
      if (e.key !== "Tab") return;
      const items = [
        ...el.querySelectorAll<HTMLElement>(
          "button, input, select, summary, [tabindex='0']",
        ),
      ].filter((x) => !x.hasAttribute("disabled"));
      const first = items[0];
      const last = items[items.length - 1];
      if (!first || !last) return;
      const at = document.activeElement;
      if (!el.contains(at) || at === headRef.current) {
        e.preventDefault();
        (e.shiftKey ? last : first).focus();
      } else if (e.shiftKey && at === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && at === last) {
        e.preventDefault();
        first.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  // The restart countdown.
  useEffect(() => {
    if (restartAt === null) return;
    const tick = () =>
      setSecondsLeft(Math.max(0, Math.ceil((restartAt - Date.now()) / 1000)));
    tick();
    const timer = window.setInterval(tick, 250);
    return () => window.clearInterval(timer);
  }, [restartAt]);

  if (!open || !d) return null;

  const current: StepId = steps[Math.min(step, steps.length - 1)] ?? "scan";
  const only = steps.length === 1; // the helpers step by itself, on a PC that is already set up
  const last = step >= steps.length - 1;

  // After a fix: look at the PC again (the report RigControl keeps is up to a minute old), then show what is left.
  const reload = async () => {
    const h = await rescanHealth();
    setD((prev) => prev && { ...prev, findings: h.findings });
  };

  // "Install and continue": the one yes for all helper programs. Only a copy of RigControl that runs as
  // administrator takes it, so without administrator rights the button restarts RigControl first; the new copy
  // opens on this step and the user presses the button there.
  const install = async () => {
    if (!d.elevated) {
      try {
        window.localStorage.setItem(RESUME_KEY, "1");
      } catch {
        // storage unavailable: the new copy starts the setup at its first step
      }
      const r = await run("install", () =>
        call<{ restarting: boolean }>("app.restartAsAdmin"),
      );
      if (!r?.restarting) {
        // Permission was not given (the reason was shown): RigControl stays as it is, and so does this step.
        try {
          window.localStorage.removeItem(RESUME_KEY);
        } catch {
          // nothing to tidy
        }
      }
      return;
    }
    const r = await run("install", () =>
      call<FirstRunInfo>("app.installHelpers", undefined, 300000),
    );
    if (!r) return; // the reason was shown; the button is still there
    const t = tidy(r);
    setD(t);
    setPhase("steps");
    const at = Math.max(0, steps.indexOf("helpers"));
    // Something did not get set up: stay on the step, which now says what went wrong and how to carry on.
    if (t.helpers.needsYes || t.helpers.pending) setStep(at);
    else if (!only) setStep(Math.min(at + 1, steps.length - 1));
    else if (t.restartSuggested) setPhase("restart");
    else close();
  };

  const quit = async () => {
    await run("quit", () => call("app.quit"));
  };

  const finish = async () => {
    if (only) {
      // Nothing else was asked: the setup was finished before.
      if (d.restartSuggested) setPhase("restart");
      else close();
      return;
    }
    const r = await run("finish", () =>
      call<FirstRunInfo>("app.completeFirstRun", { autostart }, 60000),
    );
    if (!r) return; // the reason was shown; the user can try again or skip
    const t = tidy(r);
    setD(t);
    if (t.restartSuggested) setPhase("restart");
    else close();
  };

  // Skipping changes nothing: Start with Windows stays as it is. While the helper programs wait for a yes the
  // setup can't be skipped: RigControl says why and offers to install them or to quit.
  const skip = async () => {
    if (needsYes) {
      setPhase("leave");
      return;
    }
    close();
    if (only) return;
    try {
      await call("app.completeFirstRun", { autostart: d.autostart }, 60000);
    } catch {
      // Not saved, so the setup is offered again next time. Nothing else is affected.
    }
  };

  const restartNow = async () => {
    // Ask again now: someone may have signed in since the setup was opened.
    let others = d.otherUsersSignedIn;
    try {
      others = tidy(
        await call<FirstRunInfo>("app.getFirstRun"),
      ).otherUsersSignedIn;
    } catch {
      // keep what was known; RigControl checks once more before it restarts anything
    }
    const ok = await confirm({
      title:
        others > 0
          ? "Someone else is signed in to this PC"
          : "Restart your PC now?",
      body:
        others > 0
          ? "A restart closes their programs too, and they lose anything they have not saved. Ask them first, or choose Cancel and restart later. Save your own work as well. A short countdown starts and you can still cancel it."
          : "Save your work first: every open program will close. A short countdown starts and you can still cancel it.",
      confirmLabel: others > 0 ? "Restart anyway" : "Restart now",
      danger: true,
    });
    if (!ok) return;
    const r = await run("restart", () =>
      call<{ scheduled: boolean; seconds: number }>("app.restartPc", {
        evenWithOtherUsers: others > 0,
      }),
    );
    if (r?.scheduled)
      setRestartAt(Date.now() + Math.max(0, Number(r.seconds) || 0) * 1000);
  };

  const cancelRestart = async () => {
    const r = await run(
      "cancel",
      async () => {
        await call("app.cancelRestartPc");
        return true;
      },
      "The restart was cancelled.",
    );
    if (r) setRestartAt(null);
  };

  const available = d.features.filter((f) => f.available);
  const unavailable = d.features.filter((f) => !f.available);
  const toInstall = d.helpers.items.filter((h) => h.carried && !h.installed);
  const installLabel =
    busy === "install"
      ? d.elevated
        ? "Installing…"
        : "Restarting…"
      : "Install and continue";

  return (
    <>
      <div className="backdrop">
        <div
          className="dialog firstrun"
          role="dialog"
          aria-modal="true"
          aria-labelledby={titleId}
          ref={boxRef}
        >
          {phase === "steps" ? (
            <>
              <div>
                {!only && (
                  <div className="sub">
                    Setting up RigControl · step {step + 1} of {steps.length}
                  </div>
                )}
                <h2 id={titleId} tabIndex={-1} ref={headRef}>
                  {TITLES[current]}
                </h2>
              </div>

              <div className="firstrun-body">
                {current === "scan" && (
                  <>
                    <p className="notice-text">
                      RigControl looked at this PC so it only shows what fits
                      it. Nothing was changed.
                    </p>
                    {!d.elevated && (
                      <Notice
                        tone="warn"
                        title="Running without administrator rights"
                        actions={
                          <button
                            className="btn small"
                            onClick={() => void restartAsAdmin()}
                          >
                            Restart as administrator
                          </button>
                        }
                      >
                        Some parts of this PC can't be seen or controlled
                        without them, so the lists below may be short.
                      </Notice>
                    )}
                    {d.otherAccount && (
                      <Notice
                        tone="warn"
                        title="Running as a different Windows account"
                      >
                        RigControl was started as administrator with another
                        account's password, so it runs as that account, not as
                        you. Its settings are kept for that account, and Start
                        with Windows would apply to that account's sign-in. To
                        set RigControl up for yourself, your own Windows account
                        needs to be an administrator.
                      </Notice>
                    )}
                    {!d.helpers.needsYes && d.helpers.notCarried.length > 0 && (
                      <Notice
                        tone="warn"
                        title="Helper programs to install yourself"
                      >
                        This build of RigControl does not carry{" "}
                        {d.helpers.notCarried.join(", ")}, and{" "}
                        {d.helpers.notCarried.length === 1 ? "it is" : "they are"}{" "}
                        not on this PC. What depends on{" "}
                        {d.helpers.notCarried.length === 1 ? "it" : "them"} will
                        not work until you install{" "}
                        {d.helpers.notCarried.length === 1 ? "it" : "them"}{" "}
                        yourself. Settings lists them under Built in.
                      </Notice>
                    )}
                    {d.hardware.length > 0 && (
                      <div>
                        <div className="finding-module">What was found</div>
                        {d.hardware.map((h, i) => (
                          <div className="field" key={i}>
                            <div className="field-label">
                              <span>{h.label}</span>
                            </div>
                            <div className="field-control">{h.value}</div>
                          </div>
                        ))}
                      </div>
                    )}
                    <div>
                      <div className="finding-module">
                        What RigControl can do here
                      </div>
                      {available.length === 0 ? (
                        <div className="sub">
                          Nothing was found that RigControl can control on this
                          PC.
                        </div>
                      ) : (
                        <ul className="firstrun-list">
                          {available.map((f) => (
                            <li key={f.id}>{f.name}</li>
                          ))}
                        </ul>
                      )}
                    </div>
                    {unavailable.length > 0 && (
                      <details>
                        <summary className="finding-summary">
                          {unavailable.length === 1
                            ? "1 thing that doesn't apply to this PC"
                            : `${unavailable.length} things that don't apply to this PC`}
                        </summary>
                        <ul className="firstrun-list">
                          {unavailable.map((f) => (
                            <li key={f.id}>
                              {f.name}
                              {f.reason && (
                                <span className="sub"> · {f.reason}</span>
                              )}
                            </li>
                          ))}
                        </ul>
                      </details>
                    )}
                  </>
                )}

                {current === "helpers" && (
                  <>
                    <p className="notice-text">
                      {needsYes
                        ? "RigControl works through these free, open-source programs for lights, sensors, fans and sound, so it installs all of them together. Nothing is installed until you press Install and continue."
                        : d.helpers.pending
                          ? "You said yes, but not everything could be set up."
                          : "Everything RigControl needs is set up."}
                    </p>
                    <div>
                      {d.helpers.items.map((h) => (
                        <HelperRow key={h.id} helper={h} />
                      ))}
                    </div>
                    {needsYes && d.helpers.serviceWillMove && (
                      <div className="sub">
                        An OpenRGB service is already set up on this PC. From
                        then on it runs RigControl's copy of OpenRGB; its
                        settings are copied and the old files stay where they
                        are.
                      </div>
                    )}
                    {needsYes && !d.elevated && (
                      <div className="sub">
                        {d.accountIsAdministrator
                          ? "Installing needs administrator rights. The button first restarts RigControl as administrator (Windows asks for permission). This step is then shown again: press the button once more there and the programs are installed."
                          : "This Windows account is not an administrator. The button asks for an administrator's password and restarts RigControl with it. This step is then shown again: press the button once more there and the programs are installed."}
                      </div>
                    )}
                    {!needsYes && d.helpers.pending && (
                      <Notice
                        tone="warn"
                        title="Some programs are not set up yet"
                      >
                        {d.helpers.problems.length > 0
                          ? d.helpers.problems.map((p, i) => (
                              <div key={i}>{p}</div>
                            ))
                          : "The set-up is taking longer than expected."}
                        <div>
                          You can carry on. What depends on them will not work
                          until then; Settings has a button to try again (under
                          Built in).
                        </div>
                      </Notice>
                    )}
                    {d.helpers.notCarried.length > 0 && (
                      <div className="sub">
                        Not included in this build of RigControl:{" "}
                        {d.helpers.notCarried.join(", ")}. What depends on{" "}
                        {d.helpers.notCarried.length === 1 ? "it" : "them"} will
                        not work until you install{" "}
                        {d.helpers.notCarried.length === 1 ? "it" : "them"}{" "}
                        yourself.
                      </div>
                    )}
                  </>
                )}

                {current === "conflicts" &&
                  (d.findings.length === 0 ? (
                    <div className="empty">
                      <strong>Nothing is getting in RigControl's way.</strong>
                    </div>
                  ) : (
                    <>
                      <p className="notice-text">
                        These programs or Windows settings do the same jobs as
                        RigControl. You can sort them out now or later on the
                        Health page.
                      </p>
                      <FindingGroups findings={d.findings} onFixed={reload} />
                    </>
                  ))}

                {current === "autostart" && (
                  <>
                    <div className="field">
                      <div className="field-label">
                        <span>Start RigControl with Windows</span>
                        <span className="sub">
                          {d.elevated || d.accountIsAdministrator
                            ? "RigControl starts as administrator when you sign in, so fans, lights and sensors work straight away without a permission prompt each time."
                            : "RigControl starts when you sign in. This Windows account is not an administrator, so it starts without administrator rights: sensors, motherboard fans and the helper programs will not work."}
                        </span>
                      </div>
                      <div className="field-control">
                        <Switch
                          checked={autostart}
                          disabled={busy !== null}
                          onChange={setAutostart}
                        />
                      </div>
                    </div>
                    {autostart && !d.elevated && (
                      <div className="sub">
                        Windows will ask for administrator permission once to
                        set this up.
                      </div>
                    )}
                    {autostart && d.programFolderOpen && (
                      <Notice
                        tone="warn"
                        title="RigControl's folder can be changed by any program"
                      >
                        RigControl would start as administrator from a folder
                        that programs without administrator rights can change.
                        Such a program could replace RigControl's files and get
                        administrator rights that way. It is safer to close
                        RigControl, move its folder to one only administrators
                        can change (for example inside C:\Program Files) and
                        start it from there. You can also carry on as it is.
                      </Notice>
                    )}
                    <div className="sub">
                      You can change this later in Settings.
                    </div>
                  </>
                )}
              </div>

              <div className="firstrun-actions">
                {needsYes ? (
                  <button
                    className="btn ghost"
                    disabled={busy !== null}
                    onClick={() => setPhase("leave")}
                  >
                    Close setup
                  </button>
                ) : only ? (
                  <span />
                ) : (
                  <button
                    className="btn ghost"
                    disabled={busy !== null}
                    onClick={() => void skip()}
                  >
                    Skip setup
                  </button>
                )}
                <div className="row" style={{ gap: 10 }}>
                  {step > 0 && (
                    <button
                      className="btn"
                      disabled={busy !== null}
                      onClick={() => setStep(step - 1)}
                    >
                      Back
                    </button>
                  )}
                  {current === "helpers" && needsYes ? (
                    <button
                      className="btn primary"
                      disabled={
                        busy !== null ||
                        toInstall.length +
                          (d.helpers.serviceWillMove ? 1 : 0) ===
                          0
                      }
                      onClick={() => void install()}
                    >
                      {installLabel}
                    </button>
                  ) : last ? (
                    <button
                      className="btn primary"
                      disabled={busy !== null}
                      onClick={() => void finish()}
                    >
                      {busy === "finish"
                        ? "Saving…"
                        : only
                          ? "Close"
                          : "Finish"}
                    </button>
                  ) : (
                    <button
                      className="btn primary"
                      disabled={busy !== null}
                      onClick={() => setStep(step + 1)}
                    >
                      Next
                    </button>
                  )}
                </div>
              </div>
            </>
          ) : phase === "leave" ? (
            <>
              <h2 id={titleId} tabIndex={-1} ref={headRef}>
                RigControl can't run its hardware features without these
                programs
              </h2>
              <div className="notice-text">
                Lights, temperatures, fan control and the audio equaliser all
                work through{" "}
                {toInstall.map((h) => h.name).join(", ") ||
                  "its helper programs"}
                . Without {toInstall.length === 1 ? "it" : "them"} RigControl
                can't do those jobs, so it doesn't carry on. Nothing has been
                installed.
              </div>
              {!d.elevated && (
                <div className="sub">
                  Install and continue first restarts RigControl as
                  administrator and then shows the list once more.
                </div>
              )}
              <div className="firstrun-actions">
                <button
                  className="btn ghost"
                  disabled={busy !== null}
                  onClick={() => setPhase("steps")}
                >
                  Show the list again
                </button>
                <div className="row" style={{ gap: 10 }}>
                  <button
                    className="btn"
                    disabled={busy !== null}
                    onClick={() => void quit()}
                  >
                    {busy === "quit" ? "Closing…" : "Quit RigControl"}
                  </button>
                  <button
                    className="btn primary"
                    disabled={busy !== null}
                    onClick={() => void install()}
                  >
                    {installLabel}
                  </button>
                </div>
              </div>
            </>
          ) : restartAt === null ? (
            <>
              <h2 id={titleId} tabIndex={-1} ref={headRef}>
                Restart your PC?
              </h2>
              <div className="notice-text">
                RigControl is set up.{" "}
                {d.restartForAutostart &&
                  "Restarting now lets it start with Windows as administrator and check everything from start-up. "}
                {d.restartForEqualiser &&
                  "The equaliser only works after Windows has been restarted once. "}
                You can also carry on and restart whenever suits you.
              </div>
              {d.otherUsersSignedIn > 0 && (
                <div className="sub">
                  Someone else is signed in to this PC. A restart would close
                  their programs too, so Later is the safe choice.
                </div>
              )}
              <div className="dialog-actions">
                <button
                  className="btn"
                  disabled={busy !== null}
                  onClick={close}
                >
                  Later
                </button>
                <button
                  className="btn primary"
                  disabled={busy !== null}
                  onClick={() => void restartNow()}
                >
                  Restart now
                </button>
              </div>
            </>
          ) : (
            <>
              <h2 id={titleId} tabIndex={-1} ref={headRef}>
                {secondsLeft > 0
                  ? "Your PC is about to restart"
                  : "Restarting…"}
              </h2>
              <div
                className="row"
                style={{ gap: 16, alignItems: "center" }}
                role="timer"
                aria-live="off"
              >
                <span className="countdown">{secondsLeft}</span>
                <span className="notice-text">
                  {secondsLeft === 1 ? "second" : "seconds"} left. Save your
                  work now.
                </span>
              </div>
              <div className="dialog-actions">
                <button
                  className="btn primary"
                  disabled={busy !== null}
                  onClick={() => void cancelRestart()}
                >
                  {busy === "cancel" ? "Cancelling…" : "Cancel the restart"}
                </button>
              </div>
            </>
          )}
        </div>
      </div>
      {confirmElement}
    </>
  );
}

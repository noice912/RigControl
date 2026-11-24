import { call } from "../bridge";
import { useApp } from "../appContext";
import { useAction } from "../hooks";
import { moduleLabel, SEVERITY_ORDER, useHealth } from "../health";
import { goTo } from "../nav";
import { Notice, useConfirm } from "./controls";
import type { HealthFinding, HealthSeverity } from "../types";

const SEVERITY_TITLE: Record<HealthSeverity, string> = {
  blocks: "Stopping something from working",
  warn: "May get in the way",
  info: "Good to know",
};

const KIND_LABEL: Record<string, string> = {
  app: "Program",
  process: "Program",
  program: "Program",
  service: "Background service",
  driver: "Driver",
  registry: "Windows setting",
  task: "Starts with Windows",
  setting: "Windows setting",
  windows: "Windows setting",
  system: "Windows setting",
  hardware: "Hardware",
};

/**
 * Health findings grouped by how serious they are, then by the part of RigControl they affect.
 * A fix button shows only when the backend offers one, and always asks first.
 * `onFixed` runs after a fix so the caller can check again.
 */
export function FindingGroups(props: { findings: HealthFinding[]; panels?: boolean; onFixed: () => Promise<unknown> | void }) {
  const { modules } = useApp();
  const { busy, run } = useAction();
  const { confirm, element } = useConfirm();

  const fix = async (f: HealthFinding) => {
    const wanted = f.fix;
    if (!wanted) return;
    const ok = await confirm({
      title: `${wanted.label}?`,
      body: wanted.confirm || (
        <>
          {f.name}: {f.whatToDo || f.whatItBreaks}
        </>
      ),
      confirmLabel: wanted.label,
    });
    if (!ok) return;
    const done = await run("fix-" + f.id, async () => {
      await call(wanted.method, wanted.args ?? undefined, 60000);
      return true;
    });
    if (done) await run("recheck", async () => void (await props.onFixed()), "Done. Checked again.");
  };

  const label = (module: string) => moduleLabel(module, modules.find((m) => m.name === module)?.displayName);

  return (
    <>
      {element}
      {SEVERITY_ORDER.map((sev) => {
        const list = props.findings.filter((f) => f.severity === sev);
        if (list.length === 0) return null;
        const groups = [...new Set(list.map((f) => f.module))].sort((a, b) => label(a).localeCompare(label(b)));
        return (
          <section key={sev} className={props.panels ? "panel" : "stack"} aria-label={SEVERITY_TITLE[sev]}>
            <h2 className={`finding-sev ${sev}`}>
              {SEVERITY_TITLE[sev]}
              <span className="badge">{list.length}</span>
            </h2>
            {groups.map((g) => (
              <div key={g} className="finding-group">
                <div className="finding-module">{label(g)}</div>
                {list
                  .filter((f) => f.module === g)
                  .map((f) => {
                    const kind = KIND_LABEL[f.kind.toLowerCase()];
                    return (
                      <div key={f.id} className={`finding ${sev}`}>
                        <div className="finding-body">
                          <div className="finding-name">
                            {f.name}
                            {kind && <span className="badge">{kind}</span>}
                          </div>
                          {f.whatItBreaks && <div>{f.whatItBreaks}</div>}
                          {f.whatToDo && (
                            <div className="sub">
                              <strong>What to do:</strong> {f.whatToDo}
                            </div>
                          )}
                          {!f.verified && (
                            <div className="sub faint">We have not tried RigControl next to this one ourselves yet, so this is our best understanding.</div>
                          )}
                        </div>
                        {f.fix && (
                          <button className="btn small" disabled={busy !== null} onClick={() => void fix(f)}>
                            {busy === "fix-" + f.id ? "Working…" : f.fix.label}
                          </button>
                        )}
                      </div>
                    );
                  })}
              </div>
            ))}
          </section>
        );
      })}
    </>
  );
}

/**
 * A compact notice for one page: what the health check found for these modules, with a link to the Health page.
 * Shows nothing when there is nothing serious (or the health check is not available).
 */
export function HealthNotice({ modules, exclude }: { modules: string[]; /** Ids of findings the page already explains itself. */ exclude?: string[] }) {
  const { report } = useHealth();
  const list = (report?.findings ?? []).filter((f) => modules.includes(f.module) && f.severity !== "info" && !exclude?.includes(f.id));
  const first = list[0];
  if (!first) return null;
  const blocks = list.some((f) => f.severity === "blocks");
  const names =
    list
      .slice(0, 3)
      .map((f) => f.name)
      .join(", ") + (list.length > 3 ? ` and ${list.length - 3} more` : "");
  const title =
    list.length === 1
      ? `${first.name} ${blocks ? "is in the way" : "may get in the way"}`
      : `${list.length} things ${blocks ? "are in the way here" : "may get in the way here"}`;
  return (
    <Notice
      tone={blocks ? "error" : "warn"}
      title={title}
      actions={
        <button className="btn small" onClick={() => goTo("health")}>
          Open Health
        </button>
      }
    >
      {list.length === 1 ? first.whatItBreaks || first.whatToDo : `${names}. Health explains each one and what to do.`}
    </Notice>
  );
}

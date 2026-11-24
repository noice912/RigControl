import { useApp, useModule } from "../appContext";
import { Notice } from "./controls";

/** Shows why a feature area is not fully working, if it isn't. Renders nothing when the module is ready. */
export function ModuleProblem({ name }: { name: string }) {
  const m = useModule(name);
  const { info, restartAsAdmin } = useApp();
  if (!m || m.state === "ready" || m.state === "notStarted") return null;
  if (m.state === "starting") return <Notice title={`${m.displayName} is starting`}>This takes a few seconds.</Notice>;
  const needsAdmin = !info?.isElevated && /administrator/i.test(m.message ?? "");
  return (
    <Notice
      tone={m.state === "failed" ? "error" : "warn"}
      title={m.state === "failed" ? `${m.displayName} is not available` : `${m.displayName} is running with limits`}
      actions={
        needsAdmin ? (
          <button className="btn" onClick={() => void restartAsAdmin()}>
            Restart as administrator
          </button>
        ) : undefined
      }
    >
      {m.message ?? "No details were given."}
    </Notice>
  );
}

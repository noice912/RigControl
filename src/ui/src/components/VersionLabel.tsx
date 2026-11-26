import { useApp } from "../appContext";
import { useLive } from "../hooks";

/**
 * The running version as SemVer ("1.1.2", or "1.1.3-rc.1" for a development build; never the "+commit" part).
 * The Windows app's updates module reads it from the informational version, so a development build's "-rc.N" is
 * kept; app.getInfo's version is the fallback.
 */
export function useRunningVersion(): string | null {
  const { info, can } = useApp();
  const withUpdates = !!info && can("host.updates");
  const { data } = useLive("updates.status", withUpdates ? "updates.getStatus" : undefined);
  const v = (withUpdates ? data?.current : null) ?? info?.version ?? null;
  return v ? (v.split("+")[0] ?? v) : null;
}

/** True for a version with a pre-release part: a development build. */
export function isDevelopmentBuild(version: string | null | undefined) {
  return !!version && (version.split("+")[0] ?? "").includes("-");
}

/** "RigControl 1.1.2", or "RigControl 1.1.3-rc.1" with a small "Development build" badge. */
export function VersionLabel({ prefix = "RigControl ", version }: { prefix?: string; version?: string | null }) {
  const running = useRunningVersion();
  const v = version ?? running;
  if (!v) return null;
  return (
    <>
      {prefix}
      {v}
      {isDevelopmentBuild(v) && (
        <span className="badge warn" title="An unfinished version published for testing, not the public build.">
          Development build
        </span>
      )}
    </>
  );
}

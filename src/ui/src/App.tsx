import { useEffect, useState, type ReactNode } from "react";
import { call, hostKind } from "./bridge";
import { notOnThisSystem, useApp } from "./appContext";
import { NotOnThisSystem } from "./components/ModuleProblem";
import { Icons } from "./components/icons";
import { FirstRun } from "./components/FirstRun";
import { useHealth } from "./health";
import { onGoTo } from "./nav";
import { Overview } from "./pages/Overview";
import { Cooling } from "./pages/Cooling";
import { Graphics } from "./pages/Graphics";
import { Lighting } from "./pages/Lighting";
import { Drivers } from "./pages/Drivers";
import { Cpu } from "./pages/Cpu";
import { Games } from "./pages/Games";
import { Capture } from "./pages/Capture";
import { Health } from "./pages/Health";
import { Settings } from "./pages/Settings";
import { Developer } from "./pages/Developer";
import { UpdateBanner } from "./components/Updates";
import { useGitHub } from "./components/GitHubAccount";
import { isDevelopmentBuild, useRunningVersion } from "./components/VersionLabel";

type Route = "overview" | "cooling" | "graphics" | "capture" | "lighting" | "drivers" | "cpu" | "games" | "audio" | "health" | "settings" | "developer";

/**
 * `module` is the module whose state the entry shows. `needs` is what the page cannot do without, when that is not
 * `module`: another module's name, or something the app itself must offer ("host.audio").
 */
const NAV: { route: Route; label: string; icon: () => ReactNode; module?: string; needs?: string }[] = [
  { route: "overview", label: "Overview", icon: Icons.overview, module: "sensors" },
  { route: "cooling", label: "Cooling", icon: Icons.cooling, module: "cooling" },
  { route: "graphics", label: "Graphics", icon: Icons.graphics, module: "nvidia" },
  { route: "capture", label: "Recording", icon: Icons.capture, module: "capture" },
  { route: "games", label: "Games", icon: Icons.games, module: "presence" },
  { route: "lighting", label: "Lighting", icon: Icons.lighting, module: "lighting" },
  { route: "cpu", label: "CPU", icon: Icons.cpu, module: "cpu" },
  { route: "drivers", label: "Drivers", icon: Icons.drivers, module: "drivers" },
  { route: "audio", label: "Audio", icon: Icons.audio, needs: "host.audio" },
  { route: "health", label: "Health", icon: Icons.health, needs: "health" },
  { route: "settings", label: "Settings", icon: Icons.settings },
  // Only for a GitHub account that may push to RigControl's repository (see `devHere` below).
  { route: "developer", label: "Developer", icon: Icons.developer, needs: "host.updates" },
];

const STORE_KEY = "rigcontrol.route";

function loadRoute(): Route {
  // "index.html#cooling" opens a given section (used by RigControl's --page option).
  const fromHash = window.location.hash.replace("#", "") as Route;
  if (NAV.some((n) => n.route === fromHash)) return fromHash;
  try {
    const r = window.localStorage.getItem(STORE_KEY) as Route | null;
    if (r && NAV.some((n) => n.route === r)) return r;
  } catch {
    // storage unavailable
  }
  return "overview";
}

export function App() {
  const { info, modules, restartAsAdmin, can } = useApp();
  const runningVersion = useRunningVersion();
  const [route, setRoute] = useState<Route>(loadRoute);
  const [setupOpen, setSetupOpen] = useState(false);
  const health = useHealth().report;
  const healthCount = health ? health.counts.blocks + health.counts.warn : 0;

  // Notices on other pages link here ("Open Health").
  useEffect(
    () =>
      onGoTo((r) => {
        if (NAV.some((n) => n.route === r)) setRoute(r as Route);
      }),
    [],
  );

  useEffect(() => {
    try {
      window.localStorage.setItem(STORE_KEY, route);
    } catch {
      // storage unavailable
    }
    // The first-time setup is drawn by this page, so the native audio view must not cover it.
    call("app.setNativeView", { view: route === "audio" && !setupOpen ? "audio" : null }).catch(() => {});
  }, [route, setupOpen]);

  // A page that does not fit this PC at all (its module says "Not available on this PC") is left out of the menu.
  const notForThisPc = (module?: string) =>
    !!module && (modules.find((x) => x.name === module)?.message ?? "").startsWith("Not available on this PC");
  // The same for a page whose feature has no version for this system (the Linux and macOS app),
  // and for a page that needs something this app does not offer.
  const notHere = (n: (typeof NAV)[number]) => {
    const name = n.needs ?? n.module;
    if (!name) return false;
    if (name.startsWith("host.")) return !can(name);
    return notOnThisSystem(modules.find((x) => x.name === name));
  };
  // The Developer page exists only for a signed-in GitHub account that may push to the maintainers' private repository; for anyone else there is no trace of it.
  const github = useGitHub().data;
  const devHere = !!info && can("host.updates") && !!github?.maintainer;
  const nav = NAV.filter((n) => !notForThisPc(n.module) && !notHere(n) && (n.route !== "developer" || devHere));
  // Overview is the usual fallback. Where Overview itself has no version, the first page that does.
  const overviewHere = !notHere(NAV[0]!);
  const shownRoute: Route = nav.some((n) => n.route === route) ? route : overviewHere ? "overview" : (nav[0]?.route ?? "settings");
  // A page that has no version here is never drawn, so it never asks its module for anything.
  const shownEntry = NAV.find((n) => n.route === shownRoute);
  const blocked = shownEntry && notHere(shownEntry) ? modules.find((x) => x.name === (shownEntry.needs ?? shownEntry.module)) : undefined;
  const firstRunHere = !!info && can("host.firstRun");
  // The Linux and macOS app has only some of the pages. Until it has said which, no page is drawn there, so a page
  // it does not have never asks for anything. The Windows app draws at once, as always.
  const waitingForModules = hostKind === "photino" && modules.length === 0;

  const flagged = (module?: string) => {
    if (!module) return false;
    const m = modules.find((x) => x.name === module);
    return m?.state === "failed" || m?.state === "degraded";
  };

  const page =
    waitingForModules ? null :
    blocked ? (
      <div className="page">
        <div className="page-head">
          <h1>{shownEntry!.label}</h1>
        </div>
        <NotOnThisSystem title={blocked.displayName} message={blocked.message} />
      </div>
    ) :
    shownRoute === "overview" ? <Overview /> :
    shownRoute === "cooling" ? <Cooling /> :
    shownRoute === "graphics" ? <Graphics /> :
    shownRoute === "capture" ? <Capture /> :
    shownRoute === "lighting" ? <Lighting /> :
    shownRoute === "drivers" ? <Drivers /> :
    shownRoute === "cpu" ? <Cpu /> :
    shownRoute === "games" ? <Games /> :
    shownRoute === "health" ? <Health /> :
    shownRoute === "settings" ? <Settings /> :
    shownRoute === "developer" ? <Developer /> : null;

  return (
    <div className={`shell ${shownRoute === "audio" ? "native" : ""}`}>
      <nav className="rail" aria-label="Sections">
        <div className="brand">
          <div className="brand-name">RigControl</div>
          {info && <div className="brand-machine">{info.machineName}</div>}
          {isDevelopmentBuild(runningVersion) && (
            <div className="brand-machine" title="An unfinished version published for testing, not the public build.">
              <span className="badge warn" style={{ marginLeft: 0 }}>Development build</span> {runningVersion}
            </div>
          )}
        </div>
        <div className="nav">
          {nav.map((n) => (
            <button key={n.route} className="nav-item" aria-current={shownRoute === n.route ? "page" : undefined} onClick={() => setRoute(n.route)}>
              {n.icon()}
              {n.label}
              {flagged(n.module) && <span className="nav-flag" title="Something here needs attention" />}
              {n.route === "health" && healthCount > 0 && (
                <span
                  className={`nav-count ${health && health.counts.blocks > 0 ? "blocks" : ""}`}
                  title={healthCount === 1 ? "1 thing is getting in the way" : `${healthCount} things are getting in the way`}
                >
                  {healthCount}
                </span>
              )}
            </button>
          ))}
        </div>
        {info && !info.isElevated && can("host.elevation") && (
          <div className="rail-foot">
            <span className="limited">Running without administrator rights. Some sensors and controls are off.</span>
            <button className="btn small" onClick={() => void restartAsAdmin()}>
              Restart as administrator
            </button>
          </div>
        )}
      </nav>
      {shownRoute !== "audio" && (
        <main className="main">
          {info && can("host.updates") && <UpdateBanner onSettings={shownRoute === "settings"} />}
          {page}
        </main>
      )}
      {/* The first-time setup is a Windows thing; where the app has none it is not asked for at all. */}
      {firstRunHere && <FirstRun onOpenChange={setSetupOpen} />}
    </div>
  );
}

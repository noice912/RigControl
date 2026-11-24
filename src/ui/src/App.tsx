import { useEffect, useState, type ReactNode } from "react";
import { call } from "./bridge";
import { useApp } from "./appContext";
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

type Route = "overview" | "cooling" | "graphics" | "capture" | "lighting" | "drivers" | "cpu" | "games" | "audio" | "health" | "settings";

const NAV: { route: Route; label: string; icon: () => ReactNode; module?: string }[] = [
  { route: "overview", label: "Overview", icon: Icons.overview, module: "sensors" },
  { route: "cooling", label: "Cooling", icon: Icons.cooling, module: "cooling" },
  { route: "graphics", label: "Graphics", icon: Icons.graphics, module: "nvidia" },
  { route: "capture", label: "Recording", icon: Icons.capture, module: "capture" },
  { route: "games", label: "Games", icon: Icons.games, module: "presence" },
  { route: "lighting", label: "Lighting", icon: Icons.lighting, module: "lighting" },
  { route: "cpu", label: "CPU", icon: Icons.cpu, module: "cpu" },
  { route: "drivers", label: "Drivers", icon: Icons.drivers, module: "drivers" },
  { route: "audio", label: "Audio", icon: Icons.audio },
  { route: "health", label: "Health", icon: Icons.health },
  { route: "settings", label: "Settings", icon: Icons.settings },
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
  const { info, modules, restartAsAdmin } = useApp();
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
  const nav = NAV.filter((n) => !notForThisPc(n.module));
  const shownRoute: Route = nav.some((n) => n.route === route) ? route : "overview";

  const flagged = (module?: string) => {
    if (!module) return false;
    const m = modules.find((x) => x.name === module);
    return m?.state === "failed" || m?.state === "degraded";
  };

  const page =
    shownRoute === "overview" ? <Overview /> :
    shownRoute === "cooling" ? <Cooling /> :
    shownRoute === "graphics" ? <Graphics /> :
    shownRoute === "capture" ? <Capture /> :
    shownRoute === "lighting" ? <Lighting /> :
    shownRoute === "drivers" ? <Drivers /> :
    shownRoute === "cpu" ? <Cpu /> :
    shownRoute === "games" ? <Games /> :
    shownRoute === "health" ? <Health /> :
    shownRoute === "settings" ? <Settings /> : null;

  return (
    <div className={`shell ${shownRoute === "audio" ? "native" : ""}`}>
      <nav className="rail" aria-label="Sections">
        <div className="brand">
          <div className="brand-name">RigControl</div>
          {info && <div className="brand-machine">{info.machineName}</div>}
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
        {info && !info.isElevated && (
          <div className="rail-foot">
            <span className="limited">Running without administrator rights. Some sensors and controls are off.</span>
            <button className="btn small" onClick={() => void restartAsAdmin()}>
              Restart as administrator
            </button>
          </div>
        )}
      </nav>
      {shownRoute !== "audio" && <main className="main">{page}</main>}
      <FirstRun onOpenChange={setSetupOpen} />
    </div>
  );
}

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { call, errorCode, errorMessage, on } from "./bridge";
import type { AppInfo, AppSettings, ModuleInfo } from "./types";
import { setTempUnit } from "./units";
import { useToast } from "./components/Toast";

type AppCtx = {
  info: AppInfo | null;
  modules: ModuleInfo[];
  restartAsAdmin: () => Promise<void>;
  openUrl: (url: string) => void;
  /**
   * True when this app can do the named thing ("lighting.screen", "host.tray": see docs/API.md).
   * Also true while the app has not said yet, and for an older app that sends no list: nothing is hidden on a guess.
   */
  can: (capability: string) => boolean;
  /** True on the Linux and macOS app. */
  notWindows: boolean;
};

const Ctx = createContext<AppCtx>({
  info: null,
  modules: [],
  restartAsAdmin: async () => {},
  openUrl: () => {},
  can: () => true,
  notWindows: false,
});

/** How a module's message starts when the feature has no version for this system (Linux, macOS). */
export const NOT_ON_THIS_SYSTEM = "Not available on this system";

/** True when the module is a stand-in that only says "Not available on this system". */
export function notOnThisSystem(m: ModuleInfo | null | undefined): boolean {
  return !!m && m.state === "failed" && (m.message ?? "").startsWith(NOT_ON_THIS_SYSTEM);
}

export function AppProvider({ children }: { children: ReactNode }) {
  const toast = useToast();
  const [info, setInfo] = useState<AppInfo | null>(null);
  const [modules, setModules] = useState<ModuleInfo[]>([]);

  useEffect(() => {
    call<AppInfo>("app.getInfo").then(setInfo).catch(() => {});
    call<AppSettings>("app.getSettings")
      .then((s) => setTempUnit(s.temperatureUnit === "C" ? "C" : "F"))
      .catch(() => {});
    call<ModuleInfo[]>("app.getModules").then(setModules).catch(() => {});
    const off = on("app.modulesChanged", setModules);
    const timer = window.setInterval(() => {
      call<ModuleInfo[]>("app.getModules").then(setModules).catch(() => {});
    }, 5000);
    return () => {
      off();
      window.clearInterval(timer);
    };
  }, []);

  const restartAsAdmin = useCallback(async () => {
    try {
      await call("app.restartAsAdmin");
    } catch (err) {
      if (errorCode(err) === "cancelled") toast.show("Administrator permission was not given, so nothing changed.", "error");
      else toast.show(errorMessage(err), "error");
    }
  }, [toast]);

  const openUrl = useCallback(
    (url: string) => {
      call("app.openExternal", { url }).catch((err) => toast.show(errorMessage(err), "error"));
    },
    [toast],
  );

  const can = useCallback(
    (capability: string) => !info || !info.capabilities || info.capabilities.includes(capability),
    [info],
  );
  const notWindows = !!info?.platform && info.platform !== "windows";

  const value = useMemo(
    () => ({ info, modules, restartAsAdmin, openUrl, can, notWindows }),
    [info, modules, restartAsAdmin, openUrl, can, notWindows],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useApp(): AppCtx {
  return useContext(Ctx);
}

/** The state of one backend module, or null while unknown. */
export function useModule(name: string): ModuleInfo | null {
  const { modules } = useApp();
  return modules.find((m) => m.name === name) ?? null;
}

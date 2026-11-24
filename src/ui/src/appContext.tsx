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
};

const Ctx = createContext<AppCtx>({
  info: null,
  modules: [],
  restartAsAdmin: async () => {},
  openUrl: () => {},
});

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

  const value = useMemo(() => ({ info, modules, restartAsAdmin, openUrl }), [info, modules, restartAsAdmin, openUrl]);
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

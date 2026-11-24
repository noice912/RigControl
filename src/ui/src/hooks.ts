import { useCallback, useEffect, useRef, useState } from "react";
import { call, errorMessage, on } from "./bridge";
import type { EventMap } from "./types";
import { useToast } from "./components/Toast";

/** Loads a value once (and whenever `deps` change). `reload` fetches it again. */
export function useQuery<T>(method: string, params?: unknown, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const seq = useRef(0);

  const reload = useCallback(async () => {
    const mine = ++seq.current;
    setLoading(true);
    try {
      const result = await call<T>(method, params);
      if (mine === seq.current) {
        setData(result);
        setError(null);
      }
    } catch (err) {
      if (mine === seq.current) setError(errorMessage(err));
    } finally {
      if (mine === seq.current) setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [method, JSON.stringify(params ?? null), ...deps]);

  useEffect(() => {
    void reload();
  }, [reload]);

  return { data, setData, error, loading, reload };
}

/** Latest value of a push event, seeded by an optional initial query. */
export function useLive<K extends keyof EventMap>(event: K, initialMethod?: string) {
  const [data, setData] = useState<EventMap[K] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const off = on(event, (d) => {
      if (alive) {
        setData(d);
        setError(null);
      }
    });
    if (initialMethod) {
      call<EventMap[K]>(initialMethod)
        .then((d) => {
          if (alive) setData((cur) => cur ?? d);
        })
        .catch((err) => {
          if (alive) setError(errorMessage(err));
        });
    }
    return () => {
      alive = false;
      off();
    };
  }, [event, initialMethod]);

  return { data, setData, error };
}

/**
 * Wraps a backend action: tracks "busy", shows a toast on failure (and optionally on success),
 * and returns the result or undefined.
 */
export function useAction() {
  const toast = useToast();
  const [busy, setBusy] = useState<string | null>(null);

  const run = useCallback(
    async <T>(key: string, fn: () => Promise<T>, success?: string): Promise<T | undefined> => {
      setBusy(key);
      try {
        const result = await fn();
        if (success) toast.show(success, "ok");
        return result;
      } catch (err) {
        toast.show(errorMessage(err), "error");
        return undefined;
      } finally {
        setBusy((b) => (b === key ? null : b));
      }
    },
    [toast],
  );

  return { busy, run };
}

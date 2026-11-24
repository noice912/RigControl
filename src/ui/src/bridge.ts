import type { EventMap } from "./types";

/** An error returned by the backend. `code` is one of the codes in docs/API.md. */
export class RpcError extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
    this.name = "RpcError";
  }
}

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
};

declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}

type Pending = { resolve: (v: unknown) => void; reject: (e: unknown) => void; timer: number };

const pending = new Map<number, Pending>();
const listeners = new Map<string, Set<(data: unknown) => void>>();
let nextId = 1;
let mockBackend: null | {
  call(method: string, params: unknown): Promise<unknown>;
  subscribe(emit: (event: string, data: unknown) => void): void;
} = null;

const webview: WebView | undefined = typeof window !== "undefined" ? window.chrome?.webview : undefined;

/** True when running inside RigControl.exe; false in a plain browser (development preview with fake data). */
export const isNative = webview !== undefined;

function dispatch(event: string, data: unknown) {
  const set = listeners.get(event);
  if (!set) return;
  for (const fn of [...set]) {
    try {
      fn(data);
    } catch (err) {
      console.error(`Listener for ${event} failed`, err);
    }
  }
}

if (webview) {
  webview.addEventListener("message", (e) => {
    const msg = e.data as Record<string, unknown> | null;
    if (!msg || typeof msg !== "object") return;
    if (typeof msg.event === "string") {
      dispatch(msg.event, msg.data);
      return;
    }
    const id = typeof msg.id === "number" ? msg.id : NaN;
    const p = pending.get(id);
    if (!p) return;
    pending.delete(id);
    window.clearTimeout(p.timer);
    if (msg.ok === true) {
      p.resolve(msg.result);
    } else {
      const err = (msg.error ?? {}) as { code?: string; message?: string };
      p.reject(new RpcError(err.code ?? "internal", err.message ?? "Something went wrong."));
    }
  });
}

async function ensureMock() {
  if (mockBackend) return mockBackend;
  const mod = await import("./mock");
  mockBackend = mod.createMockBackend();
  mockBackend.subscribe(dispatch);
  return mockBackend;
}

/** Calls a backend method. Rejects with RpcError on failure or after `timeoutMs`. */
export function call<T>(method: string, params?: unknown, timeoutMs = 20000): Promise<T> {
  if (!webview) {
    return ensureMock().then((m) => m.call(method, params) as Promise<T>);
  }
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      pending.delete(id);
      reject(new RpcError("timeout", "RigControl did not answer in time. Try again."));
    }, timeoutMs);
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
    try {
      webview.postMessage({ id, method, params: params ?? null });
    } catch (err) {
      pending.delete(id);
      window.clearTimeout(timer);
      reject(new RpcError("internal", String(err)));
    }
  });
}

/** Subscribes to a push event. Returns an unsubscribe function. */
export function on<K extends keyof EventMap>(event: K, handler: (data: EventMap[K]) => void): () => void {
  let set = listeners.get(event);
  if (!set) {
    set = new Set();
    listeners.set(event, set);
  }
  const fn = handler as (data: unknown) => void;
  set.add(fn);
  if (!webview) void ensureMock();
  return () => {
    set!.delete(fn);
  };
}

/** Human-readable message for any thrown value. */
export function errorMessage(err: unknown): string {
  if (err instanceof RpcError) return err.message;
  if (err instanceof Error) return err.message;
  return String(err);
}

export function errorCode(err: unknown): string | null {
  return err instanceof RpcError ? err.code : null;
}

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

/** The Windows app: WebView2's message channel. Messages are objects. */
type WebView = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
};

/** The Linux and macOS app: the window's message channel. Messages are text, so they travel as JSON. */
type TextChannel = {
  sendMessage(message: string): void;
  receiveMessage(callback: (message: string) => void): void;
};

declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}

type Pending = { resolve: (v: unknown) => void; reject: (e: unknown) => void; timer: number; method: string };

const pending = new Map<number, Pending>();
const listeners = new Map<string, Set<(data: unknown) => void>>();
let nextId = 1;
let mockBackend: null | {
  call(method: string, params: unknown): Promise<unknown>;
  subscribe(emit: (event: string, data: unknown) => void): void;
} = null;

/**
 * What the interface is talking to:
 *  - "webview2": the Windows app;
 *  - "photino":  the Linux and macOS app;
 *  - "mock":     no app at all, a developer's browser preview with made-up data (never in a packaged build);
 *  - "none":     a packaged build that found no app to talk to. It shows an error page, never made-up data.
 */
export type HostKind = "webview2" | "photino" | "mock" | "none";

const webview: WebView | undefined = typeof window !== "undefined" ? window.chrome?.webview : undefined;

function findTextChannel(): TextChannel | undefined {
  if (typeof window === "undefined") return undefined;
  // Browsers have their own, older window.external without these two functions.
  const ext = (window as unknown as { external?: Partial<TextChannel> }).external;
  return ext && typeof ext.sendMessage === "function" && typeof ext.receiveMessage === "function"
    ? (ext as TextChannel)
    : undefined;
}

// WebView2 is looked for first, so the Windows app behaves exactly as it always has.
const textChannel: TextChannel | undefined = webview ? undefined : findTextChannel();

/**
 * The Linux and macOS app only. Its window hands over messages from whatever page it is showing and cannot say
 * which page sent them, so the app writes a value made for this run into its own page (a meta tag) and reads only
 * messages that start with it. Another page that ends up in the window does not have the value.
 */
function findPageKey(): string {
  if (typeof document === "undefined") return "";
  return document.querySelector('meta[name="rigcontrol-page-key"]')?.getAttribute("content") ?? "";
}

const pageKey = textChannel ? findPageKey() : "";

/** Sends one message on the text channel, marked as coming from RigControl's own page. */
function sendText(message: unknown) {
  textChannel!.sendMessage(pageKey + JSON.stringify(message));
}

/** Made-up data is only ever loaded by the developer preview ("npm run dev") or a build made on purpose with VITE_RIGCONTROL_MOCK=1. */
const mockAllowed = import.meta.env.DEV || import.meta.env.VITE_RIGCONTROL_MOCK === "1";

export const hostKind: HostKind = webview ? "webview2" : textChannel ? "photino" : mockAllowed ? "mock" : "none";

/** True when running inside a real RigControl app; false in a plain browser. */
export const isNative = hostKind === "webview2" || hostKind === "photino";

const NO_HOST = "RigControl's window did not connect.";

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

/** One message from the app: a pushed event or the answer to a call. */
function receive(raw: unknown) {
  const msg = raw as Record<string, unknown> | null;
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
    // The Linux and macOS app only: say that the answer to app.getInfo arrived. The app's start-up check
    // ("--smoke") waits for this note, because it is the only proof that messages from the app reach the page.
    if (textChannel && p.method === "app.getInfo") {
      try {
        sendText({ note: "answered", id });
      } catch {
        // the window is closing
      }
    }
    p.resolve(msg.result);
  } else {
    const err = (msg.error ?? {}) as { code?: string; message?: string };
    p.reject(new RpcError(err.code ?? "internal", err.message ?? "Something went wrong."));
  }
}

if (webview) {
  webview.addEventListener("message", (e) => receive(e.data));
} else if (textChannel) {
  // The Linux and macOS app sends nothing to a page that has not said "still here" for a few seconds, because the
  // window cannot tell whether the page it is showing is still RigControl's. Same interval as PageAliveEvery.
  const alive = () => {
    try {
      sendText({ note: "alive" });
    } catch {
      // the window is closing
    }
  };
  window.setInterval(alive, 2000);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") alive();
  });
  textChannel.receiveMessage((text) => {
    let msg: unknown;
    try {
      msg = JSON.parse(text);
    } catch {
      return; // not one of ours
    }
    receive(msg);
  });
}

/** Hands one request to the app. Only called when there is one. */
function post(message: { id: number; method: string; params: unknown }) {
  if (webview) webview.postMessage(message);
  else sendText(message);
}

async function ensureMock() {
  if (mockBackend) return mockBackend;
  // Written out in full here so a packaged build leaves the made-up data out altogether.
  if (import.meta.env.DEV || import.meta.env.VITE_RIGCONTROL_MOCK === "1") {
    const mod = await import("./mock");
    mockBackend = mod.createMockBackend();
    mockBackend.subscribe(dispatch);
    return mockBackend;
  }
  throw new RpcError("noHost", NO_HOST);
}

/** Calls a backend method. Rejects with RpcError on failure or after `timeoutMs`. */
export function call<T>(method: string, params?: unknown, timeoutMs = 20000): Promise<T> {
  if (hostKind === "none") return Promise.reject(new RpcError("noHost", NO_HOST));
  if (hostKind === "mock") {
    return ensureMock().then((m) => m.call(method, params) as Promise<T>);
  }
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      pending.delete(id);
      reject(new RpcError("timeout", "RigControl did not answer in time. Try again."));
    }, timeoutMs);
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer, method });
    try {
      post({ id, method, params: params ?? null });
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
  if (hostKind === "mock") void ensureMock().catch(() => undefined);
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

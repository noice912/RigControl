// Constants and shared helpers of the relay part of docs/protocols/link.md.

export const SIG_PREFIX = "rigcontrol-link-v1|";

/** Text signed in hello: 'rigcontrol-link-v1|' + nonce (as sent, base64url) + '|' + room. */
export const helloText = (nonce: string, room: string) => `${SIG_PREFIX}${nonce}|${room}`;

/** Text signed in allow: 'rigcontrol-link-v1|allow|' + room + '|' + ids joined by ',' (in the order sent). */
export const allowText = (room: string, phones: readonly string[]) => `${SIG_PREFIX}allow|${room}|${phones.join(",")}`;

export const MAX_FRAME_BYTES = 64 * 1024; // 65536 bytes of UTF-8, inclusive
export const FRAMES_PER_SECOND = 20;
export const MAX_EXCESS_FRAMES = 100;
export const FIRST_FRAME_TIMEOUT_MS = 10_000; // hello (connect) / offer or claim (pair) must arrive within this
export const ROOM_IDLE_MS = 90 * 24 * 60 * 60 * 1000;
export const PAIR_TTL_MS = 10 * 60 * 1000;
/** A used or expired h stays refused this long after its first offer (then the object is deleted). */
export const PAIR_TOMBSTONE_MS = 24 * 60 * 60 * 1000;
/** Most phone ids in one allow-list. */
export const MAX_PHONES = 32;
// The per-address upgrade limits (30 upgrades / 60 s; 10 pair upgrades / 60 s) live in wrangler.jsonc ("ratelimits").

export type ErrorCode =
  | "badmsg" // not JSON / not an object / unknown "t" / a field missing or of the wrong form / binary frame
  | "badkey" // hello.pub is not an uncompressed P-256 SPKI
  | "badsig" // hello or allow signature does not check
  | "badroom" // hello.room differs from ?room=, or a PC's id(pub) is not the room
  | "denied" // phone: room unknown or id(pub) not on the allow-list
  | "notallowed" // after ok: a phone sent "allow", or a "to" the sender may not use
  | "removed" // phone taken off the allow-list
  | "replaced" // the same id (or the same pairing offer) connected again; this older connection is closed
  | "timeout" // no first frame within 10 s
  | "toolarge" // frame over 64 KiB
  | "ratelimit" // 100 frames over the 20/s limit
  | "nopair" // claim/offer for an unknown, expired, withdrawn or used h; an offer with another owner
  | "expired"; // the pairing offer reached its 10 minutes

export const CLOSE = {
  normal: 1000,
  binary: 1003,
  policy: 1008,
  tooBig: 1009,
  replaced: 4000,
  removed: 4001,
} as const;

/** Per-connection rate state, kept in the WebSocket attachment so it survives hibernation. */
export interface RateState {
  /** start of the current 1-second window (ms) */
  rw: number;
  /** frames counted in that window */
  rn: number;
  /** excess frames over the whole life of the connection */
  rx: number;
}

export const newRate = (): RateState => ({ rw: 0, rn: 0, rx: 0 });

/**
 * Counts one incoming frame. Fixed 1-second windows starting at the first frame after the previous window ended.
 * Returns "ok", "drop" (excess frame, silently dropped) or "close" (the 100th excess frame).
 */
export function countFrame(r: RateState, now: number): "ok" | "drop" | "close" {
  if (now - r.rw >= 1000) {
    r.rw = now;
    r.rn = 0;
  }
  r.rn++;
  if (r.rn <= FRAMES_PER_SECOND) return "ok";
  r.rx++;
  return r.rx >= MAX_EXCESS_FRAMES ? "close" : "drop";
}

const utf8 = new TextEncoder();

export function fits(s: string): boolean {
  // s.length counts UTF-16 units; each is at most 3 UTF-8 bytes, so short strings need no encoding.
  if (s.length * 3 <= MAX_FRAME_BYTES) return true;
  if (s.length > MAX_FRAME_BYTES) return false;
  return utf8.encode(s).length <= MAX_FRAME_BYTES;
}

/** Parses a text frame into a JSON object, or null. */
export function parseObject(text: string): Record<string, unknown> | null {
  try {
    const v: unknown = JSON.parse(text);
    return v !== null && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** Sends a frame, ignoring a socket that is already gone. */
export function sendJson(ws: WebSocket, value: unknown): void {
  try {
    ws.send(JSON.stringify(value));
  } catch {
    /* closed */
  }
}

/** Sends {"t":"error","code":...} and closes with the given close code (reason = the error code). */
export function fail(ws: WebSocket, code: ErrorCode, closeCode: number): void {
  sendJson(ws, { t: "error", code });
  try {
    ws.close(closeCode, code);
  } catch {
    /* closed */
  }
}

/**
 * Runs the frames of one socket one after another, so a frame that arrives while an earlier one is still being
 * handled (for example during the hello's signature check) waits for it, without holding up the other sockets.
 * In memory only: nothing hibernates while a frame is being handled.
 */
export class SocketQueue {
  private readonly tails = new Map<WebSocket, Promise<void>>();

  run(ws: WebSocket, fn: () => Promise<void>): Promise<void> {
    const prev = this.tails.get(ws) ?? Promise.resolve();
    const next = prev.then(fn, fn);
    this.tails.set(ws, next);
    const clear = () => {
      if (this.tails.get(ws) === next) this.tails.delete(ws);
    };
    next.then(clear, clear);
    return next;
  }
}

/** Sets the alarm to `at` only when none is set or `at` is earlier, so most connections write nothing. */
export async function alarmNoLaterThan(storage: DurableObjectStorage, at: number): Promise<void> {
  const current = await storage.getAlarm();
  if (current === null || at < current) await storage.setAlarm(at);
}

/** Sets the alarm to exactly `at` (Infinity = no alarm). Used in the alarm handler, which recomputes it. */
export async function alarmAt(storage: DurableObjectStorage, at: number): Promise<void> {
  if (at === Infinity) await storage.deleteAlarm();
  else await storage.setAlarm(at);
}

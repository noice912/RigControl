import { DurableObject } from "cloudflare:workers";
import { isB64uText, isId } from "./b64u";
import { importSigningKey, randomNonce, verifySignature, type PublicKey } from "./crypto";
import {
  CLOSE,
  FIRST_FRAME_TIMEOUT_MS,
  MAX_PHONES,
  ROOM_IDLE_MS,
  SocketQueue,
  alarmAt,
  alarmNoLaterThan,
  allowText,
  countFrame,
  fail,
  fits,
  helloText,
  newRate,
  parseObject,
  sendJson,
  type ErrorCode,
  type RateState,
} from "./protocol";

// One Durable Object per room. Stored (and nothing else): "pc" = the PC's signing key (base64url SPKI),
// "allow" = the allow-list (phone ids, as last signed), "lastSeen" = ms of the last hello or disconnect.
// Messages pass through memory only.

interface Attachment extends RateState {
  /** wait = challenge sent, hello not yet accepted; ok = accepted; done = closed or being closed */
  s: "wait" | "ok" | "done";
  room: string;
  nonce: string;
  /** hello deadline (wait only) */
  dl: number;
  role?: "pc" | "phone";
  id?: string;
}

const att = (ws: WebSocket) => ws.deserializeAttachment() as Attachment;

export class Room extends DurableObject<Env> {
  private pcKey: CryptoKey | null = null;
  private readonly queue = new SocketQueue();

  override async fetch(request: Request): Promise<Response> {
    const room = new URL(request.url).searchParams.get("room") ?? "";
    const { 0: client, 1: server } = new WebSocketPair();
    this.ctx.acceptWebSocket(server);
    const nonce = randomNonce();
    const a: Attachment = { s: "wait", room, nonce, dl: Date.now() + FIRST_FRAME_TIMEOUT_MS, ...newRate() };
    server.serializeAttachment(a);
    sendJson(server, { t: "challenge", nonce });
    await alarmNoLaterThan(this.ctx.storage, a.dl);
    return new Response(null, { status: 101, webSocket: client });
  }

  override webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    // One frame of a socket at a time: frames sent right behind the hello are handled after it, in order.
    return this.queue.run(ws, () => this.handle(ws, message));
  }

  private async handle(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const a = att(ws);
    if (a.s === "done") return;
    const now = Date.now();
    if (a.s === "wait" && now >= a.dl) return this.kill(ws, a, "timeout", CLOSE.policy);
    const rate = countFrame(a, now);
    ws.serializeAttachment(a);
    if (rate === "drop") return;
    if (rate === "close") return this.kill(ws, a, "ratelimit", CLOSE.policy);
    if (typeof message !== "string") return this.kill(ws, a, "badmsg", CLOSE.binary);
    if (!fits(message)) return this.kill(ws, a, "toolarge", CLOSE.tooBig);
    const m = parseObject(message);

    if (a.s === "wait") {
      if (!m || m.t !== "hello") return this.kill(ws, a, "badmsg", CLOSE.policy);
      return this.hello(ws, a, m);
    }

    if (!m) return sendJson(ws, { t: "error", code: "badmsg" });
    switch (m.t) {
      case "msg":
        return this.forward(ws, a, m);
      case "allow":
        if (a.role !== "pc") return sendJson(ws, { t: "error", code: "notallowed" });
        return this.allow(ws, a, m);
      default:
        return sendJson(ws, { t: "error", code: "badmsg" });
    }
  }

  override async webSocketClose(ws: WebSocket): Promise<void> {
    try {
      ws.close(1000, "");
    } catch {
      /* already closed */
    }
    await this.left(ws);
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    await this.left(ws);
  }

  override async alarm(): Promise<void> {
    // The alarm is set "no later than" what is needed, so it can run early (at a hello deadline, or 90 days after
    // an older lastSeen); it then works out the next time exactly.
    const now = Date.now();
    for (const ws of this.ctx.getWebSockets()) {
      const a = att(ws);
      if (a.s === "wait" && a.dl <= now) this.kill(ws, a, "timeout", CLOSE.policy);
    }
    if (await this.ctx.storage.get<string>("pc")) {
      if (this.live().length > 0) {
        await this.ctx.storage.put("lastSeen", now);
      } else {
        const lastSeen = (await this.ctx.storage.get<number>("lastSeen")) ?? 0;
        if (now >= lastSeen + ROOM_IDLE_MS) {
          await this.ctx.storage.deleteAll();
          this.pcKey = null;
        }
      }
    }
    await alarmAt(this.ctx.storage, await this.nextAlarm());
  }

  // ------------------------------------------------------------------ hello

  private async hello(ws: WebSocket, a: Attachment, m: Record<string, unknown>): Promise<void> {
    if ((m.role !== "pc" && m.role !== "phone") || typeof m.room !== "string" || typeof m.pub !== "string" || typeof m.sig !== "string")
      return this.kill(ws, a, "badmsg", CLOSE.policy);
    if (m.room !== a.room) return this.kill(ws, a, "badroom", CLOSE.policy);
    // The crypto runs outside blockConcurrencyWhile, so a flood of hellos cannot hold up the room; this socket's
    // later frames wait in its queue.
    const pub = await importSigningKey(m.pub);
    if (!pub) return this.kill(ws, a, "badkey", CLOSE.policy);
    if (!(await verifySignature(pub.key, m.sig, helloText(a.nonce, a.room)))) return this.kill(ws, a, "badsig", CLOSE.policy);
    if (m.role === "pc" && pub.id !== a.room) return this.kill(ws, a, "badroom", CLOSE.policy);
    const role = m.role;
    const pubText = m.pub;
    return this.ctx.blockConcurrencyWhile(() => this.accept(ws, role, pubText, pub));
  }

  /** Storage part of the hello (inside blockConcurrencyWhile). */
  private async accept(ws: WebSocket, role: "pc" | "phone", pubText: string, pub: PublicKey): Promise<void> {
    const a = att(ws);
    if (a.s !== "wait") return; // timed out or closed meanwhile
    const storage = this.ctx.storage;
    const storedPc = await storage.get<string>("pc");
    if (role === "pc") {
      if (!storedPc) {
        // First PC hello: the room is created.
        await storage.put({ pc: pubText, allow: [] as string[] });
      } else if (storedPc !== pubText) {
        return this.kill(ws, a, "badroom", CLOSE.policy); // cannot happen with canonical SPKI; kept as a guard
      }
      this.pcKey = pub.key;
    } else {
      const allow = storedPc ? ((await storage.get<string[]>("allow")) ?? []) : [];
      if (!allow.includes(pub.id)) return this.kill(ws, a, "denied", CLOSE.policy);
    }
    if (att(ws).s !== "wait") return; // closed meanwhile

    // Accepted. An older connection of the same device is closed (newest wins).
    let wasOnline = false;
    for (const other of this.live()) {
      const o = att(other);
      if (o.id === pub.id && other !== ws) {
        wasOnline = true;
        this.kill(other, o, "replaced", CLOSE.replaced, false);
      }
    }
    a.s = "ok";
    a.role = role;
    a.id = pub.id;
    ws.serializeAttachment(a);
    const seenAt = Date.now();
    await storage.put("lastSeen", seenAt);
    sendJson(ws, { t: "ok" });

    if (a.role === "pc") {
      const seen = new Set<string>();
      for (const p of this.live()) {
        const o = att(p);
        if (o.role === "phone" && o.id && !seen.has(o.id)) {
          seen.add(o.id);
          sendJson(ws, { t: "presence", id: o.id, online: true });
        }
      }
    } else if (!wasOnline) {
      const pc = this.socketOf(a.room);
      if (pc) sendJson(pc, { t: "presence", id: pub.id, online: true });
    }
    await alarmNoLaterThan(storage, seenAt + ROOM_IDLE_MS);
  }

  // ------------------------------------------------------------------ allow

  private async allow(ws: WebSocket, a: Attachment, m: Record<string, unknown>): Promise<void> {
    const phones = m.phones;
    if (
      !Array.isArray(phones) ||
      phones.length > MAX_PHONES ||
      !phones.every(isId) ||
      new Set(phones).size !== phones.length ||
      phones.includes(a.room)
    )
      return sendJson(ws, { t: "error", code: "badmsg" });
    const key = await this.loadPcKey();
    if (!key || !(await verifySignature(key, m.sig, allowText(a.room, phones)))) return sendJson(ws, { t: "error", code: "badsig" });
    return this.ctx.blockConcurrencyWhile(() => this.storeAllow(ws, phones));
  }

  /** Storage part of allow (inside blockConcurrencyWhile). */
  private async storeAllow(ws: WebSocket, phones: string[]): Promise<void> {
    if (att(ws).s !== "ok") return; // replaced or closed meanwhile
    await this.ctx.storage.put("allow", phones);
    const keep = new Set(phones);
    for (const p of this.live()) {
      const o = att(p);
      if (o.role === "phone" && o.id && !keep.has(o.id)) {
        this.kill(p, o, "removed", CLOSE.removed, false);
        sendJson(ws, { t: "presence", id: o.id, online: false });
      }
    }
  }

  // ------------------------------------------------------------------ msg

  private async forward(ws: WebSocket, a: Attachment, m: Record<string, unknown>): Promise<void> {
    const to = m.to;
    if (!isId(to) || !isB64uText(m.body)) return sendJson(ws, { t: "error", code: "badmsg" });
    if (a.role === "phone") {
      if (to !== a.room) return sendJson(ws, { t: "error", code: "notallowed" });
    } else {
      const allow = (await this.ctx.storage.get<string[]>("allow")) ?? [];
      if (!allow.includes(to)) return sendJson(ws, { t: "error", code: "notallowed" });
    }
    const target = this.socketOf(to);
    if (!target) return sendJson(ws, { t: "gone", to });
    const out = JSON.stringify({ t: "msg", from: a.id, body: m.body });
    if (!fits(out)) return sendJson(ws, { t: "error", code: "toolarge" });
    try {
      target.send(out);
    } catch {
      sendJson(ws, { t: "gone", to });
    }
  }

  // ------------------------------------------------------------------ helpers

  /** Accepted connections. */
  private live(): WebSocket[] {
    return this.ctx.getWebSockets().filter((ws) => att(ws).s === "ok");
  }

  private socketOf(id: string): WebSocket | undefined {
    return this.live().find((ws) => att(ws).id === id);
  }

  private async loadPcKey(): Promise<CryptoKey | null> {
    if (this.pcKey) return this.pcKey;
    const pub = await importSigningKey(await this.ctx.storage.get<string>("pc"));
    this.pcKey = pub?.key ?? null;
    return this.pcKey;
  }

  /**
   * Sends an error and closes. With `announce`, an accepted phone that goes away is reported to the PC (callers
   * that report it themselves, or replace the connection, pass false).
   */
  private kill(ws: WebSocket, a: Attachment, code: ErrorCode, closeCode: number, announce = true): void {
    const wasOk = a.s === "ok";
    a.s = "done";
    ws.serializeAttachment(a);
    fail(ws, code, closeCode);
    if (wasOk && announce) this.departed(a);
  }

  /** Called when the client closed or the socket failed. */
  private async left(ws: WebSocket): Promise<void> {
    const a = att(ws);
    const wasOk = a.s === "ok";
    a.s = "done";
    ws.serializeAttachment(a);
    if (wasOk) {
      this.departed(a);
      const now = Date.now();
      await this.ctx.storage.put("lastSeen", now);
      await alarmNoLaterThan(this.ctx.storage, now + ROOM_IDLE_MS);
    }
  }

  private departed(a: Attachment): void {
    if (a.role !== "phone" || !a.id) return;
    if (this.socketOf(a.id)) return; // a newer connection of the same phone is still here
    const pc = this.socketOf(a.room);
    if (pc) sendJson(pc, { t: "presence", id: a.id, online: false });
  }

  /** The next alarm: the earliest open hello deadline, else the idle expiry of a room that exists. */
  private async nextAlarm(): Promise<number> {
    let next = Infinity;
    for (const ws of this.ctx.getWebSockets()) {
      const a = att(ws);
      if (a.s === "wait") next = Math.min(next, a.dl);
    }
    if (await this.ctx.storage.get<string>("pc")) {
      const lastSeen = (await this.ctx.storage.get<number>("lastSeen")) ?? Date.now();
      next = Math.min(next, lastSeen + ROOM_IDLE_MS);
    }
    return next;
  }
}

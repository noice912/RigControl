import { DurableObject } from "cloudflare:workers";
import { b64uDecode, b64uEncode, isB64uText, isId } from "./b64u";
import {
  CLOSE,
  FIRST_FRAME_TIMEOUT_MS,
  PAIR_TOMBSTONE_MS,
  PAIR_TTL_MS,
  SocketQueue,
  alarmAt,
  alarmNoLaterThan,
  countFrame,
  fail,
  fits,
  newRate,
  parseObject,
  sendJson,
  type ErrorCode,
  type RateState,
} from "./protocol";

// One Durable Object per pairing rendezvous, named by h. Stored:
//   "first" = ms of the first offer (the 10 minutes run from it; kept 24 h as a tombstone),
//   "owner" = base64url SHA-256 of the owner secret the first offer carried (until used or expired),
//   "box"   = the offer box, only while the PC's offer connection is open,
//   "used"  = a claim succeeded (kept 24 h as a tombstone).
// The boxes are sealed with a key the relay never sees. 10 minutes after the first offer the open offer is closed
// and owner/box are deleted; 24 hours after it everything is deleted.

interface Attachment extends RateState {
  /** wait = nothing received yet; offer = this is the PC's open offer; done = closed or being closed */
  s: "wait" | "offer" | "done";
  h: string;
  dl: number;
}

const att = (ws: WebSocket) => ws.deserializeAttachment() as Attachment;

async function ownerHash(owner: string): Promise<string> {
  return b64uEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", b64uDecode(owner)!)));
}

export class Pairing extends DurableObject<Env> {
  private readonly queue = new SocketQueue();

  override async fetch(request: Request): Promise<Response> {
    const h = new URL(request.url).searchParams.get("h") ?? "";
    const { 0: client, 1: server } = new WebSocketPair();
    this.ctx.acceptWebSocket(server);
    const a: Attachment = { s: "wait", h, dl: Date.now() + FIRST_FRAME_TIMEOUT_MS, ...newRate() };
    server.serializeAttachment(a);
    await alarmNoLaterThan(this.ctx.storage, a.dl);
    return new Response(null, { status: 101, webSocket: client });
  }

  override webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    return this.queue.run(ws, () => this.handle(ws, message));
  }

  private async handle(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const a = att(ws);
    if (a.s === "done") return;
    const now = Date.now();
    if (a.s === "wait" && now >= a.dl) return this.kill(ws, "timeout", CLOSE.policy);
    const rate = countFrame(a, now);
    ws.serializeAttachment(a);
    if (rate === "drop") return;
    if (rate === "close") return this.kill(ws, "ratelimit", CLOSE.policy);
    if (typeof message !== "string") return this.kill(ws, "badmsg", CLOSE.binary);
    if (!fits(message)) return this.kill(ws, "toolarge", CLOSE.tooBig);
    const m = parseObject(message);
    // After its offer the PC only waits; any frame from it, or a frame that is not a valid offer/claim, ends it.
    if (a.s !== "wait" || !m || (m.t !== "offer" && m.t !== "claim") || m.h !== a.h || !isB64uText(m.box))
      return this.kill(ws, "badmsg", CLOSE.policy);
    const box = m.box;
    if (m.t === "offer") {
      if (!isId(m.owner)) return this.kill(ws, "badmsg", CLOSE.policy);
      const owner = await ownerHash(m.owner);
      return this.ctx.blockConcurrencyWhile(() => this.offer(ws, box, owner));
    }
    // Claims are limited per address in the Worker (each pair connection carries at most one claim).
    return this.ctx.blockConcurrencyWhile(() => this.claim(ws, box));
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
    const now = Date.now();
    const storage = this.ctx.storage;
    let next = Infinity;
    for (const ws of this.ctx.getWebSockets()) {
      const a = att(ws);
      if (a.s !== "wait") continue;
      if (a.dl <= now) this.kill(ws, "timeout", CLOSE.policy);
      else next = Math.min(next, a.dl);
    }
    const first = await storage.get<number>("first");
    if (first !== undefined) {
      if (now >= first + PAIR_TOMBSTONE_MS) {
        await storage.deleteAll();
      } else if (now >= first + PAIR_TTL_MS) {
        for (const ws of this.ctx.getWebSockets()) if (att(ws).s === "offer") this.kill(ws, "expired", CLOSE.policy);
        await storage.delete(["box", "owner"]);
        next = Math.min(next, first + PAIR_TOMBSTONE_MS);
      } else {
        next = Math.min(next, first + PAIR_TTL_MS);
      }
    }
    await alarmAt(storage, next);
  }

  private async offer(ws: WebSocket, box: string, owner: string): Promise<void> {
    const a = att(ws);
    if (a.s !== "wait") return; // timed out or closed meanwhile
    const storage = this.ctx.storage;
    const now = Date.now();
    const first = await storage.get<number>("first");
    if ((await storage.get<boolean>("used")) || (first !== undefined && now >= first + PAIR_TTL_MS))
      return this.kill(ws, "nopair", CLOSE.policy);
    // Only the PC that made the first offer (it knows the owner secret) may offer this h again; an open offer of
    // that PC is left alone otherwise.
    const storedOwner = await storage.get<string>("owner");
    if (storedOwner !== undefined && storedOwner !== owner) return this.kill(ws, "nopair", CLOSE.policy);
    // The same owner again (the PC reconnected) replaces its older offer; the 10 minutes keep running.
    for (const other of this.ctx.getWebSockets()) if (other !== ws && att(other).s === "offer") this.kill(other, "replaced", CLOSE.replaced);
    if (first === undefined) await storage.put({ first: now, owner, box });
    else await storage.put("box", box);
    a.s = "offer";
    ws.serializeAttachment(a);
    sendJson(ws, { t: "ok" });
    await alarmNoLaterThan(storage, (first ?? now) + PAIR_TTL_MS);
  }

  private async claim(ws: WebSocket, claimBox: string): Promise<void> {
    const a = att(ws);
    if (a.s !== "wait") return; // timed out or closed meanwhile
    const storage = this.ctx.storage;
    const first = await storage.get<number>("first");
    const offerBox = await storage.get<string>("box");
    const pc = this.ctx.getWebSockets().find((s) => att(s).s === "offer");
    if (!pc || offerBox === undefined || first === undefined || Date.now() >= first + PAIR_TTL_MS || (await storage.get<boolean>("used")))
      return this.kill(ws, "nopair", CLOSE.policy);

    sendJson(ws, { t: "offer", h: a.h, box: offerBox });
    sendJson(pc, { t: "claim", h: a.h, box: claimBox });
    await storage.delete(["box", "owner"]);
    await storage.put("used", true);
    for (const s of [ws, pc]) {
      const o = att(s);
      o.s = "done";
      s.serializeAttachment(o);
      try {
        s.close(CLOSE.normal, "paired");
      } catch {
        /* closed */
      }
    }
    // The alarm set by the offer (first + 10 min) moves on to the 24-hour tombstone when it runs.
  }

  private kill(ws: WebSocket, code: ErrorCode, closeCode: number): void {
    const a = att(ws);
    a.s = "done";
    ws.serializeAttachment(a);
    fail(ws, code, closeCode);
  }

  private async left(ws: WebSocket): Promise<void> {
    const a = att(ws);
    const wasOffer = a.s === "offer";
    a.s = "done";
    ws.serializeAttachment(a);
    // The offer lives only while the PC's connection is open.
    if (wasOffer) await this.ctx.storage.delete("box");
  }
}

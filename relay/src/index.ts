import { addressKey } from "./address";
import { isId } from "./b64u";
import type { Pairing } from "./pairing";
import type { Room } from "./room";

export { Pairing } from "./pairing";
export { Room } from "./room";

declare global {
  namespace Cloudflare {
    interface Env {
      ROOMS: DurableObjectNamespace<Room>;
      PAIRS: DurableObjectNamespace<Pairing>;
      /** Workers Rate Limiting: WebSocket upgrades (/v1/connect and /v1/pair) per address, 30 / 60 s. */
      UPGRADE_LIMIT: RateLimit;
      /** Workers Rate Limiting: /v1/pair upgrades per address, 10 / 60 s (one pair connection = one offer or claim). */
      PAIR_LIMIT: RateLimit;
    }
  }
  interface Env extends Cloudflare.Env {}
}

const text = (status: number, body: string) => new Response(body, { status, headers: { "content-type": "text/plain" } });

/**
 * wss://<relay>/v1/connect?room=<room>  -> the room's object (the room must be in the URL so the connection can
 *                                          be handed to the right object before the hello).
 * wss://<relay>/v1/pair?h=<h>           -> the rendezvous object for h.
 *
 * Everything that can be refused without a Durable Object is refused here, before ns.get(), so refused requests
 * create no object and write no storage.
 */
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    let ns: DurableObjectNamespace<Room> | DurableObjectNamespace<Pairing>;
    let key: string | null;
    const pair = url.pathname === "/v1/pair";
    if (url.pathname === "/v1/connect") {
      ns = env.ROOMS;
      key = url.searchParams.get("room");
    } else if (pair) {
      ns = env.PAIRS;
      key = url.searchParams.get("h");
    } else {
      return text(404, "not found");
    }
    if (request.method !== "GET") return text(405, "use GET with a WebSocket upgrade");
    if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return text(426, "WebSocket upgrade required");
    // Native clients send no Origin; a browser always does. No web page may talk to the relay.
    if (request.headers.has("origin")) return text(403, "browser connections are not accepted");
    if (!isId(key)) return text(400, pair ? "missing or malformed ?h=" : "missing or malformed ?room=");
    const addr = addressKey(request.headers.get("cf-connecting-ip") ?? "");
    if (!(await env.UPGRADE_LIMIT.limit({ key: addr })).success) return text(429, "too many connections, try again in a minute");
    if (pair && !(await env.PAIR_LIMIT.limit({ key: addr })).success) return text(429, "too many pairing attempts, try again in a minute");
    return ns.get(ns.idFromName(key)).fetch(request);
  },
} satisfies ExportedHandler<Env>;

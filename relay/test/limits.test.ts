import { SELF, env, runInDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { addressKey } from "../src/address";
import worker from "../src/index";
import { Client, connect, freshAddress, newDevice, pcWithPhones, randomId } from "./helpers";

/** A rate-limit binding stand-in: allows `allow` calls, then refuses; records the keys it was asked about. */
function mockLimit(allow = Infinity) {
  const keys: string[] = [];
  const binding: RateLimit = {
    async limit({ key }) {
      keys.push(key);
      return { success: keys.length <= allow };
    },
  };
  return { binding, keys };
}

/** Namespaces that fail the test if a Durable Object is reached. */
const noObjects = {
  idFromName(): never {
    throw new Error("a Durable Object was reached");
  },
  get(): never {
    throw new Error("a Durable Object was reached");
  },
} as unknown as DurableObjectNamespace<never>;

function upgrade(path: string, ip: string, extra: Record<string, string> = {}) {
  return new Request(`https://relay.test${path}`, { headers: { Upgrade: "websocket", "CF-Connecting-IP": ip, ...extra } });
}

describe("upgrade limits (Workers Rate Limiting, checked before any Durable Object)", () => {
  it("refuses connect upgrades over the limit with 429 and reaches no object", async () => {
    const up = mockLimit(0);
    const pair = mockLimit();
    const testEnv = { ...env, ROOMS: noObjects, PAIRS: noObjects, UPGRADE_LIMIT: up.binding, PAIR_LIMIT: pair.binding };
    const res = await worker.fetch(upgrade(`/v1/connect?room=${randomId()}`, "192.0.2.7"), testEnv);
    expect(res.status).toBe(429);
    expect(up.keys).toEqual(["ip|192.0.2.7"]);
    expect(pair.keys).toEqual([]); // connect does not count as pairing
  });

  it("counts pair upgrades against both limits, by IPv6 /64", async () => {
    const up = mockLimit();
    const pair = mockLimit(0);
    const testEnv = { ...env, ROOMS: noObjects, PAIRS: noObjects, UPGRADE_LIMIT: up.binding, PAIR_LIMIT: pair.binding };
    const res = await worker.fetch(upgrade(`/v1/pair?h=${randomId()}`, "2001:db8:1:2:3:4:5:6"), testEnv);
    expect(res.status).toBe(429);
    expect(up.keys).toEqual(["ip6|2001:db8:1:2"]);
    expect(pair.keys).toEqual(["ip6|2001:db8:1:2"]);
  });

  it("lets an upgrade under the limits through to the object", async () => {
    const up = mockLimit();
    const pair = mockLimit();
    const res = await worker.fetch(upgrade(`/v1/pair?h=${randomId()}`, "198.51.100.9"), { ...env, UPGRADE_LIMIT: up.binding, PAIR_LIMIT: pair.binding });
    expect(res.status).toBe(101);
    res.webSocket?.accept();
    res.webSocket?.close(1000, "");
  });

  it("does not count requests refused for their form", async () => {
    const up = mockLimit();
    const pair = mockLimit();
    const testEnv = { ...env, ROOMS: noObjects, PAIRS: noObjects, UPGRADE_LIMIT: up.binding, PAIR_LIMIT: pair.binding };
    expect((await worker.fetch(upgrade("/v1/pair?h=abc", "192.0.2.8"), testEnv)).status).toBe(400);
    expect((await worker.fetch(upgrade(`/v1/connect?room=${randomId()}`, "192.0.2.8", { Origin: "https://example.com" }), testEnv)).status).toBe(403);
    expect(up.keys).toEqual([]);
  });

  it("uses the real bindings: the 11th pair upgrade from one address in a minute gets 429", async () => {
    // Local rate-limit windows are aligned to the wall clock; start well inside one.
    const intoMinute = Date.now() % 60_000;
    if (intoMinute > 45_000) await new Promise((r) => setTimeout(r, 60_000 - intoMinute + 100));
    const ip = freshAddress();
    const statuses: number[] = [];
    for (let i = 0; i < 11; i++) {
      const res = await SELF.fetch(`https://relay.test/v1/pair?h=${randomId()}`, { headers: { Upgrade: "websocket", "CF-Connecting-IP": ip } });
      statuses.push(res.status);
      res.webSocket?.accept();
      res.webSocket?.close(1000, "");
    }
    expect(statuses).toEqual([...Array(10).fill(101), 429]);
  }, 30_000);

  it("groups IPv6 addresses by /64", () => {
    expect(addressKey("2001:db8:1:2:3:4:5:6")).toBe("ip6|2001:db8:1:2");
    expect(addressKey("2001:0db8:0001:0002::9")).toBe("ip6|2001:db8:1:2");
    expect(addressKey("2001:db8::1")).toBe("ip6|2001:db8:0:0");
    expect(addressKey("192.0.2.7")).toBe("ip|192.0.2.7");
  });
});

describe("Origin", () => {
  it("refuses WebSocket upgrades that carry an Origin header with 403", async () => {
    for (const path of [`/v1/connect?room=${randomId()}`, `/v1/pair?h=${randomId()}`]) {
      for (const origin of ["https://evil.example", "null", "chrome-extension://abc"]) {
        const res = await SELF.fetch(`https://relay.test${path}`, { headers: { Upgrade: "websocket", Origin: origin } });
        expect(res.status).toBe(403);
      }
    }
  });
});

describe("allow-list size", () => {
  it("accepts 32 phones and refuses 33 (list unchanged)", async () => {
    const { pc, pcDev } = await pcWithPhones(1);
    const ids = Array.from({ length: 33 }, randomId);
    const sign = (list: string[]) => pcDev.sign(`rigcontrol-link-v1|allow|${pcDev.id}|${list.join(",")}`);
    pc.send({ t: "allow", phones: ids, sig: await sign(ids) });
    expect(await pc.next()).toEqual({ t: "error", code: "badmsg" });
    const stub = env.ROOMS.get(env.ROOMS.idFromName(pcDev.id));
    expect(((await runInDurableObject(stub, (_i, s) => s.storage.get<string[]>("allow"))) ?? []).length).toBe(1);
    const ok = ids.slice(0, 32);
    pc.send({ t: "allow", phones: ok, sig: await sign(ok) });
    pc.send({ t: "msg", to: ok[31], body: "AA" }); // round trip: gone (not connected) means the list was taken
    expect(await pc.next()).toEqual({ t: "gone", to: ok[31] });
    expect(await runInDurableObject(stub, (_i, s) => s.storage.get<string[]>("allow"))).toEqual(ok);
  });
});

describe("hello deadline", () => {
  it("refuses a hello that arrives after the 10 s, even before the alarm ran", async () => {
    const dev = await newDevice();
    const c = await Client.open(`/v1/connect?room=${dev.id}`);
    const ch = await c.next();
    const stub = env.ROOMS.get(env.ROOMS.idFromName(dev.id));
    await runInDurableObject(stub, (_i, state) => {
      for (const ws of state.getWebSockets()) ws.serializeAttachment({ ...ws.deserializeAttachment(), dl: Date.now() - 1 });
    });
    c.send({ t: "hello", role: "pc", room: dev.id, pub: dev.pub, sig: await dev.sign(`rigcontrol-link-v1|${ch.nonce}|${dev.id}`) });
    expect(await c.next()).toEqual({ t: "error", code: "timeout" });
    expect(await c.waitClosed()).toBe(1008);
    // the room was not created
    expect(await runInDurableObject(stub, (_i, s) => s.storage.get("pc"))).toBeUndefined();
  });
});

describe("room alarm", () => {
  it("is not rewritten by every connection", async () => {
    const { pc, pcDev } = await pcWithPhones(0);
    const stub = env.ROOMS.get(env.ROOMS.idFromName(pcDev.id));
    const before = await runInDurableObject(stub, (_i, s) => s.storage.getAlarm());
    expect(before).not.toBeNull();
    pc.close();
    const again = await connect(pcDev, "pc", pcDev.id);
    expect(again.answer).toEqual({ t: "ok" });
    // the alarm already set (hello deadline or idle expiry) stays; a later expiry does not move it
    expect(await runInDurableObject(stub, (_i, s) => s.storage.getAlarm())).toBe(before);
  });
});

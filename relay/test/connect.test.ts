import { SELF, env, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { b64uEncode } from "../src/b64u";
import { Client, connect, newDevice, pcWithPhones, randomId, setAllow, sleep } from "./helpers";

describe("HTTP", () => {
  it("refuses requests that are not WebSocket upgrades or lack the room", async () => {
    expect((await SELF.fetch("https://relay.test/v1/connect?room=" + randomId())).status).toBe(426);
    const up = { headers: { Upgrade: "websocket" } };
    expect((await SELF.fetch("https://relay.test/v1/connect", up)).status).toBe(400);
    expect((await SELF.fetch("https://relay.test/v1/connect?room=abc", up)).status).toBe(400);
    expect((await SELF.fetch("https://relay.test/v1/pair", up)).status).toBe(400);
    expect((await SELF.fetch("https://relay.test/other", up)).status).toBe(404);
  });
});

describe("hello", () => {
  it("sends a 32-byte challenge first", async () => {
    const c = await Client.open(`/v1/connect?room=${randomId()}`);
    const ch = await c.next();
    expect(ch.t).toBe("challenge");
    expect(ch.nonce).toMatch(/^[A-Za-z0-9_-]{43}$/);
    c.close();
  });

  it("accepts a PC whose id is the room, creating the room", async () => {
    const pc = await newDevice();
    const { c, answer } = await connect(pc, "pc", pc.id);
    expect(answer).toEqual({ t: "ok" });
    const stored = await runInDurableObject(env.ROOMS.get(env.ROOMS.idFromName(pc.id)), (_i, state) => state.storage.get("pc"));
    expect(stored).toBe(pc.pub);
    c.close();
  });

  it("refuses a signature over another nonce", async () => {
    const pc = await newDevice();
    const c = await Client.open(`/v1/connect?room=${pc.id}`);
    await c.next();
    const sig = await pc.sign(`rigcontrol-link-v1|${randomId()}|${pc.id}`);
    c.send({ t: "hello", role: "pc", room: pc.id, pub: pc.pub, sig });
    expect(await c.next()).toEqual({ t: "error", code: "badsig" });
    expect(await c.waitClosed()).toBe(1008);
    // Nothing was created.
    const stored = await runInDurableObject(env.ROOMS.get(env.ROOMS.idFromName(pc.id)), (_i, state) => state.storage.get("pc"));
    expect(stored).toBeUndefined();
  });

  it("refuses a signature by another key, and a DER signature", async () => {
    const pc = await newDevice();
    const other = await newDevice();
    let c = await Client.open(`/v1/connect?room=${pc.id}`);
    let ch = await c.next();
    c.send({ t: "hello", role: "pc", room: pc.id, pub: pc.pub, sig: await other.sign(`rigcontrol-link-v1|${ch.nonce}|${pc.id}`) });
    expect(await c.next()).toEqual({ t: "error", code: "badsig" });

    c = await Client.open(`/v1/connect?room=${pc.id}`);
    ch = await c.next();
    const p1363 = await pc.sign(`rigcontrol-link-v1|${ch.nonce}|${pc.id}`);
    c.send({ t: "hello", role: "pc", room: pc.id, pub: pc.pub, sig: p1363 + "AAAA" }); // 67 bytes
    expect(await c.next()).toEqual({ t: "error", code: "badsig" });
  });

  it("refuses a PC whose key is not the room's", async () => {
    const pc = await newDevice();
    const room = randomId();
    const { answer } = await connect(pc, "pc", room);
    expect(answer).toEqual({ t: "error", code: "badroom" });
  });

  it("refuses a hello for another room than the URL's", async () => {
    const pc = await newDevice();
    const c = await Client.open(`/v1/connect?room=${randomId()}`);
    const ch = await c.next();
    c.send({ t: "hello", role: "pc", room: pc.id, pub: pc.pub, sig: await pc.sign(`rigcontrol-link-v1|${ch.nonce}|${pc.id}`) });
    expect(await c.next()).toEqual({ t: "error", code: "badroom" });
  });

  it("refuses a key that is not an uncompressed P-256 SPKI", async () => {
    const c = await Client.open(`/v1/connect?room=${randomId()}`);
    await c.next();
    const room = randomId();
    c.send({ t: "hello", role: "pc", room, pub: b64uEncode(new Uint8Array(91)), sig: "AA" });
    // room mismatch is checked first; use the URL's room
    expect((await c.next()).code).toBe("badroom");
    const d = await Client.open(`/v1/connect?room=${room}`);
    await d.next();
    d.send({ t: "hello", role: "pc", room, pub: b64uEncode(new Uint8Array(91)), sig: "AA" });
    expect(await d.next()).toEqual({ t: "error", code: "badkey" });
  });

  it("refuses anything but a hello first", async () => {
    const c = await Client.open(`/v1/connect?room=${randomId()}`);
    await c.next();
    c.send({ t: "msg", to: randomId(), body: "AA" });
    expect(await c.next()).toEqual({ t: "error", code: "badmsg" });
    expect(await c.waitClosed()).toBe(1008);
  });

  it("refuses a phone not on the allow-list, and a phone of a room that does not exist", async () => {
    const { pcDev, phones } = await pcWithPhones(1);
    const stranger = await newDevice();
    expect((await connect(stranger, "phone", pcDev.id)).answer).toEqual({ t: "error", code: "denied" });
    expect((await connect(phones[0]!, "phone", randomId())).answer).toEqual({ t: "error", code: "denied" });
    expect((await connect(phones[0]!, "phone", pcDev.id)).answer).toEqual({ t: "ok" });
  });

  it("closes a connection that sends no hello within 10 s", async () => {
    const room = randomId();
    const c = await Client.open(`/v1/connect?room=${room}`);
    await c.next();
    const stub = env.ROOMS.get(env.ROOMS.idFromName(room));
    await runInDurableObject(stub, (_i, state) => {
      for (const ws of state.getWebSockets()) ws.serializeAttachment({ ...ws.deserializeAttachment(), dl: Date.now() - 1 });
    });
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect(await c.next()).toEqual({ t: "error", code: "timeout" });
    expect(await c.waitClosed()).toBe(1008);
  });
});

describe("allow-list", () => {
  it("is replaced only with a valid PC signature", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    const newPhone = await newDevice();
    // Signed by someone else: refused, list unchanged.
    const sig = await newPhone.sign(`rigcontrol-link-v1|allow|${pcDev.id}|${newPhone.id}`);
    pc.send({ t: "allow", phones: [newPhone.id], sig });
    expect(await pc.next()).toEqual({ t: "error", code: "badsig" });
    expect((await connect(newPhone, "phone", pcDev.id)).answer.code).toBe("denied");
    expect((await connect(phones[0]!, "phone", pcDev.id)).answer.t).toBe("ok");
    await pc.next(); // presence of phones[0]
    // Signed over a different order: refused.
    const both = [phones[0]!.id, newPhone.id];
    const wrongOrder = await pcDev.sign(`rigcontrol-link-v1|allow|${pcDev.id}|${[...both].reverse().join(",")}`);
    pc.send({ t: "allow", phones: both, sig: wrongOrder });
    expect(await pc.next()).toEqual({ t: "error", code: "badsig" });
  });

  it("disconnects removed phones and reports them offline", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(2);
    const a = (await connect(phones[0]!, "phone", pcDev.id)).c;
    expect(await pc.next()).toEqual({ t: "presence", id: phones[0]!.id, online: true });
    await setAllow(pc, pcDev, [phones[1]!.id]);
    expect(await a.next()).toEqual({ t: "error", code: "removed" });
    expect(await a.waitClosed()).toBe(4001);
    expect(await pc.next()).toEqual({ t: "presence", id: phones[0]!.id, online: false });
    expect((await connect(phones[0]!, "phone", pcDev.id)).answer.code).toBe("denied");
  });

  it("can be emptied (the signed text then ends with '|')", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    await setAllow(pc, pcDev, []);
    pc.send({ t: "msg", to: phones[0]!.id, body: "AA" });
    expect(await pc.next()).toEqual({ t: "error", code: "notallowed" });
  });

  it("may not be sent by a phone", async () => {
    const { pcDev, phones } = await pcWithPhones(1);
    const { c } = await connect(phones[0]!, "phone", pcDev.id);
    c.send({ t: "allow", phones: [], sig: await pcDev.sign(`rigcontrol-link-v1|allow|${pcDev.id}|`) });
    expect(await c.next()).toEqual({ t: "error", code: "notallowed" });
  });
});

describe("routing", () => {
  it("delivers phone -> PC and PC -> phone with from set by the relay", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    const { c: phone } = await connect(phones[0]!, "phone", pcDev.id);
    await pc.next(); // presence
    phone.send({ t: "msg", to: pcDev.id, body: "c2VhbGVk" });
    expect(await pc.next()).toEqual({ t: "msg", from: phones[0]!.id, body: "c2VhbGVk" });
    pc.send({ t: "msg", to: phones[0]!.id, body: "cmVwbHk" });
    expect(await phone.next()).toEqual({ t: "msg", from: pcDev.id, body: "cmVwbHk" });
  });

  it("handles frames sent right behind the hello after it, in order", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    const c = await Client.open(`/v1/connect?room=${pcDev.id}`);
    const ch = await c.next();
    const sig = await phones[0]!.sign(`rigcontrol-link-v1|${ch.nonce}|${pcDev.id}`);
    c.send({ t: "hello", role: "phone", room: pcDev.id, pub: phones[0]!.pub, sig });
    c.send({ t: "msg", to: pcDev.id, body: "MQ" });
    c.send({ t: "msg", to: pcDev.id, body: "Mg" });
    expect(await c.next()).toEqual({ t: "ok" });
    expect(await pc.next()).toEqual({ t: "presence", id: phones[0]!.id, online: true });
    expect(await pc.next()).toEqual({ t: "msg", from: phones[0]!.id, body: "MQ" });
    expect(await pc.next()).toEqual({ t: "msg", from: phones[0]!.id, body: "Mg" });
  });

  it("lets phones send only to the PC", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(2);
    const { c: a } = await connect(phones[0]!, "phone", pcDev.id);
    const { c: b } = await connect(phones[1]!, "phone", pcDev.id);
    a.send({ t: "msg", to: phones[1]!.id, body: "AA" });
    expect(await a.next()).toEqual({ t: "error", code: "notallowed" });
    expect(await b.silent()).toBe(true);
    pc.pending();
  });

  it("lets the PC send only to allowed phones", async () => {
    const { pc } = await pcWithPhones(1);
    pc.send({ t: "msg", to: randomId(), body: "AA" });
    expect(await pc.next()).toEqual({ t: "error", code: "notallowed" });
  });

  it("answers gone when the other side is not connected", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    pc.send({ t: "msg", to: phones[0]!.id, body: "AA" });
    expect(await pc.next()).toEqual({ t: "gone", to: phones[0]!.id });
    pc.close();
    await sleep(100);
    const { c: phone } = await connect(phones[0]!, "phone", pcDev.id);
    phone.send({ t: "msg", to: pcDev.id, body: "AA" });
    expect(await phone.next()).toEqual({ t: "gone", to: pcDev.id });
  });

  it("rejects malformed messages without closing", async () => {
    const { pc, phones } = await pcWithPhones(1);
    pc.send("not json");
    expect(await pc.next()).toEqual({ t: "error", code: "badmsg" });
    pc.send({ t: "msg", to: phones[0]!.id, body: "not base64url!" });
    expect(await pc.next()).toEqual({ t: "error", code: "badmsg" });
    pc.send({ t: "nope" });
    expect(await pc.next()).toEqual({ t: "error", code: "badmsg" });
    pc.send({ t: "msg", to: phones[0]!.id, body: "AA" });
    expect(await pc.next()).toEqual({ t: "gone", to: phones[0]!.id });
  });

  it("closes the older connection when the same device connects again", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    const { c: first } = await connect(phones[0]!, "phone", pcDev.id);
    expect(await pc.next()).toEqual({ t: "presence", id: phones[0]!.id, online: true });
    const { c: second } = await connect(phones[0]!, "phone", pcDev.id);
    expect(await first.next()).toEqual({ t: "error", code: "replaced" });
    expect(await first.waitClosed()).toBe(4000);
    expect(await pc.silent()).toBe(true); // still online: no presence change
    pc.send({ t: "msg", to: phones[0]!.id, body: "AA" });
    expect(await second.next()).toEqual({ t: "msg", from: pcDev.id, body: "AA" });
  });
});

describe("presence", () => {
  it("tells the PC when phones come and go, and who is there when it connects", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(2);
    const { c: a } = await connect(phones[0]!, "phone", pcDev.id);
    expect(await pc.next()).toEqual({ t: "presence", id: phones[0]!.id, online: true });
    a.close();
    expect(await pc.next()).toEqual({ t: "presence", id: phones[0]!.id, online: false });

    await connect(phones[1]!, "phone", pcDev.id);
    expect(await pc.next()).toEqual({ t: "presence", id: phones[1]!.id, online: true });
    pc.close();
    await sleep(100);
    const again = await connect(pcDev, "pc", pcDev.id);
    expect(again.answer).toEqual({ t: "ok" });
    expect(await again.c.next()).toEqual({ t: "presence", id: phones[1]!.id, online: true });
    expect(await again.c.silent()).toBe(true);
  });
});

describe("limits", () => {
  it("accepts a 64 KiB frame and closes on a larger one", async () => {
    const { pc, phones } = await pcWithPhones(1);
    const head = `{"t":"msg","to":"${phones[0]!.id}","body":"`;
    const exact = head + "A".repeat(65536 - head.length - 2) + `"}`;
    expect(new TextEncoder().encode(exact).length).toBe(65536);
    pc.send(exact);
    expect(await pc.next()).toEqual({ t: "gone", to: phones[0]!.id });
    pc.send(head + "A".repeat(65536 - head.length - 1) + `"}`);
    expect(await pc.next()).toEqual({ t: "error", code: "toolarge" });
    expect(await pc.waitClosed()).toBe(1009);
  });

  it("does not forward a frame that would be over 64 KiB once 'from' is added", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    const { c: phone } = await connect(phones[0]!, "phone", pcDev.id);
    await pc.next();
    const head = `{"t":"msg","to":"${pcDev.id}","body":"`;
    phone.send(head + "A".repeat(65536 - head.length - 2) + `"}`);
    expect(await phone.next()).toEqual({ t: "error", code: "toolarge" });
    expect(await pc.silent()).toBe(true);
  });

  it("drops frames over 20 per second and closes after 100 excess frames", async () => {
    const { pc, phones } = await pcWithPhones(1);
    await sleep(1100); // a fresh window
    const frame = JSON.stringify({ t: "msg", to: phones[0]!.id, body: "AA" });
    for (let i = 0; i < 300; i++) pc.send(frame);
    expect(await pc.waitClosed(5000)).toBe(1008);
    const got = pc.pending();
    expect(got.at(-1)).toEqual({ t: "error", code: "ratelimit" });
    const gone = got.filter((f) => f.t === "gone").length;
    expect(gone).toBeGreaterThanOrEqual(20);
    expect(gone).toBeLessThan(300 - 100);
  });

  it("closes binary frames", async () => {
    const { pc } = await pcWithPhones(0);
    pc.pending();
    pc.ws.send(new Uint8Array([1, 2, 3]));
    expect(await pc.next()).toEqual({ t: "error", code: "badmsg" });
    expect(await pc.waitClosed()).toBe(1003);
  });
});

describe("storage", () => {
  it("keeps only the PC key, the allow-list and the last-seen time", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    const { c: phone } = await connect(phones[0]!, "phone", pcDev.id);
    phone.send({ t: "msg", to: pcDev.id, body: "c2VjcmV0" });
    await pc.next();
    await pc.next();
    const all = await runInDurableObject(env.ROOMS.get(env.ROOMS.idFromName(pcDev.id)), async (_i, state) =>
      Object.fromEntries(await state.storage.list()),
    );
    expect(Object.keys(all).sort()).toEqual(["allow", "lastSeen", "pc"]);
    expect(all.allow).toEqual([phones[0]!.id]);
    expect(JSON.stringify(all)).not.toContain("c2VjcmV0");
  });

  it("deletes a room idle for 90 days", async () => {
    const { pc, pcDev, phones } = await pcWithPhones(1);
    pc.close();
    await sleep(100);
    const stub = env.ROOMS.get(env.ROOMS.idFromName(pcDev.id));
    // Not yet idle long enough: kept.
    await runInDurableObject(stub, (_i, state) => state.storage.put("lastSeen", Date.now() - 89 * 86400_000));
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect(await runInDurableObject(stub, (_i, state) => state.storage.get("pc"))).toBe(pcDev.pub);
    expect(await runInDurableObject(stub, (_i, state) => state.storage.getAlarm())).not.toBeNull();
    // 90 days: gone, and the phone is no longer allowed.
    await runInDurableObject(stub, (_i, state) => state.storage.put("lastSeen", Date.now() - 90 * 86400_000 - 1));
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect(await runInDurableObject(stub, async (_i, state) => (await state.storage.list()).size)).toBe(0);
    expect((await connect(phones[0]!, "phone", pcDev.id)).answer.code).toBe("denied");
  });
});

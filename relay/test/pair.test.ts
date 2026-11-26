import { env, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { Client, randomId } from "./helpers";

const pairStub = (h: string) => env.PAIRS.get(env.PAIRS.idFromName(h));

async function offer(h: string, box = "b2ZmZXI", owner = randomId()) {
  const pc = await Client.open(`/v1/pair?h=${h}`);
  pc.send({ t: "offer", h, box, owner });
  expect(await pc.next()).toEqual({ t: "ok" });
  return pc;
}

async function claim(h: string, box = "Y2xhaW0") {
  const phone = await Client.open(`/v1/pair?h=${h}`);
  phone.send({ t: "claim", h, box });
  return phone;
}

async function offerRefused(h: string, owner = randomId()) {
  const pc = await Client.open(`/v1/pair?h=${h}`);
  pc.send({ t: "offer", h, box: "AA", owner });
  expect(await pc.next()).toEqual({ t: "error", code: "nopair" });
  expect(await pc.waitClosed()).toBe(1008);
}

const storedKeys = async (h: string) =>
  (await runInDurableObject(pairStub(h), async (_i, s) => [...(await s.storage.list()).keys()])).sort();

describe("pairing", () => {
  it("swaps the boxes, closes both and forgets them", async () => {
    const h = randomId();
    const pc = await offer(h);
    const phone = await claim(h);
    expect(await phone.next()).toEqual({ t: "offer", h, box: "b2ZmZXI" });
    expect(await pc.next()).toEqual({ t: "claim", h, box: "Y2xhaW0" });
    expect(await phone.waitClosed()).toBe(1000);
    expect(await pc.waitClosed()).toBe(1000);
    expect(await storedKeys(h)).toEqual(["first", "used"]);
  });

  it("stores only a hash of the owner", async () => {
    const h = randomId();
    const owner = randomId();
    await offer(h, "b2ZmZXI", owner);
    const all = await runInDurableObject(pairStub(h), async (_i, s) => Object.fromEntries(await s.storage.list()));
    expect(Object.keys(all).sort()).toEqual(["box", "first", "owner"]);
    expect(all.owner).not.toBe(owner);
    expect(JSON.stringify(all)).not.toContain(owner);
  });

  it("answers nopair for an unknown h", async () => {
    const phone = await claim(randomId());
    expect(await phone.next()).toEqual({ t: "error", code: "nopair" });
    expect(await phone.waitClosed()).toBe(1008);
  });

  it("is single use", async () => {
    const h = randomId();
    const owner = randomId();
    await offer(h, "b2ZmZXI", owner);
    const first = await claim(h);
    expect((await first.next()).t).toBe("offer");
    const second = await claim(h);
    expect(await second.next()).toEqual({ t: "error", code: "nopair" });
    // and the PC cannot offer the same h again, not even with its owner secret
    await offerRefused(h, owner);
  });

  it("expires 10 minutes after the first offer", async () => {
    const h = randomId();
    const pc = await offer(h);
    const stub = pairStub(h);
    await runInDurableObject(stub, (_i, s) => s.storage.put("first", Date.now() - 10 * 60_000));
    const phone = await claim(h);
    expect(await phone.next()).toEqual({ t: "error", code: "nopair" });
    expect(await runDurableObjectAlarm(stub)).toBe(true);
    expect(await pc.next()).toEqual({ t: "error", code: "expired" });
    expect(await pc.waitClosed()).toBe(1008);
    // owner and box are gone; only the tombstone is left
    expect(await storedKeys(h)).toEqual(["first"]);
  });

  it("keeps refusing a used or expired h after 10 minutes, and forgets it after 24 hours", async () => {
    // used
    const used = randomId();
    await offer(used);
    expect((await (await claim(used)).next()).t).toBe("offer");
    const usedStub = pairStub(used);
    await runInDurableObject(usedStub, (_i, s) => s.storage.put("first", Date.now() - 10 * 60_000 - 1));
    expect(await runDurableObjectAlarm(usedStub)).toBe(true);
    expect(await storedKeys(used)).toEqual(["first", "used"]);
    // the alarm now waits for the 24 hours
    const alarm = await runInDurableObject(usedStub, (_i, s) => s.storage.getAlarm());
    const first = await runInDurableObject(usedStub, (_i, s) => s.storage.get<number>("first"));
    expect(alarm).toBe(first! + 24 * 3600_000);
    await offerRefused(used);
    const late = await claim(used);
    expect(await late.next()).toEqual({ t: "error", code: "nopair" });
    // after 24 hours everything goes
    await runInDurableObject(usedStub, (_i, s) => s.storage.put("first", Date.now() - 24 * 3600_000 - 1));
    expect(await runDurableObjectAlarm(usedStub)).toBe(true);
    expect(await storedKeys(used)).toEqual([]);

    // expired without a claim
    const expired = randomId();
    const owner = randomId();
    const pc = await offer(expired, "b2ZmZXI", owner);
    const expStub = pairStub(expired);
    await runInDurableObject(expStub, (_i, s) => s.storage.put("first", Date.now() - 11 * 60_000));
    expect(await runDurableObjectAlarm(expStub)).toBe(true);
    expect(await pc.next()).toEqual({ t: "error", code: "expired" });
    await offerRefused(expired, owner);
    expect(await storedKeys(expired)).toEqual(["first"]);
  });

  it("withdraws the offer when the PC disconnects", async () => {
    const h = randomId();
    const pc = await offer(h);
    pc.close();
    await new Promise((r) => setTimeout(r, 100));
    const phone = await claim(h);
    expect(await phone.next()).toEqual({ t: "error", code: "nopair" });
  });

  it("lets the PC with the same owner replace its offer", async () => {
    const h = randomId();
    const owner = randomId();
    const old = await offer(h, "b2xk", owner);
    const fresh = await offer(h, "bmV3", owner);
    expect(await old.next()).toEqual({ t: "error", code: "replaced" });
    expect(await old.waitClosed()).toBe(4000);
    const phone = await claim(h);
    expect(await phone.next()).toEqual({ t: "offer", h, box: "bmV3" });
    expect((await fresh.next()).t).toBe("claim");
  });

  it("refuses an offer with another owner and keeps the existing one", async () => {
    const h = randomId();
    const pc = await offer(h, "b2xk", randomId());
    await offerRefused(h, randomId());
    expect(await pc.silent()).toBe(true);
    const phone = await claim(h);
    expect(await phone.next()).toEqual({ t: "offer", h, box: "b2xk" });
    expect((await pc.next()).t).toBe("claim");
  });

  it("refuses a re-offer with another owner after the PC's connection dropped", async () => {
    const h = randomId();
    const owner = randomId();
    const pc = await offer(h, "b2xk", owner);
    pc.close();
    await new Promise((r) => setTimeout(r, 100));
    await offerRefused(h, randomId());
    // the owner itself still may
    await offer(h, "bmV3", owner);
  });

  it("refuses an offer without a well-formed owner", async () => {
    for (const owner of [undefined, "", "AA", randomId() + "A", 7]) {
      const h = randomId();
      const c = await Client.open(`/v1/pair?h=${h}`);
      c.send({ t: "offer", h, box: "AA", owner });
      expect(await c.next()).toEqual({ t: "error", code: "badmsg" });
      expect(await c.waitClosed()).toBe(1008);
    }
  });

  it("refuses an h that differs from the URL's", async () => {
    const h = randomId();
    const c = await Client.open(`/v1/pair?h=${h}`);
    c.send({ t: "offer", h: randomId(), box: "AA", owner: randomId() });
    expect(await c.next()).toEqual({ t: "error", code: "badmsg" });
  });

  it("closes a connection that sends nothing within 10 s, also when a late frame beats the alarm", async () => {
    const h = randomId();
    const c = await Client.open(`/v1/pair?h=${h}`);
    await runInDurableObject(pairStub(h), (_i, state) => {
      for (const ws of state.getWebSockets()) ws.serializeAttachment({ ...ws.deserializeAttachment(), dl: Date.now() - 1 });
    });
    c.send({ t: "claim", h, box: "AA" });
    expect(await c.next()).toEqual({ t: "error", code: "timeout" });
    expect(await c.waitClosed()).toBe(1008);
  });

  it("writes the alarm once, not on every connection", async () => {
    const h = randomId();
    const a = await Client.open(`/v1/pair?h=${h}`);
    const first = await runInDurableObject(pairStub(h), (_i, s) => s.storage.getAlarm());
    expect(first).not.toBeNull();
    const b = await Client.open(`/v1/pair?h=${h}`);
    // a later deadline does not move the alarm
    expect(await runInDurableObject(pairStub(h), (_i, s) => s.storage.getAlarm())).toBe(first);
    a.close();
    b.close();
  });
});

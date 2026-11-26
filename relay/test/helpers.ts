import { SELF } from "cloudflare:test";
import { b64uEncode } from "../src/b64u";

export type Frame = Record<string, unknown> & { t: string };

/** A WebSocket client of the relay, with a queue of received frames. */
export class Client {
  private frames: Frame[] = [];
  private waiters: ((f: Frame) => void)[] = [];
  closeCode: number | null = null;
  closeReason = "";
  readonly closed: Promise<void>;

  constructor(readonly ws: WebSocket) {
    let done!: () => void;
    this.closed = new Promise((r) => (done = r));
    ws.addEventListener("message", (e) => {
      const f = JSON.parse(e.data as string) as Frame;
      const w = this.waiters.shift();
      if (w) w(f);
      else this.frames.push(f);
    });
    ws.addEventListener("close", (e) => {
      this.closeCode = e.code;
      this.closeReason = e.reason;
      done();
    });
    ws.accept();
  }

  /** Opens a WebSocket to the relay. Each call gets its own address unless one is given (the upgrade limits). */
  static async open(path: string, ip = freshAddress()): Promise<Client> {
    const res = await SELF.fetch(`https://relay.test${path}`, { headers: { Upgrade: "websocket", "CF-Connecting-IP": ip } });
    if (res.status !== 101 || !res.webSocket) throw new Error(`upgrade failed: ${res.status} ${await res.text()}`);
    return new Client(res.webSocket);
  }

  send(v: unknown): void {
    this.ws.send(typeof v === "string" ? v : JSON.stringify(v));
  }

  next(timeoutMs = 3000): Promise<Frame> {
    const f = this.frames.shift();
    if (f) return Promise.resolve(f);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("no frame within timeout")), timeoutMs);
      this.waiters.push((x) => {
        clearTimeout(timer);
        resolve(x);
      });
    });
  }

  /** Resolves true when no frame arrives within the time. */
  async silent(ms = 200): Promise<boolean> {
    if (this.frames.length) return false;
    await new Promise((r) => setTimeout(r, ms));
    return this.frames.length === 0;
  }

  /** Frames received but not yet taken. */
  pending(): Frame[] {
    return this.frames.splice(0);
  }

  async waitClosed(timeoutMs = 3000): Promise<number | null> {
    await Promise.race([this.closed, new Promise((r) => setTimeout(r, timeoutMs))]);
    return this.closeCode;
  }

  close(): void {
    try {
      this.ws.close(1000, "bye");
    } catch {
      /* closed */
    }
  }
}

export interface Device {
  pub: string;
  id: string;
  keys: CryptoKeyPair;
  sign(text: string): Promise<string>;
}

export async function newDevice(): Promise<Device> {
  const keys = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const spki = new Uint8Array((await crypto.subtle.exportKey("spki", keys.publicKey)) as ArrayBuffer);
  const id = b64uEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", spki)));
  return {
    pub: b64uEncode(spki),
    id,
    keys,
    async sign(text: string) {
      const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, keys.privateKey, new TextEncoder().encode(text));
      return b64uEncode(new Uint8Array(sig));
    },
  };
}

/** Opens /v1/connect for `room`, answers the challenge, and returns the client and the relay's answer. */
export async function connect(dev: Device, role: "pc" | "phone", room: string): Promise<{ c: Client; answer: Frame }> {
  const c = await Client.open(`/v1/connect?room=${room}`);
  const ch = await c.next();
  if (ch.t !== "challenge" || typeof ch.nonce !== "string") throw new Error("no challenge");
  const sig = await dev.sign(`rigcontrol-link-v1|${ch.nonce}|${room}`);
  c.send({ t: "hello", role, room, pub: dev.pub, sig });
  return { c, answer: await c.next() };
}

export async function setAllow(pc: Client, dev: Device, phones: string[]): Promise<void> {
  const sig = await dev.sign(`rigcontrol-link-v1|allow|${dev.id}|${phones.join(",")}`);
  pc.send({ t: "allow", phones, sig });
}

/** A PC with a room and the given phones allowed. */
export async function pcWithPhones(n: number): Promise<{ pcDev: Device; pc: Client; phones: Device[] }> {
  const pcDev = await newDevice();
  const { c: pc, answer } = await connect(pcDev, "pc", pcDev.id);
  if (answer.t !== "ok") throw new Error(`pc hello: ${JSON.stringify(answer)}`);
  const phones: Device[] = [];
  for (let i = 0; i < n; i++) phones.push(await newDevice());
  await setAllow(pc, pcDev, phones.map((p) => p.id));
  // allow has no answer: a round trip through the room shows it was handled.
  pc.send({ t: "msg", to: phones[0]?.id ?? pcDev.id, body: "AA" });
  await pc.next();
  return { pcDev, pc, phones };
}

/** A random IPv6 /64 each time, so tests do not run into the per-address upgrade limits. */
export function freshAddress(): string {
  const g = [...crypto.getRandomValues(new Uint16Array(3))].map((x) => x.toString(16));
  return `2001:db8:${g.join(":")}::1`;
}

export const randomId = () => b64uEncode(crypto.getRandomValues(new Uint8Array(32)));

export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

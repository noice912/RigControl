import { describe, expect, it } from "vitest";
import { b64uDecode, b64uEncode, isId } from "../src/b64u";
import { importSigningKey, verifySignature } from "../src/crypto";
import { allowText, helloText } from "../src/protocol";

// Fixed values (also in README.md) so other clients can check their id, signed text and signature form.
// Private scalar d = SHA-256(UTF-8 "rigcontrol-link test vector") = TYZpsf8nnOtU2hG5CrIbpr2t5zLCvJS1Fg8Zz6CoLWU.
const PUB =
  "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXBkWlhfTI7ssupvLHvSM3b0LyJ_7IruW1xR88mgLuZ7ZZQnX8ZtbGtG1Ey6WBVDQvVAtWs-SGyTcDNBTqzAmPA";
const ID = "s-cAp3rrut4EfrWaw6fc5Ykr_lc0qYFZmXVsTT2CQ8k";
const NONCE = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"; // bytes 0..31
const SIG = "TNnWHUJntqq5_mS-kax7XgIs-eYlApHejbhUY_Q3AbTqUq7UGQTsN0zzRTTceiAJhXBDU0kaMOTkHKkUPoF_Lw";

describe("test vectors", () => {
  it("id = base64url(SHA-256(SPKI)), hello signature in P1363 form", async () => {
    const key = await importSigningKey(PUB);
    expect(key?.id).toBe(ID);
    expect(helloText(NONCE, ID)).toBe(`rigcontrol-link-v1|${NONCE}|${ID}`);
    expect(await verifySignature(key!.key, SIG, helloText(NONCE, ID))).toBe(true);
    expect(await verifySignature(key!.key, SIG, helloText(NONCE, ID) + " ")).toBe(false);
  });

  it("allow text", () => {
    expect(allowText("R", ["a", "b"])).toBe("rigcontrol-link-v1|allow|R|a,b");
    expect(allowText("R", [])).toBe("rigcontrol-link-v1|allow|R|");
  });

  it("base64url is strict", () => {
    expect(b64uEncode(new Uint8Array([0xfb, 0xff]))).toBe("-_8");
    expect(b64uDecode("-_8")).toEqual(new Uint8Array([0xfb, 0xff]));
    expect(b64uDecode("-_8=")).toBeNull(); // padding
    expect(b64uDecode("+/8")).toBeNull(); // standard alphabet
    expect(b64uDecode("-_9")).toBeNull(); // non-zero unused bits
    expect(isId(ID)).toBe(true);
    expect(isId(ID.slice(0, 42) + "l")).toBe(false); // 43rd char must leave the last 2 bits zero
  });
});

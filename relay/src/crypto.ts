import { b64uDecode, b64uEncode } from "./b64u";

// The only accepted key form: SubjectPublicKeyInfo DER of an uncompressed P-256 point (91 bytes), which is what
// .NET's ExportSubjectPublicKeyInfo(), Apple's and Android's SPKI exports and WebCrypto's exportKey("spki") give.
// Requiring this exact form means one key has exactly one id.
const SPKI_PREFIX = new Uint8Array([
  0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce,
  0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
]);
const SPKI_LENGTH = 91;

const ECDSA = { name: "ECDSA", namedCurve: "P-256" } as const;
const ECDSA_SHA256 = { name: "ECDSA", hash: "SHA-256" } as const;
const utf8 = new TextEncoder();

export interface PublicKey {
  /** base64url(SHA-256(SPKI DER)), no padding. */
  id: string;
  key: CryptoKey;
}

/** Parses a signing key sent as base64url SPKI. Null when it is not a valid uncompressed P-256 key. */
export async function importSigningKey(pubB64u: unknown): Promise<PublicKey | null> {
  if (typeof pubB64u !== "string") return null;
  const der = b64uDecode(pubB64u);
  if (!der || der.length !== SPKI_LENGTH) return null;
  for (let i = 0; i < SPKI_PREFIX.length; i++) if (der[i] !== SPKI_PREFIX[i]) return null;
  let key: CryptoKey;
  try {
    key = await crypto.subtle.importKey("spki", der, ECDSA, false, ["verify"]);
  } catch {
    return null; // not a point on the curve
  }
  const id = b64uEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", der)));
  return { id, key };
}

/** Checks a base64url IEEE P1363 (r||s, 64 bytes) ECDSA P-256 SHA-256 signature over the UTF-8 of `text`. */
export async function verifySignature(key: CryptoKey, sigB64u: unknown, text: string): Promise<boolean> {
  if (typeof sigB64u !== "string") return false;
  const sig = b64uDecode(sigB64u);
  if (!sig || sig.length !== 64) return false;
  try {
    return await crypto.subtle.verify(ECDSA_SHA256, key, sig, utf8.encode(text));
  } catch {
    return false;
  }
}

export function randomNonce(): string {
  return b64uEncode(crypto.getRandomValues(new Uint8Array(32)));
}

// base64url (RFC 4648 section 5) without padding. Decoding is strict: only the URL alphabet, no '=', and the
// unused low bits of the last character must be zero, so every byte string has exactly one accepted spelling.

export function b64uEncode(bytes: Uint8Array): string {
  let s = "";
  for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]!);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

const ALPHABET = /^[A-Za-z0-9_-]*$/;

export function b64uDecode(s: string): Uint8Array<ArrayBuffer> | null {
  if (!ALPHABET.test(s) || s.length % 4 === 1) return null;
  const std = s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4);
  let bin: string;
  try {
    bin = atob(std);
  } catch {
    return null;
  }
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  if (b64uEncode(out) !== s) return null; // non-zero trailing bits
  return out;
}

/** A body: one or more base64url characters. The relay never decodes it. */
export function isB64uText(s: unknown): s is string {
  return typeof s === "string" && s.length > 0 && ALPHABET.test(s);
}

const ID_SHAPE = /^[A-Za-z0-9_-]{43}$/;

/** A device id / room / pairing h: base64url of 32 bytes (43 characters), canonical spelling. */
export function isId(s: unknown): s is string {
  return typeof s === "string" && ID_SHAPE.test(s) && b64uDecode(s) !== null;
}

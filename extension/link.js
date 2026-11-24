// Finds RigControl (or RichPresence) on this PC. Shared by background.js and popup.js (link-token.js is loaded first).
// Each Windows user's RigControl listens on its own port, 47610 to 47619, on 127.0.0.1 only. A RigControl that
// belongs to another Windows user answers 403, so it is passed over.
//
// That alone is not proof: any program on the PC could listen on one of these ports and call itself RigControl.
// The copy of this extension that RigControl hands out ("Get the browser extension" on its Games page) has your
// own secret in link-token.js. With it, every app is asked to prove it knows the secret (it signs a random number;
// the secret is never sent), and only one that can is ever told what you watch. A copy without the secret (loaded
// straight from the source folder) cannot check and reports to the first RigControl that answers.
const RP_HAS_TOKEN = typeof RP_TOKEN === 'string' && /^[0-9a-f]{64}$/.test(RP_TOKEN);
const RP_TRUST_MS = 20000;  // how long a checked port is used before it is checked again
let rpCheckedAt = 0;
const RP_FIRST_PORT = 47610;
const RP_PORT_COUNT = 10;
let rpPort = 0;         // the port that answered last time (remembered while the extension stays loaded)
let rpFinding = null;   // one search at a time
let rpMissUntil = 0;    // after a search found nothing: not again for a few seconds

function rpHex(bytes) { return Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, '0')).join(''); }

// HMAC-SHA256(secret, nonce) as hex: what your own RigControl answers.
async function rpProof(nonce) {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey('raw', enc.encode(RP_TOKEN), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return rpHex(await crypto.subtle.sign('HMAC', key, enc.encode(nonce)));
}

// The app's greeting on one port, or null. With a secret, null too for any app that cannot prove it knows it.
async function rpAsk(port) {
  try {
    const nonce = RP_HAS_TOKEN ? rpHex(crypto.getRandomValues(new Uint8Array(16))) : '';
    const r = await fetch(`http://127.0.0.1:${port}/status${nonce ? '?nonce=' + nonce : ''}`, {
      headers: { 'X-RichPresence': '1' }, cache: 'no-store', signal: AbortSignal.timeout(1500)
    });
    if (!r.ok) return null;
    const s = await r.json();
    if (!s || typeof s.app !== 'string') return null;
    if (RP_HAS_TOKEN && (typeof s.proof !== 'string' || s.proof !== await rpProof(nonce))) return null;
    s.checked = RP_HAS_TOKEN;
    return s;
  } catch (e) {
    return null;
  }
}

async function rpSearch() {
  if (rpPort) {
    const s = await rpAsk(rpPort);
    if (s) { rpCheckedAt = Date.now(); return { port: rpPort, status: s }; }
    rpPort = 0;
  }
  let other = null;
  for (let port = RP_FIRST_PORT; port < RP_FIRST_PORT + RP_PORT_COUNT; port++) {
    const s = await rpAsk(port);
    if (!s) continue;
    // RigControl first; RichPresence (same protocol) only when no RigControl answers.
    if (s.app === 'RigControl') { rpPort = port; rpCheckedAt = Date.now(); return { port, status: s }; }
    if (!other) other = { port, status: s };
  }
  if (other) { rpPort = other.port; rpCheckedAt = Date.now(); }
  return other;
}

// { port, status } of the app that answers, or null when none does. force: search even right after a miss.
function rpFind(force) {
  if (rpFinding) return rpFinding;
  if (!force && !rpPort && Date.now() < rpMissUntil) return Promise.resolve(null);
  rpFinding = rpSearch().then((found) => {
    rpFinding = null;
    rpMissUntil = found ? 0 : Date.now() + 10000;
    return found;
  });
  return rpFinding;
}

// Forget the remembered port (it stopped answering, or now belongs to someone else).
function rpForget() { rpPort = 0; rpCheckedAt = 0; }

// True while the remembered port may be used without asking again. With a secret, a port is asked to prove itself
// again every 20 seconds, so a program that takes the port over after your RigControl closed is found out.
function rpTrusted() { return rpPort !== 0 && (!RP_HAS_TOKEN || Date.now() - rpCheckedAt < RP_TRUST_MS); }

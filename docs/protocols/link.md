# RigControl Link: phone <-> PC through the relay (protocol v1)

The PC opens no incoming ports. The PC and its linked phones each connect OUT to a relay (Cloudflare Worker +
Durable Objects) over `wss://`. The relay forwards sealed messages between them and can read none of them.

## Keys (all P-256, built into .NET on Windows, iOS and Android)

- Every device (PC or phone) has a long-term **signing key** (ECDSA P-256) and a long-term **agreement key**
  (ECDH P-256). Private halves are encrypted for the user (DPAPI on Windows; Keychain/Keystore on phones).
- A device's **id** = base64url(SHA-256(signing public key, SubjectPublicKeyInfo DER)), no padding.
- A PC's **room** = the PC's id.

## Relay (Worker)

Endpoints (all `wss://<relay>/v1/...`), one JSON message per WebSocket text frame, max 64 KiB, max 20 frames/s per
connection (excess frames are dropped and the connection closed after 100 excess frames):

1. `GET /v1/connect` – every connection starts with the relay sending `{"t":"challenge","nonce":"<32 random bytes b64u>"}`.
   The client answers `{"t":"hello","role":"pc"|"phone","room":"<room>","pub":"<signing SPKI b64u>","sig":"<b64u ECDSA P-256 SHA-256 P1363 over UTF-8 'rigcontrol-link-v1|' + nonce + '|' + room>"}`.
   - role pc: the relay accepts when id(pub) == room. First PC hello for a room creates it.
   - role phone: accepted when id(pub) is in the room's allow-list.
   - On success: `{"t":"ok"}`, else `{"t":"error","code":"..."}` and close.
2. After `ok`:
   - PC -> relay `{"t":"allow","phones":["<id>",...],"sig":"<PC signature over 'rigcontrol-link-v1|allow|' + room + '|' + ids joined by ','>"}` replaces the allow-list
     (the relay checks the signature with the room's PC key) and disconnects removed phones.
   - Either side -> relay `{"t":"msg","to":"<id>","body":"<sealed b64u>"}`; the relay delivers `{"t":"msg","from":"<id>","body":...}`
     to that device if connected in the same room (phones may only send `to` the PC; the PC to any allowed phone),
     else answers `{"t":"gone","to":"<id>"}`. Nothing is queued in v1.
   - Relay -> PC `{"t":"presence","id":"<phone id>","online":true|false}`.
3. `GET /v1/pair` – pairing rendezvous: the PC sends `{"t":"offer","h":"<b64u SHA-256('rigcontrol-pair|' + code)>","box":"<sealed>"}`
   (valid 10 min, single use). A phone sends `{"t":"claim","h":"...","box":"<sealed>"}`; the relay hands the phone the
   offer box and the PC the claim box, then forgets both. Unknown/expired `h`: `{"t":"error","code":"nopair"}`.
   Max 10 claims per IP per minute.
- Storage per room: the PC signing key, the allow-list, last-seen time. Rooms idle for 90 days are deleted.
  No message content is ever stored or logged.

## Pairing

- The PC shows a **code**: 20 characters of Crockford base32 (100 bits), shown as 5 groups of 4, plus a QR code of
  `rigcontrol://pair?relay=<relay host>&code=<code>`. Valid 10 minutes, one phone.
- K = HKDF-SHA256(ikm = UTF-8 code (upper-cased, dashes removed), salt = "rigcontrol-pair-v1", info = "box", 32 bytes).
- Offer box (sealed with K, AES-256-GCM, 12-byte random nonce prefixed): `{"pc":"<signing SPKI>","pcAgree":"<agreement SPKI>","name":"<PC name>"}`.
- Claim box (same K): `{"phone":"<signing SPKI>","phoneAgree":"<agreement SPKI>","name":"<phone name>"}`.
- Both sides then hold each other's keys; the PC adds the phone to its allow-list (`allow`). The relay never sees K.

## Sessions and messages

- Session key per (PC, phone) connection pair: on each new connection the sender includes an ephemeral P-256
  agreement key in its first sealed message; key = HKDF-SHA256(ECDH(static_pc, static_phone) || ECDH(eph_a, eph_b),
  salt = sorted(ids) joined by '|', info = "rigcontrol-link-v1 session"). (v1 may start with the static-static key and
  add the ephemeral exchange in the same release; both ends must agree — see `RigControl.Link`.)
- Sealed body = 12-byte nonce || AES-256-GCM(ciphertext || tag), AAD = "rigcontrol-link-v1|" + from + "|" + to + "|" + counter.
- Plaintext JSON: `{"n":<counter, strictly increasing per direction>,"type":"...", ...}`. A counter not larger than the last
  accepted one is rejected (replay).
- Types (phone -> PC): `get` `{"what":"snapshot"}`, `do` `{"action":"<name>","args":{...}}`; (PC -> phone): `snapshot` {...},
  `result` `{"ok":true|false,"error":"..."}`, `event` {...}.
- Allowed actions (D1): `lighting.setEffect`/`lighting.off`/`lighting.on`, `capture.start`, `capture.stop`,
  `capture.saveReplay`, `presence.setCustom`. Everything else is refused on the PC.

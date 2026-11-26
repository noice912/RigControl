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

URLs carry the routing value as a query parameter, and it must equal the value inside the frames:
`wss://<relay>/v1/connect?room=<room>` (= hello's `room`) and `wss://<relay>/v1/pair?h=<h>` (= the offer's or claim's
`h`). base64url is strict everywhere (no padding, canonical last character; 32-byte values are 43 characters), `pub` is
the 91-byte SPKI DER of an uncompressed P-256 point, signatures are exactly 64 bytes (r||s). `relay/README.md` lists
every frame, error and close code and a test vector; `RigControl.Link` (`LinkProtocol.cs`) lists the client-side
choices (nonce layout, counters, `re`, action arguments).

1. `GET /v1/connect` – every connection starts with the relay sending `{"t":"challenge","nonce":"<32 random bytes b64u>"}`.
   The client answers `{"t":"hello","role":"pc"|"phone","room":"<room>","pub":"<signing SPKI b64u>","sig":"<b64u ECDSA P-256 SHA-256 P1363 over UTF-8 'rigcontrol-link-v1|' + nonce + '|' + room>"}`.
   - role pc: the relay accepts when id(pub) == room. First PC hello for a room creates it.
   - role phone: accepted when id(pub) is in the room's allow-list.
   - On success: `{"t":"ok"}`, else `{"t":"error","code":"..."}` and close.
2. After `ok`:
   - PC -> relay `{"t":"allow","phones":["<id>",...],"sig":"<PC signature over 'rigcontrol-link-v1|allow|' + room + '|' + ids joined by ','>"}` replaces the allow-list
     (the relay checks the signature with the room's PC key) and disconnects removed phones. At most 32 phones. The PC
     sends it after every `ok` (a room idle for 90 days comes back empty).
   - Either side -> relay `{"t":"msg","to":"<id>","body":"<sealed b64u>"}`; the relay delivers `{"t":"msg","from":"<id>","body":...}`
     to that device if connected in the same room (phones may only send `to` the PC; the PC to any allowed phone),
     else answers `{"t":"gone","to":"<id>"}`. Nothing is queued in v1.
   - Relay -> PC `{"t":"presence","id":"<phone id>","online":true|false}`.
3. `GET /v1/pair` – pairing rendezvous: the PC sends `{"t":"offer","h":"<b64u SHA-256('rigcontrol-pair|' + code)>","box":"<sealed>","owner":"<32 random bytes b64u, new per code>"}`
   (valid 10 min, single use; a later offer for the same `h` must carry the same owner). A phone sends
   `{"t":"claim","h":"...","box":"<sealed>"}`; the relay hands the phone the offer box and the PC the claim box, then
   forgets both. Unknown/expired/used `h` (refused for 24 h after the first offer): `{"t":"error","code":"nopair"}`.
   Max 10 pair connections (so claims) per IP per minute, 30 WebSocket upgrades in all; over that HTTP 429.
   Upgrades with an `Origin` header get 403. Exact wire details: `relay/README.md`.
- Storage per room: the PC signing key, the allow-list, last-seen time. Rooms idle for 90 days are deleted.
  No message content is ever stored or logged.

## Pairing

- The PC shows a **code**: 20 characters of Crockford base32 (100 bits), shown as 5 groups of 4, plus a QR code of
  `rigcontrol://pair?relay=<relay host>&code=<code>`. Valid 10 minutes, one phone.
- K = HKDF-SHA256(ikm = UTF-8 code (upper-cased, dashes removed), salt = "rigcontrol-pair-v1", info = "box", 32 bytes).
- Offer box (sealed with K, AES-256-GCM, 12-byte random nonce prefixed, no AAD):
  `{"pc":"<signing SPKI b64u>","pcAgree":"<agreement SPKI b64u>","name":"<PC name>","epoch":"<b64u of 16 random bytes>"}`.
  The **epoch** is made by the PC for every pairing and stored with the phone on both sides.
- Claim box (same K, no AAD): `{"phone":"<signing SPKI b64u>","phoneAgree":"<agreement SPKI b64u>","name":"<phone name>"}`.
- Both sides then hold each other's keys; the PC adds the phone to its allow-list (`allow`). The relay never sees K.
- The code is typed upper- or lower-case, with or without dashes or spaces; Crockford's O is read as 0 and I/L as 1.
  This normalised form is what is hashed for `h` and used as HKDF input.

## Sessions and messages

- Session key (v1: static-static, bound to the pairing; no ephemeral exchange in v1):
  key = HKDF-SHA256(ikm = ECDH(static_pc, static_phone) (raw shared secret), salt = sorted(ids, ordinal) joined by '|'
  + '|' + epoch, info = "rigcontrol-link-v1 session", 32 bytes). Linking the same phone again gives a new epoch and so
  a new key: frames recorded under an earlier pairing can't be opened at all, so the stored counter starts again at 0.
- Sealed body = 12-byte nonce || AES-256-GCM(ciphertext || tag), AAD = "rigcontrol-link-v1|" + from + "|" + to + "|" +
  counter (decimal) + "|" + epoch. Nonce = 1 byte sender (1 PC, 2 phone) || 3 random bytes || counter as unsigned
  64-bit big-endian, so the receiver can build the AAD before decrypting; the plaintext `n` must equal it.
- Plaintext JSON: `{"n":<counter, strictly increasing per direction>,"type":"...", ...}`. A counter not larger than the last
  accepted one is rejected (replay). Counters start at the sender's clock in microseconds since 1970 and go up by one,
  so they keep rising across restarts; they stay below 2^53. The receiver stores the last one it accepted. The PC
  also stores the highest counter it sent each phone and never sends a lower one (a clock set back).
- Freshness: the PC refuses a phone `get` or `do` whose counter is more than 120 s behind or 30 s ahead of the PC's
  clock, answering `result` `{"ok":false,"code":"stale","error":"<fixed sentence>"}`; such a message does not move the stored counter. So a
  message the relay held back or delayed can't be used later. The PC stores the counter of every accepted `do` before
  its rate check (so a `busy` one can't be replayed after a restart either). Over the rate limit the answer is `result`
  `{"ok":false,"code":"busy","error":"<fixed sentence>"}`. Phones word `stale` and `busy` themselves.
- Types (phone -> PC): `get` `{"what":"snapshot"}`, `do` `{"action":"<name>","args":{...}}`; (PC -> phone): `snapshot`
  `{"re":<n of the get>, ...}`, `result` `{"re":<n of the request>,"ok":true|false,"error":"...","code":"..."}` (`code`, when present, is a short
  reason such as `unavailable` or `internal`; `error` is a short fixed sentence, never the PC's own error text), `event` `{"kind":"...", ...}`.
- Allowed actions (D1): `lighting.setEffect`/`lighting.off`/`lighting.on`, `capture.start`, `capture.stop`,
  `capture.saveReplay`, `presence.setCustom`. Everything else is refused on the PC. Arguments (unknown fields refused):
  `lighting.setEffect` `{"effect":"<effect id>"}` or `{"scene":"<saved scene name>"}`; `presence.setCustom`
  `{"id":"<custom status id>","on":true|false}` (switches a status already made on the PC); the rest take none.

## Client rules (RigControl.Link)

- The PC keeps its `/v1/pair` socket open while the code is shown. When it closes for any reason without a claim
  (`replaced`, `expired`, `nopair`, a network drop), that code is finished: it is never offered again and the PC shows
  a new code (new `h`, new `owner`, new epoch).
- The challenge nonce must be exactly 43 canonical base64url characters before anything is signed over it.
- The PC sends `allow` after every `ok` (at most 32 phones). No `Origin` header is sent on the WebSocket.
- An upgrade answered HTTP 429 (or a `ratelimit` close) waits at least 10 s, growing to a minute, before trying again.
- A phone closed with `removed` (close code 4001) stops: it does not connect again until the app starts it again
  (it is no longer on the allow-list, so it would only be refused). After `denied` a client waits at least 60 s.
- Dead connections: after 30 s without a frame a client sends `{"t":"ping"}` (today's relay answers unknown frames
  with `{"t":"error","code":"badmsg"}` and keeps the connection; that answer counts as traffic and is not reported).
  After 75 s without any frame the connection is dropped and made again. A receive that fails after the hello is
  reported as `dropped`, not `unreachable`. A single send that takes more than 15 s drops the connection too (it is
  made again), so a peer that stopped reading can't hold a client forever.
- Error codes and close reasons from the relay are only used when they match `[a-z0-9]{1,24}` (else `error`), and
  only known codes are logged or shown, in the client's own words.

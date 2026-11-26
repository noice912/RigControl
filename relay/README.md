# RigControl Link relay

The relay of `docs/protocols/link.md`: a Cloudflare Worker with Durable Objects that passes sealed messages between
a PC and its phones. Both connect out to it, so the PC opens no port. The relay holds no key that opens a message.

## What it stores

| Where | What | How long |
|---|---|---|
| Room object (one per PC, named by the room = the PC's id) | `pc`: the PC's signing key (SPKI); `allow`: the phone ids of the last signed allow-list (at most 32); `lastSeen`: time of the last hello or disconnect | Until the room has been idle (no connection) for 90 days; an alarm then deletes it |
| Pairing object (one per `h`) | `first`: time of the first offer; `owner`: SHA-256 of the first offer's owner secret; `box`: the sealed offer box, only while the PC's offer connection is open; `used`: a claim succeeded | `owner` and `box` until the claim or 10 minutes after the first offer; the tombstone (`first`, `used`) until 24 hours after the first offer, then the object is deleted |

The per-address limits use Workers Rate Limiting bindings (`ratelimits` in `wrangler.jsonc`), which keep their
counters inside Cloudflare and write nothing to this Worker's storage. They are checked in the Worker before any
Durable Object is reached, so a refused request creates no object.

Not stored and not logged: message bodies, claim boxes, names, anything about who sent what. The code logs nothing,
and `wrangler.jsonc` turns Workers observability (request logs) and Logpush off. Room ids and `h` are in the request
URLs (they must be, to pick the Durable Object): they show up in `wrangler tail` while someone runs it, and would be
in request logs if observability or Logpush were ever turned on. An `h` is only a hash of a one-time code and a room
id a hash of a public key, but they do link connections to a PC, so keep logging off.

## Wire details the protocol doc leaves open

These are the relay's exact behaviour; clients must match them.

- **URLs.** `wss://<relay>/v1/connect?room=<room>` and `wss://<relay>/v1/pair?h=<h>`. The room / h must be in the
  URL so the Worker can hand the connection to the right Durable Object. HTTP answers, checked in this order: 404
  other path, 405 not GET, 426 no WebSocket upgrade, 403 the request has an `Origin` header, 400 missing or
  malformed `room` / `h`, 429 over a per-address upgrade limit (see **Limits**). The `room` in hello and the `h` in
  offer/claim must equal the URL's.
- **No `Origin`.** Any upgrade that carries an `Origin` header (whatever its value, `null` included) gets 403, so no
  web page can use the relay. Native clients must not send one (.NET's `ClientWebSocket`, `URLSessionWebSocketTask`
  and OkHttp send none unless told to).
- **base64url**: RFC 4648 URL alphabet, no `=` padding, strict (no `+`/`/`, no padding, unused low bits of the last
  character must be zero). Ids, rooms, `h` and the nonce are 32 bytes = 43 characters. Bodies and boxes must be
  non-empty base64url text (not decoded by the relay).
- **Keys**: `pub` must be the 91-byte SPKI DER of an uncompressed P-256 point (what .NET's
  `ExportSubjectPublicKeyInfo()` gives). id = base64url(SHA-256(those 91 bytes)).
- **Signatures**: ECDSA P-256 SHA-256, IEEE P1363 (r||s, exactly 64 bytes), base64url. Signed texts (UTF-8):
  - hello: `rigcontrol-link-v1|` + nonce (the base64url string as sent in the challenge) + `|` + room
  - allow: `rigcontrol-link-v1|allow|` + room + `|` + phone ids joined by `,` in the order of the `phones` array
    (empty list: the text ends with `|`). `phones` must be unique ids and must not contain the room.
- **Frames**: one JSON object per text frame. Field order of what the relay sends is as shown below.
  - `{"t":"challenge","nonce":"..."}` (first frame on `/v1/connect`; nothing is sent first on `/v1/pair`). Before
    signing, a client must check that the nonce is exactly 43 characters of canonical base64url (it decodes to 32
    bytes and re-encodes to the same text) and refuse anything else, so a relay can never get it to sign other text.
  - `{"t":"offer","h":"<h>","box":"<offer box>","owner":"<owner>"}` from the PC on `/v1/pair`. `owner` is required:
    base64url of 32 random bytes (43 characters, canonical), made fresh for each code and kept only in memory while
    that code is shown. The relay stores SHA-256(owner) with the first offer; any later offer for the same `h` must
    carry the same owner, else it gets `nopair` and is closed 1008 while the existing offer stays.
  - `{"t":"ok"}` after an accepted hello, and after an accepted pairing offer (PC side)
  - `{"t":"presence","id":"...","online":true|false}` to the PC: right after the PC's `ok` one `online:true` per
    phone already connected; then on each phone's arrival and departure (a phone reconnecting while still
    connected gives no change). A phone removed from the allow-list gives `online:false`.
  - `{"t":"msg","from":"...","body":"..."}`, `{"t":"gone","to":"..."}`
  - pairing: phone gets `{"t":"offer","h":"...","box":"<offer box>"}`, PC gets `{"t":"claim","h":"...","box":"<claim box>"}`,
    then both are closed with 1000 reason `paired`.
  - `{"t":"error","code":"..."}`
  - `allow` has no answer when it worked. `phones` may hold at most 32 ids (more: `badmsg`, list unchanged).
    Unknown fields are ignored.
- **Errors** (the close reason is the code too):

  | code | when | then |
  |---|---|---|
  | `badmsg` | not JSON / not an object / unknown `t` / wrong or missing field (also an offer without a well-formed `owner`, and `allow` with more than 32 phones) | before ok and on `/v1/pair`: close 1008; after ok: connection stays |
  | `badmsg` | binary frame | close 1003 |
  | `badkey` | `pub` not an uncompressed P-256 SPKI | close 1008 |
  | `badsig` | hello signature wrong | close 1008 |
  | `badsig` | allow signature wrong (list unchanged) | stays open |
  | `badroom` | hello `room` differs from the URL's, or a PC's id(pub) is not the room | close 1008 |
  | `denied` | phone: room does not exist or id(pub) not on the allow-list | close 1008 |
  | `notallowed` | a phone sent `allow`; a phone's `to` is not the PC; the PC's `to` is not on the allow-list | stays open |
  | `toolarge` | incoming frame over 65536 bytes of UTF-8 | close 1009 |
  | `toolarge` | the forwarded `msg` frame would be over 65536 bytes (it is 2 bytes longer than the sent one) | stays open, not delivered |
  | `ratelimit` | the 100th frame over 20/s | close 1008 |
  | `timeout` | no hello (connect) / no offer or claim (pair) within 10 s of the upgrade; one that arrives later is refused the same way | close 1008 |
  | `replaced` | the same device id connected again (newest wins), or a new offer for the same `h` | close 4000 |
  | `removed` | phone taken off the allow-list | close 4001 |
  | `nopair` | claim with no open offer (unknown, expired, withdrawn, used); offer for a used or expired `h`; offer whose `owner` differs from the first offer's | close 1008 |
  | `expired` | the PC's offer reached 10 minutes after the first offer | close 1008 |

- **Rate**: every incoming frame counts (hello too). Fixed 1-second windows; frames 21+ in a window are dropped
  without an answer and counted; the 100th dropped frame over the connection's life closes it.
- **Limits** per address (`CF-Connecting-IP`; IPv6 counted per /64), Workers Rate Limiting, 60-second periods:
  30 WebSocket upgrades (`/v1/connect` and `/v1/pair` together), and of those 10 `/v1/pair` upgrades. A pair
  connection carries one offer or claim, so this is at most 10 claims (or offers) a minute per address, also for
  unknown `h`. Over a limit the upgrade is answered HTTP 429 (no WebSocket); clients must back off (wait at least
  10 s, growing to a minute) instead of retrying at once. The counters are per Cloudflare location and approximate.
- **Hello ordering**: frames sent right after the hello are handled after it (in order), so a client may pipeline.
- **Pairing**: the offer lives only while the PC's offer connection is open (closing it withdraws the offer). The 10
  minutes run from the first offer. The relay lets an offer with the same `owner` replace the open one (the older
  connection gets `replaced`), but the PC must not rely on that: when its offer connection is closed for any reason
  (`replaced`, `expired`, `nopair`, a network drop) without a claim, it drops that code and shows a new one (new
  code, new `h`, new owner). After a claim, or once the 10 minutes are over, the `h` stays refused until 24 hours
  after the first offer (tombstone); after that the relay has forgotten it. Clients must never reuse a code or `h`:
  every code is fresh random.
- **Rooms**: the first accepted PC hello creates the room (empty allow-list). A room with any connection open does not
  expire. A room idle for 90 days is deleted and comes back empty on the PC's next hello, and nothing tells the PC;
  so the PC must send its signed `allow` after every `ok`, not only when the list changes.

### Test vector

Private scalar d = SHA-256(UTF-8 `rigcontrol-link test vector`) = `TYZpsf8nnOtU2hG5CrIbpr2t5zLCvJS1Fg8Zz6CoLWU`

- pub (SPKI) `MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXBkWlhfTI7ssupvLHvSM3b0LyJ_7IruW1xR88mgLuZ7ZZQnX8ZtbGtG1Ey6WBVDQvVAtWs-SGyTcDNBTqzAmPA`
- id `s-cAp3rrut4EfrWaw6fc5Ykr_lc0qYFZmXVsTT2CQ8k`
- nonce (bytes 0..31) `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`
- hello text `rigcontrol-link-v1|AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8|s-cAp3rrut4EfrWaw6fc5Ykr_lc0qYFZmXVsTT2CQ8k`
- a valid signature of it `TNnWHUJntqq5_mS-kax7XgIs-eYlApHejbhUY_Q3AbTqUq7UGQTsN0zzRTTceiAJhXBDU0kaMOTkHKkUPoF_Lw`

## Tests

```
npm ci
npm run typecheck
npm test
```

The tests (`test/`) run inside the Workers runtime through `@cloudflare/vitest-plugin` (the renamed
`@cloudflare/vitest-pool-workers`), against the real Durable Objects with hibernating WebSockets. They cover hello
signatures (valid, other nonce, other key, wrong length), key form, room checks, the allow-list (signature, order,
removal, empty list, phones may not send it), routing (phone to PC only, PC to allowed phones only, `gone`,
`from` set by the relay, newest connection wins), presence, the 64 KiB limit (exact boundary and the forwarded
frame), the 20 frames/s limit and close after 100 excess frames, binary frames, hello timeout (by the alarm and for a
late hello), what is stored, 90-day expiry, the 32-phone cap, `Origin` refused, the upgrade limits (stand-in
rate-limit bindings passed to the Worker's `fetch`, plus one run against the local bindings), IPv6 /64, alarms not
rewritten per connection, and pairing (happy path, unknown, expired, withdrawn, owner match replaces, owner mismatch
refused, owner required, single use, 24-hour tombstone, late first frame), plus the test vector above.

`npm run check-deploy` builds the Worker without uploading it (`wrangler deploy --dry-run`).

## Deploy (by hand; CI never deploys)

1. `npx wrangler login` (or `CLOUDFLARE_API_TOKEN` with Workers Scripts edit rights).
2. `npx wrangler deploy` from this folder. Migration `v1` creates the SQLite-backed Durable Object classes `Room` and
   `Pairing` (works on the free plan). The rate-limit `namespace_id`s in `wrangler.jsonc` must not be used by another Worker on the
   same account.
3. The relay is then at `wss://rigcontrol-relay.<account>.workers.dev`; a custom domain can be added in the dashboard
   or with a `routes` entry.

Changing a Durable Object class name later needs a new migration (`renamed_classes`), never an edit of `v1`.

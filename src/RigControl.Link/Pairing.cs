using System.Text.Json.Nodes;

namespace RigControl.Link;

/// <summary>
/// A phone as the PC knows it after pairing. Keys are base64url SubjectPublicKeyInfo DER. <see cref="Epoch"/> is this
/// pairing's epoch (16 random bytes, base64url): it is part of the session key and the AAD, so a new pairing of the
/// same phone never accepts frames sealed under an earlier one.
/// </summary>
public sealed record PairedPhone(string Id, string Name, string SigningKey, string AgreementKey, string Epoch);

/// <summary>A PC as the phone knows it after pairing. <see cref="Relay"/> is the relay base address it was paired through.</summary>
public sealed record LinkedPc(string Id, string Name, string SigningKey, string AgreementKey, string Relay, string Epoch);

/// <summary>
/// The pairing rendezvous on <c>/v1/pair</c>. The PC offers, the phone claims; each box is sealed with
/// K = HKDF(code), so the relay (which only sees h = SHA-256 of the code) can read neither.
///
/// Wire details (relay/README.md): the URL carries <c>?h=</c>; no hello on <c>/v1/pair</c>; the PC gets
/// <c>{"t":"ok"}</c> for an accepted offer (ignored here); the relay hands the boxes over as
/// <c>{"t":"claim","h":..,"box":..}</c> (to the PC) and <c>{"t":"offer","h":..,"box":..}</c> (to the phone), then closes
/// both with 1000 "paired".
/// </summary>
public static class Pairing
{
    /// <summary>
    /// PC side: posts the offer for <paramref name="code"/> and waits (up to <paramref name="lifetime"/>) for a
    /// phone's claim. Throws <see cref="LinkException"/>: "expired", "interrupted", "wrongcode", "badbox", or a relay code.
    /// </summary>
    public static async Task<PairedPhone> OfferAsync(IRelayTransport transport, Uri relay, string code, DeviceKeys pc, string pcName,
        TimeSpan lifetime, CancellationToken ct)
    {
        var k = PairingCode.BoxKey(code);
        var epoch = B64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(LinkProtocol.EpochBytes));
        var box = LinkCrypto.SealBox(k, new JsonObject
        {
            ["pc"] = pc.SigningPublicB64,
            ["pcAgree"] = pc.AgreementPublicB64,
            ["name"] = LinkDefaults.CleanName(pcName, "PC"),
            ["epoch"] = epoch,
        });
        var h = PairingCode.RendezvousHash(code);
        // "owner": 32 random bytes made for this code only and kept only in memory (the relay stores its hash).
        var owner = B64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var offer = new JsonObject { ["t"] = "offer", ["h"] = h, ["owner"] = owner, ["box"] = box };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(lifetime);
        // The relay keeps the offer only while this connection stays open. When it closes for any reason without a
        // claim ("replaced", "expired", "nopair", a network drop), this code is finished: it is never offered again,
        // and the caller shows a new code.
        try
        {
            var conn = await transport.ConnectAsync(LinkEndpoints.Pair(relay, h), cts.Token).ConfigureAwait(false);
            await using (conn.ConfigureAwait(false))
            {
                await conn.SendAsync(offer.ToJsonString(), cts.Token).ConfigureAwait(false);
                var answer = await WaitForBox(conn, "claim", cts.Token).ConfigureAwait(false);
                var o = LinkCrypto.OpenBox(k, answer);
                var sign = Key(o, "phone");
                var agree = Key(o, "phoneAgree");
                var id = DeviceKeys.IdOf(B64Url.Decode(sign));
                if (id == pc.Id) throw new LinkException("badbox", "The pairing answer was the PC's own.");
                return new PairedPhone(id, LinkDefaults.CleanName(RelayJson.Str(o, "name"), "Phone"), sign, agree, epoch);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LinkException("expired", "The code ran out before a phone used it.");
        }
    }

    /// <summary>
    /// Phone side: claims the PC's offer with the code the user typed or scanned. Throws <see cref="LinkException"/>:
    /// "nopair" (unknown or expired code, from the relay), "wrongcode", "badbox", "timeout", "interrupted".
    /// </summary>
    public static async Task<LinkedPc> ClaimAsync(IRelayTransport transport, Uri relay, string code, DeviceKeys phone, string phoneName,
        CancellationToken ct, TimeSpan? timeout = null)
    {
        var normalized = PairingCode.Normalize(code) ?? throw new LinkException("badcode", "That isn't a pairing code (20 letters and digits).");
        var k = PairingCode.BoxKey(normalized);
        var box = LinkCrypto.SealBox(k, new JsonObject
        {
            ["phone"] = phone.SigningPublicB64,
            ["phoneAgree"] = phone.AgreementPublicB64,
            ["name"] = LinkDefaults.CleanName(phoneName, "Phone"),
        });
        var h = PairingCode.RendezvousHash(normalized);
        var claim = new JsonObject { ["t"] = "claim", ["h"] = h, ["box"] = box };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            var conn = await transport.ConnectAsync(LinkEndpoints.Pair(relay, h), cts.Token).ConfigureAwait(false);
            await using (conn.ConfigureAwait(false))
            {
                await conn.SendAsync(claim.ToJsonString(), cts.Token).ConfigureAwait(false);
                var answer = await WaitForBox(conn, "offer", cts.Token).ConfigureAwait(false);
                var o = LinkCrypto.OpenBox(k, answer);
                var sign = Key(o, "pc");
                var agree = Key(o, "pcAgree");
                var id = DeviceKeys.IdOf(B64Url.Decode(sign));
                if (id == phone.Id) throw new LinkException("badbox", "The pairing answer was the phone's own.");
                var epoch = RelayJson.Str(o, "epoch");
                if (!LinkProtocol.IsEpoch(epoch)) throw new LinkException("badbox", "The pairing answer had no proper epoch.");
                return new LinkedPc(id, LinkDefaults.CleanName(RelayJson.Str(o, "name"), "PC"), sign, agree, relay.ToString(), epoch!);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LinkException("timeout", "The relay did not answer in time.");
        }
    }

    private static async Task<string> WaitForBox(IRelayConnection conn, string expected, CancellationToken ct)
    {
        while (true)
        {
            var text = await conn.ReceiveAsync(ct).ConfigureAwait(false);
            if (text is null)
            {
                if (conn.CloseReason is { } reason) throw RelaySession.RelayFailure(reason);
                throw new LinkException("interrupted", "The connection to the relay was lost during pairing.");
            }
            var o = RelayJson.Parse(text);
            if (o is null) continue;
            var t = RelayJson.Str(o, "t");
            if (t == "error") throw RelaySession.RelayFailure(RelayJson.Str(o, "code"));
            if (t == expected && RelayJson.Str(o, "box") is { } box) return box;
            // "challenge", "ok" and anything newer are not part of pairing for this client.
        }
    }

    private static string Key(JsonObject o, string name)
    {
        var s = RelayJson.Str(o, name);
        if (s is null || !B64Url.TryDecode(s, out var spki) || !DeviceKeys.IsValidPublicKey(spki))
            throw new LinkException("badbox", "The pairing answer did not hold a proper key.");
        return s;
    }
}

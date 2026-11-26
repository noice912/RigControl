// LinkProbe: acts as a phone for RigControl Link (docs/protocols/link.md), for testing a PC through a real relay.
//
//   dotnet run --project tools/LinkProbe -- pair --relay wss://<relay host> --code ABCD-EFGH-JKMN-PQRS-TVWX [--name "Probe"]
//   dotnet run --project tools/LinkProbe -- pair --qr "rigcontrol://pair?relay=...&code=..."
//   dotnet run --project tools/LinkProbe -- snapshot
//   dotnet run --project tools/LinkProbe -- do capture.saveReplay
//   dotnet run --project tools/LinkProbe -- do lighting.setEffect --args "{\"effect\":\"wave\"}"
//
// State lives in linkprobe-state.json in the current folder: the probe's own keys (in plain text: this is a test
// tool, not a phone; delete the file to forget it), the linked PC and the last counter accepted from it.
// Exit codes: 0 done, 1 refused or failed, 2 wrong usage.

using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Link;

const string StateFile = "linkprobe-state.json";

if (args.Length == 0) return Usage();
var opts = ParseOptions(args.Skip(1).ToArray());
var transport = new WebSocketRelayTransport();
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    switch (args[0])
    {
        case "pair":
            return await Pair();
        case "snapshot":
            return await WithPhone(async phone =>
            {
                var snap = await phone.RequestSnapshotAsync(cts.Token);
                Console.WriteLine(snap.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            });
        case "do":
            if (args.Length < 2) return Usage();
            JsonObject? actionArgs = null;
            if (opts.TryGetValue("args", out var raw))
            {
                actionArgs = JsonNode.Parse(raw) as JsonObject;
                if (actionArgs is null) return Fail("--args must be a JSON object.");
            }
            return await WithPhone(async phone =>
            {
                var r = await phone.SendActionAsync(args[1], actionArgs, cts.Token);
                Console.WriteLine(r.Ok ? "ok" : "refused: " + r.Error);
                return r.Ok ? 0 : 1;
            });
        default:
            return Usage();
    }
}
catch (LinkException ex)
{
    return Fail($"{ex.Code}: {ex.Message}");
}
catch (OperationCanceledException)
{
    return Fail("Stopped (timed out or cancelled).");
}

async Task<int> Pair()
{
    using var keys = DeviceKeys.Create();
    var name = opts.GetValueOrDefault("name") ?? "LinkProbe";
    LinkedPc pc;
    if (opts.TryGetValue("qr", out var qr))
    {
        pc = await PhoneSide.PairFromQrAsync(transport, qr, keys, name, cts.Token);
    }
    else
    {
        if (!opts.TryGetValue("relay", out var relay) || !opts.TryGetValue("code", out var code)) return Usage();
        pc = await PhoneSide.PairAsync(transport, relay, code, keys, name, cts.Token);
    }
    var state = new JsonObject
    {
        ["keys"] = B64Url.Encode(keys.ExportPrivate()),
        ["pc"] = JsonSerializer.SerializeToNode(pc),
        ["lastIn"] = 0,
    };
    File.WriteAllText(StateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Linked to \"{pc.Name}\" ({pc.Id}). This probe's id: {keys.Id}");
    return 0;
}

async Task<int> WithPhone(Func<PhoneSide, Task<int>> work)
{
    if (!File.Exists(StateFile)) return Fail($"No {StateFile} here: run \"pair\" first.");
    var state = JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject();
    using var keys = DeviceKeys.ImportPrivate(B64Url.Decode((string)state["keys"]!));
    var pc = state["pc"].Deserialize<LinkedPc>() ?? throw new LinkException("badstate", "The state file has no PC.");
    var lastIn = (long?)state["lastIn"] ?? 0;
    await using var phone = new PhoneSide(transport, pc, keys, lastIn, new PhoneSideOptions
    {
        Relay = new RelayLinkOptions { Log = s => Console.Error.WriteLine("[relay] " + s) },
    });
    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
    {
        connect.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await phone.ConnectAsync(connect.Token);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            return Fail("Could not connect to the relay" + (phone.LastError is { } e ? $" ({e})" : "") + ".");
        }
    }
    try
    {
        return await work(phone);
    }
    finally
    {
        state["lastIn"] = phone.LastIn;
        File.WriteAllText(StateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < a.Length) d[a[i][2..]] = a[++i];
    }
    return d;
}

static int Usage()
{
    Console.Error.WriteLine("""
        LinkProbe - a pretend phone for RigControl Link.
          pair --relay <wss://host> --code <code> [--name <name>]
          pair --qr "<rigcontrol://pair?... text>"
          snapshot
          do <action> [--args <json object>]
        Allowed actions:
        """ + " " + string.Join(", ", LinkActions.Allowed));
    return 2;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

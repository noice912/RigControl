using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigControl.Presence;

/// <summary>The Discord account a connection reached, as Discord's READY answer names it. Never a token: READY has none.</summary>
public sealed record DiscordUser(string Id, string Name);

/// <summary>One Discord card = one connection with one Discord application id.</summary>
public interface IDiscordConnection : IDisposable
{
    string ClientId { get; }
    bool Connected { get; }
    /// <summary>The account that answered READY on this connection, when Discord said.</summary>
    DiscordUser? User { get; }
    /// <summary>Connects and waits for READY. False when Discord is not running or refused this id.</summary>
    bool Connect();
    /// <summary>Sets the card. Returns Discord's error message, or null when it was accepted.</summary>
    string? SetActivity(JsonObject? activity);
}

/// <summary>What RigControl can see of Discord from this Windows session.</summary>
public enum DiscordLook
{
    /// <summary>No Discord desktop app is running.</summary>
    NotRunning,
    /// <summary>Discord is running, but only under another Windows user.</summary>
    OtherUser,
    /// <summary>This user's Discord is running but did not answer the last time it was asked.</summary>
    NoAnswer,
    /// <summary>This user's Discord is running.</summary>
    Running,
}

public interface IDiscordClient
{
    /// <summary>What can be seen of Discord right now. Another Windows user's Discord never counts as this user's.</summary>
    DiscordLook Look();
    /// <summary>True when Discord is running for this user (in this Windows session).</summary>
    bool IsRunning() => Look() is DiscordLook.Running or DiscordLook.NoAnswer;
    IDiscordConnection Open(string clientId);
}

/// <summary>One open link to a Discord client.</summary>
public interface IDiscordPipe : IDisposable
{
    Stream Stream { get; }
    /// <summary>True only when the Discord on the other end is known to belong to this user (same Windows session).</summary>
    bool Mine { get; }
}

/// <summary>
/// Where Discord's local IPC lives. On Windows these are the named pipes discord-ipc-0..9; on Linux and macOS they
/// are Unix sockets with the same names (not built yet: add another implementation of this).
/// </summary>
public interface IDiscordTransport
{
    /// <summary>The numbers (0-9) of the Discord links that exist right now, lowest first. Opens nothing.</summary>
    IReadOnlyList<int> List();
    /// <summary>Opens one, or returns null when it can't be opened.</summary>
    IDiscordPipe? Open(int index);
}

/// <summary>Windows: \\.\pipe\discord-ipc-N. The pipe namespace is shared by every signed-in user, so each pipe's owner is checked.</summary>
public sealed class WindowsPipeTransport : IDiscordTransport
{
    private const string Prefix = "discord-ipc-";

    /// <summary>How much of RigControl's own identity the program on the other end of a pipe gets.</summary>
    internal const System.Security.Principal.TokenImpersonationLevel PipeImpersonation = System.Security.Principal.TokenImpersonationLevel.Identification;

    public IReadOnlyList<int> List()
    {
        try
        {
            var found = new SortedSet<int>();
            foreach (var p in Directory.GetFiles(@"\\.\pipe\"))
            {
                var name = Path.GetFileName(p);
                if (name.Length == Prefix.Length + 1 && name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[^1]))
                    found.Add(name[^1] - '0');
            }
            return found.ToList();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    public IDiscordPipe? Open(int index)
    {
        NamedPipeClientStream? p = null;
        try
        {
            // "Identification" only: whoever made this pipe can see who RigControl is, but can never act with
            // RigControl's administrator rights. The pipe names are open to every program and every signed-in user,
            // and the pipe has to be opened before Windows says who made it. Discord never needs more than this.
            p = new NamedPipeClientStream(".", Prefix + index, PipeDirection.InOut, PipeOptions.Asynchronous, PipeImpersonation);
            p.Connect(1000);
            // Which program made this pipe, and is it in our Windows session? If Windows won't say, it isn't ours.
            var mine = GetNamedPipeServerProcessId(p.SafePipeHandle, out var pid)
                       && WinSession.OfProcess((int)pid) == WinSession.Current;
            return new Pipe(p, mine);
        }
        catch
        {
            p?.Dispose();
            return null;
        }
    }

    private sealed class Pipe : IDiscordPipe
    {
        private readonly NamedPipeClientStream _p;
        public Pipe(NamedPipeClientStream p, bool mine) { _p = p; Mine = mine; }
        public Stream Stream => _p;
        public bool Mine { get; }
        public void Dispose() { try { _p.Dispose(); } catch { /* ignore */ } }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverPid);
}

/// <summary>
/// Discord's local IPC (the same link the official Rich Presence libraries use): frames of
/// [int32 opcode][int32 length][UTF-8 JSON]. Ported from RichPresence's Connect-Discord / Set-Activity.
///
/// Rules:
///  - Only a Discord running in RigControl's own Windows session is ever spoken to. Another user's is passed over.
///  - The link is chosen once and every card uses it, so all cards land on the same Discord client.
/// </summary>
public sealed class DiscordClient : IDiscordClient
{
    private static readonly TimeSpan OwnerCache = TimeSpan.FromSeconds(30);

    private readonly IDiscordTransport _transport;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<int, (bool Mine, DateTime At)> _owners = new();
    private int? _chosen;
    private int _live;
    private bool _noAnswer;

    public DiscordClient() : this(new WindowsPipeTransport()) { }

    public DiscordClient(IDiscordTransport transport, Func<DateTime>? now = null)
    {
        _transport = transport;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The link every card uses, once one answered READY.</summary>
    internal int? Chosen { get { lock (_gate) return _chosen; } }

    public DiscordLook Look()
    {
        var list = _transport.List();
        lock (_gate)
        {
            foreach (var k in _owners.Keys.Where(k => !list.Contains(k)).ToList()) _owners.Remove(k);
            if (_chosen is { } c && !list.Contains(c) && _live == 0) _chosen = null;
            if (list.Count == 0)
            {
                _noAnswer = false;
                return DiscordLook.NotRunning;
            }
        }
        var mine = false;
        var unknown = false; // a pipe is there but could not be opened, so nobody knows whose it is
        foreach (var i in list)
        {
            bool? known;
            lock (_gate)
            {
                known = _chosen == i && _live > 0 ? true
                    : _owners.TryGetValue(i, out var o) && _now() - o.At < OwnerCache ? o.Mine
                    : null;
            }
            if (known is null)
            {
                // Opened only to ask who owns it; nothing is sent.
                // A pipe that can't be opened is not remembered, so the next look asks again.
                using var p = _transport.Open(i);
                if (p is null)
                {
                    unknown = true;
                    continue;
                }
                known = p.Mine;
                lock (_gate) _owners[i] = (p.Mine, _now());
            }
            if (known == true) { mine = true; break; }
        }
        lock (_gate)
        {
            if (!mine)
            {
                _noAnswer = false;
                // "Another Windows user" is only said when every pipe was asked and none is this user's. A Discord
                // that is starting, closing or busy just did not answer; no card is sent to a pipe until it is
                // known to be this user's, so this never lets another user's Discord through.
                return unknown ? DiscordLook.NoAnswer : DiscordLook.OtherUser;
            }
            return _noAnswer ? DiscordLook.NoAnswer : DiscordLook.Running;
        }
    }

    public IDiscordConnection Open(string clientId) => new DiscordConnection(clientId, this);

    // ---- what a connection needs from the set it belongs to

    internal IDiscordTransport Transport => _transport;

    /// <summary>The links to try, in order: the chosen one alone, else every one that exists.</summary>
    internal (IReadOnlyList<int> Indexes, bool Pinned) Candidates()
    {
        lock (_gate)
        {
            if (_chosen is { } c) return (new[] { c }, true);
        }
        return (_transport.List(), false);
    }

    internal void Owner(int index, bool mine)
    {
        lock (_gate) _owners[index] = (mine, _now());
    }

    /// <summary>A card connected on this link: it is now the one for every card.</summary>
    internal void Connected(int index)
    {
        lock (_gate)
        {
            _chosen = index;
            _live++;
            _noAnswer = false;
        }
    }

    internal void Closed()
    {
        lock (_gate) { if (_live > 0) _live--; }
    }

    /// <summary>
    /// The chosen link stopped working. While other cards are still connected on it they keep it (so cards never
    /// split across two Discord clients); once none is, the next card looks again from the start.
    /// </summary>
    internal bool Unpin()
    {
        lock (_gate)
        {
            if (_live > 0) return false;
            _chosen = null;
            return true;
        }
    }

    /// <summary>Whether this user's Discord answered the handshake at all (a refused app id is still an answer).</summary>
    internal void Answered(bool answered)
    {
        lock (_gate) _noAnswer = !answered;
    }
}

internal sealed class DiscordConnection : IDiscordConnection
{
    private readonly DiscordClient _owner;
    private readonly int _pid = Environment.ProcessId;
    private IDiscordPipe? _pipe;
    /// <summary>True once this connection answered READY and counts as a holder of the chosen link.</summary>
    private bool _counted;

    public DiscordConnection(string clientId, DiscordClient owner)
    {
        ClientId = clientId;
        _owner = owner;
    }

    public string ClientId { get; }
    public bool Connected => _pipe is { } p && p.Stream.CanWrite && (p.Stream is not PipeStream ps || ps.IsConnected);
    public DiscordUser? User { get; private set; }
    /// <summary>Discord's reason when it refused the id at the handshake.</summary>
    public string? Refused { get; private set; }

    public bool Connect()
    {
        if (Connected) return true;
        Close();
        Refused = null;
        User = null;
        // Without a real application id there is nothing to say to Discord: nothing is opened and nothing is sent.
        if (!AppIds.IsId(ClientId)) return false;
        var (indexes, pinned) = _owner.Candidates();
        var result = Try(indexes);
        if (result == Outcome.Ready) return true;
        // The link every card used is gone or silent: look at all of them again, unless other cards still hold it.
        if (pinned && result != Outcome.Refused && _owner.Unpin())
        {
            result = Try(_owner.Transport.List());
            if (result == Outcome.Ready) return true;
        }
        if (result == Outcome.Silent) _owner.Answered(false);
        return false;
    }

    private enum Outcome { None, Silent, Refused, Ready }

    /// <summary>Tries each link in turn, passing over one that belongs to another user or does not answer READY.</summary>
    private Outcome Try(IReadOnlyList<int> indexes)
    {
        var best = Outcome.None;
        foreach (var i in indexes)
        {
            IDiscordPipe? p;
            try { p = _owner.Transport.Open(i); }
            catch { p = null; }
            if (p is null) continue;
            _owner.Owner(i, p.Mine);
            if (!p.Mine)
            {
                // Another Windows user's Discord: never spoken to.
                p.Dispose();
                continue;
            }
            _pipe = p;
            string? r;
            try
            {
                Send(0, new JsonObject { ["v"] = 1, ["client_id"] = ClientId }.ToJsonString());
                r = Read();
            }
            catch
            {
                r = null;
            }
            if (!string.IsNullOrEmpty(r) && Ready(r, out var user))
            {
                User = user;
                _counted = true;
                _owner.Connected(i);
                return Outcome.Ready;
            }
            Close();
            if (string.IsNullOrEmpty(r))
            {
                if (best == Outcome.None) best = Outcome.Silent;
                continue;
            }
            // An answer that isn't READY: Discord refused this app id. Remember why and try the next link.
            Refused ??= MessageOf(r) ?? "Discord refused it";
            best = Outcome.Refused;
            _owner.Answered(true);
        }
        return best;
    }

    /// <summary>True for Discord's READY answer; the account is read from it when it is there (id and shown name only).</summary>
    internal static bool Ready(string json, out DiscordUser? user)
    {
        user = null;
        try
        {
            var j = JsonNode.Parse(json);
            if (Text(j?["evt"]) != "READY") return false;
            var u = j?["data"]?["user"];
            var id = Text(u?["id"]);
            if (id.Length is < 1 or > 20 || !id.All(char.IsAsciiDigit)) return true;
            var name = Clean(Text(u?["global_name"]));
            if (name == "") name = Clean(Text(u?["username"]));
            user = new DiscordUser(id, name == "" ? "your account" : name);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>A name is shown and logged: no control characters, and not endless.</summary>
    private static string Clean(string v)
    {
        var t = new string(v.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return t.Length > 64 ? t[..64] : t;
    }

    public string? SetActivity(JsonObject? activity)
    {
        if (_pipe is null || !AppIds.IsId(ClientId)) throw new IOException("Discord is not connected.");
        var args = new JsonObject { ["pid"] = _pid };
        if (activity is not null) args["activity"] = activity;
        var cmd = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["nonce"] = Guid.NewGuid().ToString(), ["args"] = args };
        Send(1, cmd.ToJsonString());
        var r = Read() ?? throw new IOException("Discord did not answer.");
        if (!r.Contains("\"ERROR\"", StringComparison.Ordinal)) return null;
        try { return JsonNode.Parse(r)?["data"]?["message"]?.GetValue<string>() ?? r; }
        catch { return r; }
    }

    private static string? MessageOf(string json)
    {
        try { return JsonNode.Parse(json)?["message"]?.GetValue<string>(); }
        catch { return null; }
    }

    private void Send(int op, string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + data.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
        data.CopyTo(frame, 8);
        var s = _pipe!.Stream;
        s.Write(frame, 0, frame.Length);
        s.Flush();
    }

    /// <summary>One frame, never waiting more than 5 s; a late answer would be mistaken for the next one, so the pipe is dropped.</summary>
    private string? Read()
    {
        try
        {
            var h = new byte[8];
            if (!ReadExactly(h)) return null;
            var len = BitConverter.ToInt32(h, 4);
            if (len <= 0) return "";
            if (len > 1 << 20) { Close(); return null; }
            var b = new byte[len];
            return ReadExactly(b) ? Encoding.UTF8.GetString(b) : null;
        }
        catch
        {
            Close();
            return null;
        }
    }

    private bool ReadExactly(byte[] buf)
    {
        var got = 0;
        while (got < buf.Length)
        {
            if (_pipe is null) return false;
            var t = _pipe.Stream.ReadAsync(buf, got, buf.Length - got);
            if (!t.Wait(5000)) { Close(); return false; }
            if (t.Result <= 0) { Close(); return false; }
            got += t.Result;
        }
        return true;
    }

    /// <summary>Drops the link; if it had answered READY, the set is told one card fewer holds it.</summary>
    private void Close()
    {
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;
        if (!_counted) return;
        _counted = false;
        _owner.Closed();
    }

    public void Dispose() => Close();
}

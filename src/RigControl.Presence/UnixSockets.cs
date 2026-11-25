using System.Net.Sockets;
using RigControl.Core.Platform;

namespace RigControl.Presence;

/// <summary>
/// One folder Discord may keep its links in. <see cref="Root"/> is the folder that has to be this user's alone;
/// <see cref="Path"/> is the root itself or a folder below it (Flatpak and Snap keep theirs one or two levels down).
/// </summary>
public sealed record SocketFolder(string Root, string Path);

/// <summary>Where Discord puts discord-ipc-0..9 on Linux and macOS, in the order they are tried.</summary>
public static class UnixSocketFolders
{
    /// <param name="env">Reads an environment variable (tests pass their own).</param>
    public static IReadOnlyList<SocketFolder> For(HostPlatform platform, Func<string, string?> env)
    {
        var list = new List<SocketFolder>();
        void Add(string? root, string below = "")
        {
            root = Clean(root);
            if (root is null) return;
            var path = below == "" ? root : root + "/" + below;
            if (list.All(f => f.Path != path)) list.Add(new SocketFolder(root, path));
        }
        if (platform == HostPlatform.Linux)
        {
            var run = env("XDG_RUNTIME_DIR");
            Add(run);                                   // Discord installed the normal way
            Add(run, "app/com.discordapp.Discord");     // Flatpak
            Add(run, "snap.discord");                   // Snap
            Add(run, "snap.discord-canary");
            Add(run, "app/com.discordapp.DiscordCanary");
            Add(run, "app/com.discordapp.DiscordPTB");
            Add(run, "snap.discord-ptb");
            // Where Discord falls back to when there is no XDG_RUNTIME_DIR. /tmp is open to everyone, so a link
            // there is found but never used (see UnixSocketTransport).
            Add(env("TMPDIR"));
            Add(env("TMP"));
            Add(env("TEMP"));
            Add("/tmp");
        }
        else if (platform == HostPlatform.MacOS)
        {
            // macOS gives every user a temporary folder of their own.
            Add(env("TMPDIR"));
        }
        return list;
    }

    /// <summary>A full path without the slash at its end, or null for anything else.</summary>
    private static string? Clean(string? folder)
    {
        folder = (folder ?? "").Trim();
        if (!folder.StartsWith('/') || folder.Contains('\0')) return null;
        folder = folder.TrimEnd('/');
        return folder == "" ? null : folder;
    }
}

/// <summary>What the socket search asks of the file system. Abstracted so tests can drive it on any system.</summary>
internal interface ISocketFiles
{
    bool Exists(string path);
    /// <summary>Null when only this user can get into the folder; otherwise why that can't be said.</summary>
    string? WhyNotPrivate(string folder);
    /// <summary>True when the path is a link to somewhere else (or can't be looked at).</summary>
    bool IsLink(string path);
    /// <summary>Where a link ends up, as a full path; null when the path is not a link.</summary>
    string? LinkTarget(string path);
    /// <summary>When the file was made or last changed; null when that can't be read.</summary>
    DateTime? Made(string path);
}

/// <summary>The rule for "this folder is this user's alone", on its own so it can be tested everywhere.</summary>
internal static class SocketTrust
{
    private const UnixFileMode Others =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public const string OpenToOthers = "other users of this computer can get into that folder, so RigControl can't tell that it is your Discord";

    /// <summary>
    /// A folder with no rights at all for the group and for everyone else (mode 0700 or less). Only its owner can
    /// look inside such a folder, so a program that is not root and does see a file in it is the owner. That is
    /// how ownership is shown without asking the system for the owner's number.
    /// </summary>
    public static string? WhyNotPrivate(UnixFileMode mode) => (mode & Others) != 0 ? OpenToOthers : null;
}

internal sealed class RealSocketFiles : ISocketFiles
{
    public bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    public string? WhyNotPrivate(string folder)
    {
        if (OperatingSystem.IsWindows()) return "this is Windows, where Discord uses pipes instead";
        if (Environment.IsPrivilegedProcess) return "RigControl is running as root, so it can't tell whose Discord it is";
        try
        {
            var d = new DirectoryInfo(folder);
            if (!d.Exists) return "the folder is gone";
            if (d.LinkTarget is not null) return "the folder is a link to somewhere else";
            return SocketTrust.WhyNotPrivate(d.UnixFileMode);
        }
        catch
        {
            return "the folder's permissions can't be read";
        }
    }

    public bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch { return true; }
    }

    public string? LinkTarget(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch { return ""; } // can't be followed: matches nothing, so it is refused
    }

    public DateTime? Made(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }
}

/// <summary>
/// Linux and macOS: Discord's links are Unix sockets named discord-ipc-0..9. They are looked for in the folders
/// <see cref="UnixSocketFolders"/> lists, in that order.
///
/// Only this user's own Discord is ever spoken to. The check, exactly:
///  - RigControl is not running as root.
///  - The root folder (XDG_RUNTIME_DIR, or the temporary folder) is a real folder, not a link, and has no rights
///    for the group or for others (mode 0700 or less). Only the owner can look into such a folder, so seeing the
///    socket there shows that the folder is this user's, and nobody else can have put anything below it.
///  - No folder between the root and the socket is a link.
///  - The socket is not a link, or it is a link to discord-ipc-N in another of these folders under the same root
///    (people link the Flatpak socket into XDG_RUNTIME_DIR by hand).
/// A socket that fails this (anything in /tmp, which everyone can write to) is not used, and the log says so once.
/// The owner's user number is not read: .NET has no call for it, and nothing outside .NET is used here.
/// </summary>
public sealed class UnixSocketTransport : IDiscordTransport
{
    internal const string Prefix = "discord-ipc-";
    private static readonly TimeSpan DeadFor = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<SocketFolder> _folders;
    private readonly ISocketFiles _files;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    /// <summary>Socket files nobody listens on (left behind when Discord was stopped the hard way).</summary>
    private readonly Dictionary<string, (DateTime? Made, DateTime At)> _dead = new();
    private readonly HashSet<string> _said = new();

    public UnixSocketTransport()
        : this(UnixSocketFolders.For(PlatformInfo.Current, Environment.GetEnvironmentVariable), new RealSocketFiles()) { }

    internal UnixSocketTransport(IReadOnlyList<SocketFolder> folders, ISocketFiles files, Func<DateTime>? now = null)
    {
        _folders = folders;
        _files = files;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Where a sentence for the log goes (a socket that was found but is not used). Each is said once.</summary>
    public Action<string>? Note { get; set; }

    public IReadOnlyList<int> List()
    {
        var found = new List<int>();
        for (var i = 0; i <= 9; i++)
        {
            try
            {
                if (Usable(i, skipDead: true).Count > 0) found.Add(i);
            }
            catch { /* a folder that can't be read has nothing to offer */ }
        }
        return found;
    }

    public IDiscordPipe? Open(int index)
    {
        if (index is < 0 or > 9) return null;
        List<string> paths;
        try { paths = Usable(index, skipDead: false); }
        catch { return null; }
        foreach (var path in paths)
        {
            Socket? s = null;
            try
            {
                s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var t = s.ConnectAsync(new UnixDomainSocketEndPoint(path));
                if (!t.Wait(1000))
                {
                    s.Dispose();
                    continue;
                }
                lock (_gate) _dead.Remove(path);
                // The folder check above is what makes it this user's: only a socket that passed it gets here.
                return new Link(new NetworkStream(s, ownsSocket: true));
            }
            catch (Exception ex)
            {
                try { s?.Dispose(); } catch { /* ignore */ }
                // Nobody is listening: an old file. It is left alone until Discord makes it anew.
                if (ex.GetBaseException() is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    lock (_gate) _dead[path] = (_files.Made(path), _now());
                }
            }
        }
        return null;
    }

    /// <summary>The sockets with this number that exist and passed the check, in the order of the folders.</summary>
    internal List<string> Usable(int index, bool skipDead)
    {
        var list = new List<string>();
        foreach (var f in _folders)
        {
            var path = f.Path + "/" + Prefix + index;
            if (!_files.Exists(path)) continue;
            if (WhyNot(f, path) is { } why)
            {
                Say(f.Path + "|" + why, $"Discord status: the Discord link in {f.Path} isn't used: {why}.");
                continue;
            }
            if (skipDead)
            {
                lock (_gate)
                {
                    if (_dead.TryGetValue(path, out var d))
                    {
                        if (d.Made is not null && d.Made == _files.Made(path) && _now() - d.At < DeadFor) continue;
                        _dead.Remove(path);
                    }
                }
            }
            list.Add(path);
        }
        return list;
    }

    /// <summary>Null when the socket is provably this user's; otherwise why not (see the class notes).</summary>
    internal string? WhyNot(SocketFolder f, string path)
    {
        if (_files.WhyNotPrivate(f.Root) is { } why) return why;
        if (!BelowRootWithoutLinks(f)) return "a folder on the way to it is a link to somewhere else";
        if (_files.LinkTarget(path) is { } target)
        {
            var ok = _folders.Any(g => g.Root == f.Root && IsSocketIn(g, target) && BelowRootWithoutLinks(g));
            if (!ok) return "it is a link to somewhere else";
        }
        return null;
    }

    private static bool IsSocketIn(SocketFolder g, string path) =>
        path.Length == g.Path.Length + 1 + Prefix.Length + 1 && path.StartsWith(g.Path + "/" + Prefix, StringComparison.Ordinal)
        && char.IsAsciiDigit(path[^1]);

    private bool BelowRootWithoutLinks(SocketFolder f)
    {
        if (f.Path == f.Root) return true;
        if (!f.Path.StartsWith(f.Root + "/", StringComparison.Ordinal)) return false;
        var at = f.Root;
        foreach (var part in f.Path[(f.Root.Length + 1)..].Split('/'))
        {
            if (part is "" or "." or "..") return false;
            at += "/" + part;
            if (_files.IsLink(at)) return false;
        }
        return true;
    }

    private void Say(string key, string sentence)
    {
        var note = Note;
        if (note is null) return; // nobody to tell yet: it is said once somebody listens
        lock (_gate)
        {
            if (!_said.Add(key)) return;
        }
        try { note(sentence); } catch { /* the log is not worth failing for */ }
    }

    private sealed class Link : IDiscordPipe
    {
        private readonly NetworkStream _s;
        public Link(NetworkStream s) => _s = s;
        public Stream Stream => _s;
        public bool Mine => true;
        public void Dispose() { try { _s.Dispose(); } catch { /* ignore */ } }
    }
}

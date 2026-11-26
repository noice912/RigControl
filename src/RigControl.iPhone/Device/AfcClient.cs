using System.Buffers.Binary;
using System.Text;

namespace RigControl.iPhone.Device;

/// <summary>
/// Apple File Conduit (com.apple.afc): file access to the iPhone's media area, where PublicStaging lives.
/// Packet = 40-byte little-endian header (magic "CFA6LPAA", entire length, header+arguments length, packet number,
/// operation), then the arguments, then the payload.
/// </summary>
public sealed class AfcClient : IAsyncDisposable, IDisposable
{
    public const string ServiceName = "com.apple.afc";
    internal const int HeaderSize = 40;
    internal static readonly byte[] Magic = "CFA6LPAA"u8.ToArray();

    internal enum Op : ulong
    {
        Status = 0x01,
        Data = 0x02,
        RemovePath = 0x08,
        MakeDir = 0x09,
        GetFileInfo = 0x0A,
        FileOpen = 0x0D,
        FileOpenResult = 0x0E,
        FileWrite = 0x10,
        FileClose = 0x14,
    }

    /// <summary>AFC status codes RigControl cares about.</summary>
    public const ulong ObjectNotFound = 8, ObjectExists = 16, NoSpaceLeft = 18;

    private const ulong WriteOnly = 3; // "w": create or truncate

    private readonly Stream _stream;
    private ulong _packet;

    public AfcClient(Stream stream, TimeSpan? timeout = null)
    {
        _stream = stream;
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan Timeout { get; }

    /// <summary>Bytes sent in one write packet.</summary>
    public int ChunkSize { get; init; } = 64 * 1024;

    public async Task MakeDirectoryAsync(string path, CancellationToken ct = default) =>
        await RequestAsync(Op.MakeDir, PathArg(path), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);

    /// <summary>Removes a file or an empty folder. False when it wasn't there.</summary>
    public async Task<bool> RemovePathAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await RequestAsync(Op.RemovePath, PathArg(path), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return true;
        }
        catch (DeviceException e) when (AfcError.Of(e) == ObjectNotFound)
        {
            return false;
        }
    }

    /// <summary>The file's attributes (st_size, st_ifmt ...), or null when it doesn't exist.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetFileInfoAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var (_, data) = await RequestAsync(Op.GetFileInfo, PathArg(path), ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            var parts = Encoding.UTF8.GetString(data).Split('\0');
            var info = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < parts.Length; i += 2)
                info[parts[i]] = parts[i + 1];
            return info;
        }
        catch (DeviceException e) when (AfcError.Of(e) == ObjectNotFound)
        {
            return null;
        }
    }

    /// <summary>Copies a local file to <paramref name="remotePath"/>, reporting bytes sent so far.</summary>
    public async Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var handle = await OpenForWriteAsync(remotePath, ct).ConfigureAwait(false);
        var ok = false;
        try
        {
            var buffer = new byte[ChunkSize];
            long sent = 0;
            var handleArg = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(handleArg, handle);
            while (true)
            {
                var n = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                    break;
                await RequestAsync(Op.FileWrite, handleArg, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                sent += n;
                progress?.Report(sent);
            }
            ok = true;
        }
        finally
        {
            if (ok)
            {
                await CloseAsync(handle, ct).ConfigureAwait(false);
            }
            else
            {
                // The upload already failed or was cancelled; that error is the one worth showing.
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await CloseAsync(handle, cts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is DeviceException or OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task<ulong> OpenForWriteAsync(string path, CancellationToken ct)
    {
        var pathArg = PathArg(path);
        var args = new byte[8 + pathArg.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(args, WriteOnly);
        pathArg.CopyTo(args, 8);
        var (op, data) = await RequestAsync(Op.FileOpen, args, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        if (op != Op.FileOpenResult || data.Length < 8)
            throw new DeviceException(DeviceErrors.BadReply);
        return BinaryPrimitives.ReadUInt64LittleEndian(data);
    }

    private async Task CloseAsync(ulong handle, CancellationToken ct)
    {
        var args = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(args, handle);
        await RequestAsync(Op.FileClose, args, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    private static byte[] PathArg(string path)
    {
        if (path.Contains('\0'))
            throw new ArgumentException("A path can't contain a NUL character.", nameof(path));
        var bytes = Encoding.UTF8.GetBytes(path);
        var arg = new byte[bytes.Length + 1];
        bytes.CopyTo(arg, 0);
        return arg;
    }

    /// <summary>Sends one packet and reads the answer. A non-zero status becomes a DeviceException (inner <see cref="AfcError"/>).</summary>
    private async Task<(Op Op, byte[] Data)> RequestAsync(Op op, ReadOnlyMemory<byte> args, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var packet = BuildPacket(_packet++, op, args.Span, payload.Span);
        await DeviceIo.WriteAsync(_stream, packet, Timeout, ct).ConfigureAwait(false);
        var (replyOp, data) = await ReadPacketAsync(_stream, Timeout, ct).ConfigureAwait(false);
        if (replyOp == Op.Status)
        {
            var code = data.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data) : ulong.MaxValue;
            if (code != 0)
                throw new DeviceException(AfcError.Describe(code), new AfcError(code));
        }
        return (replyOp, data);
    }

    internal static byte[] BuildPacket(ulong number, Op op, ReadOnlySpan<byte> args, ReadOnlySpan<byte> payload)
    {
        var thisLength = HeaderSize + args.Length;
        var packet = new byte[thisLength + payload.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), (ulong)thisLength);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), number);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(32), (ulong)op);
        args.CopyTo(packet.AsSpan(HeaderSize));
        payload.CopyTo(packet.AsSpan(thisLength));
        return packet;
    }

    /// <summary>Reads one packet; returns its operation and its arguments and payload joined together.</summary>
    internal static async Task<(Op Op, byte[] Data)> ReadPacketAsync(Stream stream, TimeSpan timeout, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await DeviceIo.ReadExactAsync(stream, header, timeout, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new DeviceException(DeviceErrors.BadReply);
        var entire = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        var thisLength = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        var op = (Op)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
        if (thisLength < HeaderSize || entire < thisLength || entire - HeaderSize > DeviceIo.MaxPlistBytes)
            throw new DeviceException(DeviceErrors.BadReply);
        var data = new byte[entire - HeaderSize];
        await DeviceIo.ReadExactAsync(stream, data, timeout, ct).ConfigureAwait(false);
        return (op, data);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    public void Dispose() => _stream.Dispose();
}

/// <summary>The inner exception of a <see cref="DeviceException"/> caused by a non-zero AFC status.</summary>
public sealed class AfcError(ulong code) : Exception($"AFC status {code}")
{
    public ulong Code { get; } = code;

    /// <summary>The AFC status behind a DeviceException, if that is what caused it.</summary>
    public static ulong? Of(Exception e) => (e.InnerException as AfcError)?.Code;

    internal static string Describe(ulong code) => code switch
    {
        AfcClient.NoSpaceLeft => DeviceErrors.OutOfSpace,
        AfcClient.ObjectNotFound => "The file isn't on the iPhone.",
        10 => "The iPhone didn't allow access to that file.",
        _ => $"The iPhone couldn't complete a file transfer step (AFC error {code}).",
    };
}

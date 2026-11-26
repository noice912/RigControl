using System.Buffers.Binary;
using RigControl.iPhone.Plist;

namespace RigControl.iPhone.Device;

/// <summary>Reads and writes on a device connection with a time limit on every call.</summary>
internal static class DeviceIo
{
    /// <summary>The largest property list accepted from usbmuxd or the iPhone (a pair record is a few KB).</summary>
    public const int MaxPlistBytes = 16 * 1024 * 1024;

    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer[read..], cts.Token).ConfigureAwait(false);
                if (n == 0)
                    throw new DeviceException(DeviceErrors.ConnectionLost);
                read += n;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceException(DeviceErrors.NoAnswer);
        }
        catch (IOException e)
        {
            throw new DeviceException(DeviceErrors.ConnectionLost, e);
        }
        catch (ObjectDisposedException e)
        {
            throw new DeviceException(DeviceErrors.ConnectionLost, e);
        }
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(data, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceException(DeviceErrors.NoAnswer);
        }
        catch (IOException e)
        {
            throw new DeviceException(DeviceErrors.ConnectionLost, e);
        }
        catch (ObjectDisposedException e)
        {
            throw new DeviceException(DeviceErrors.ConnectionLost, e);
        }
    }

    public static PlistDict ParseReply(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return PlistFile.ParseDict(bytes);
        }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException or ArgumentException or OverflowException)
        {
            throw new DeviceException(DeviceErrors.BadReply, e);
        }
    }
}

/// <summary>
/// The framing lockdown and most device services use: a 4-byte big-endian length, then an XML (or binary) plist.
/// </summary>
public static class PlistMessages
{
    public static async Task WriteAsync(Stream stream, PlistDict message, TimeSpan timeout, CancellationToken ct)
    {
        var body = PlistFile.ToXml(message);
        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)body.Length);
        body.CopyTo(buffer, 4);
        await DeviceIo.WriteAsync(stream, buffer, timeout, ct).ConfigureAwait(false);
    }

    public static async Task<PlistDict> ReadAsync(Stream stream, TimeSpan timeout, CancellationToken ct)
    {
        var header = new byte[4];
        await DeviceIo.ReadExactAsync(stream, header, timeout, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > DeviceIo.MaxPlistBytes)
            throw new DeviceException(DeviceErrors.BadReply);
        var body = new byte[length];
        await DeviceIo.ReadExactAsync(stream, body, timeout, ct).ConfigureAwait(false);
        return DeviceIo.ParseReply(body);
    }
}

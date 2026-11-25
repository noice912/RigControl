using System.Buffers.Binary;
using System.Net.Sockets;

namespace RigControl.Lighting;

/// <summary>What the lighting module needs from OpenRGB. Abstracted so tests never start a process or open a socket.</summary>
public interface IOpenRgb : IDisposable
{
    /// <summary>Full path of an installed OpenRGB.exe, or null.</summary>
    string? FindExecutable();

    /// <summary>True if something already answers on the SDK port (a server the user or a service started).</summary>
    bool IsListening();

    /// <summary>Starts OpenRGB hidden as an SDK server and waits for its port. Returns false if it did not come up.</summary>
    bool StartServer(string executable);

    /// <summary>
    /// False when this object never looks for or starts OpenRGB (Linux and macOS): the user starts it with its SDK
    /// server on, and RigControl only connects.
    /// </summary>
    bool CanStartServer => true;

    /// <summary>True if this object started the server (and will stop it on dispose).</summary>
    bool OwnsServer { get; }

    bool Connected { get; }
    void Connect();
    void Disconnect();
    IReadOnlyList<RgbController> ReadControllers();
    void UpdateMode(int device, int modeIndex, RgbMode mode);
    void UpdateLeds(int device, IReadOnlyList<uint> colors);
    void UpdateZoneLeds(int device, int zone, IReadOnlyList<uint> colors);

    /// <summary>
    /// Tells OpenRGB how many lights hang on a header. Only on the user's own request. OpenRGB saves the number
    /// itself; the device must be read again afterwards (its lights are numbered afresh).
    /// </summary>
    void ResizeZone(int device, int zone, int leds);

    /// <summary>True (once) if the server announced that its device list changed since the last call.</summary>
    bool DeviceListChanged();
}

/// <summary>
/// The connection to an OpenRGB SDK server on this computer (127.0.0.1, protocol 3..5). It only ever talks to a
/// server that is already running: it never looks for, installs or starts OpenRGB. On Linux and macOS this is all
/// there is (the user starts OpenRGB with its SDK server on); on Windows <c>OpenRgbServer</c> adds finding and
/// starting it. All calls are serialised by the owning module.
/// </summary>
public class OpenRgbClient : IOpenRgb
{
    public const int Port = 6742;
    protected const string Host = "127.0.0.1";

    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _version;
    private bool _listChanged;

    public OpenRgbClient() : this(Port) { }

    /// <summary>For tests: a stand-in server on another port of this computer, so a real OpenRGB is never touched.</summary>
    internal OpenRgbClient(int port) => _port = port;

    /// <summary>Always null here: this class does not look for an installed OpenRGB.</summary>
    public virtual string? FindExecutable() => null;

    /// <summary>Always false here: this class does not start OpenRGB.</summary>
    public virtual bool StartServer(string executable) => false;

    public virtual bool OwnsServer => false;

    public virtual bool CanStartServer => false;

    public bool Connected => _tcp is { Connected: true } && _stream is not null;

    public bool IsListening()
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(Host, _port).Wait(400) && probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Connect()
    {
        Disconnect();
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
        if (!tcp.ConnectAsync(Host, _port).Wait(1500))
        {
            tcp.Dispose();
            throw new IOException("OpenRGB is not answering.");
        }
        _tcp = tcp;
        _stream = tcp.GetStream();

        Send(OpenRgbProtocol.ProtocolVersionRequest(OpenRgbProtocol.MaxVersion));
        var reply = WaitFor(OpenRgbProtocol.RequestProtocolVersion, null);
        if (reply.Payload.Length < 4) throw new IOException("OpenRGB sent an unexpected answer.");
        var serverMax = (int)BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload);
        _version = Math.Min(OpenRgbProtocol.MaxVersion, serverMax);
        if (_version < OpenRgbProtocol.MinVersion)
        {
            Disconnect();
            throw new IOException("This OpenRGB is too old. Install OpenRGB 1.0 or newer.");
        }
        Send(OpenRgbProtocol.ClientName("RigControl"));
    }

    public void Disconnect()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
    }

    private NetworkStream Stream => _stream ?? throw new IOException("Not connected to OpenRGB.");

    private void Send(byte[] packet) => Stream.Write(packet, 0, packet.Length);

    private void ReadExactly(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = Stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new IOException("OpenRGB closed the connection.");
            read += n;
        }
    }

    private OpenRgbPacket ReadPacket()
    {
        var header = new byte[OpenRgbProtocol.HeaderSize];
        ReadExactly(header);
        if (!OpenRgbProtocol.TryParseHeader(header, out var device, out var id, out var size))
            throw new IOException("OpenRGB sent data that is not its protocol.");
        var payload = new byte[size];
        if (size > 0) ReadExactly(payload);
        if (id == OpenRgbProtocol.DeviceListUpdated) _listChanged = true;
        return new OpenRgbPacket(device, id, payload);
    }

    /// <summary>Reads until the reply with this id (and device, when given) arrives; anything else is skipped.</summary>
    private OpenRgbPacket WaitFor(uint id, uint? device)
    {
        for (var i = 0; i < 64; i++)
        {
            var p = ReadPacket();
            if (p.Id == id && (device is null || p.DeviceId == device)) return p;
        }
        throw new IOException("OpenRGB did not answer the request.");
    }

    public IReadOnlyList<RgbController> ReadControllers()
    {
        Send(OpenRgbProtocol.ControllerCountRequest());
        var countReply = WaitFor(OpenRgbProtocol.RequestControllerCount, null);
        var count = countReply.Payload.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(countReply.Payload) : 0;
        if (count is < 0 or > 256) throw new IOException("OpenRGB reported an implausible number of devices.");
        var list = new List<RgbController>(count);
        for (var i = 0; i < count; i++)
        {
            Send(OpenRgbProtocol.ControllerDataRequest(i, _version));
            var reply = WaitFor(OpenRgbProtocol.RequestControllerData, (uint)i);
            list.Add(OpenRgbProtocol.ParseController(reply.Payload, _version, i));
        }
        return list;
    }

    public void UpdateMode(int device, int modeIndex, RgbMode mode) => Send(OpenRgbProtocol.UpdateModePacket(device, modeIndex, mode, _version));

    public void UpdateLeds(int device, IReadOnlyList<uint> colors) => Send(OpenRgbProtocol.UpdateLedsPacket(device, colors));

    public void UpdateZoneLeds(int device, int zone, IReadOnlyList<uint> colors) => Send(OpenRgbProtocol.UpdateZoneLedsPacket(device, zone, colors));

    public void ResizeZone(int device, int zone, int leds) => Send(OpenRgbProtocol.ResizeZonePacket(device, zone, leds));

    public bool DeviceListChanged()
    {
        try
        {
            while (Connected && Stream.DataAvailable) ReadPacket();
        }
        catch
        {
            Disconnect();
        }
        var changed = _listChanged;
        _listChanged = false;
        return changed;
    }

    public virtual void Dispose() => Disconnect();
}

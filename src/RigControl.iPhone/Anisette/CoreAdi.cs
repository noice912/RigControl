using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RigControl.iPhone.Anisette;

/// <summary>The few CoreADI operations anisette needs. <see cref="CoreAdi"/> is the real one; tests use a fake.</summary>
public interface ICoreAdi
{
    bool IsMachineProvisioned(long dsId);
    (byte[] Mid, byte[] Otp) RequestOtp(long dsId);
    ulong GetRouting(long dsId);
    void SetRouting(long dsId, ulong routing);
    (byte[] Cpim, uint Session) StartProvisioning(long dsId, byte[] spim);
    void EndProvisioning(uint session, byte[] ptm, byte[] tk);
    void DestroyProvisioningSession(uint session);
    (byte[] Mid, byte[] Srm) Synchronize(long dsId, byte[] sim);
    void EraseProvisioning(long dsId);
}

/// <summary>
/// Apple's own device-identification library (CoreADI64.dll, from an installed Apple app), called through its
/// dispatcher export <c>vdfut768ig(code, parameters)</c>. Each call is a message: big-endian u32 1 (clear),
/// u32 1 (marker), then the call's fields big-endian, pointers as 8-byte addresses. The library writes its result
/// (an int, negative on failure) over the start of the message and fills the out-pointers.
/// Function codes and layouts as documented by Dadoum/apple-crates (adi/src/calls.rs); this is our own code.
/// The library is not thread-safe: every call holds one lock.
/// </summary>
public sealed unsafe class CoreAdi : ICoreAdi, IDisposable
{
    private const uint Initialize = 0x12db31c5;
    private const uint IsMachineProvisionedCode = 0xb0eda7af;
    private const uint ProvisioningStart = 0x716bd86c;
    private const uint ProvisioningEnd = 0x0b2b3196;
    private const uint ProvisioningSessionDestroy = 0x55a637c0;
    private const uint SetIdmsRouting = 0x632b8d6e;
    private const uint GetIdmsRouting = 0x85fe63b0;
    private const uint SynchronizeCode = 0xa54951ec;
    private const uint RequestOtpCode = 0xcfe0b46a;
    private const uint ProvisioningErase = 0x7715488c;
    private const uint StorageDispose = 0x3e58e7f9;
    private const uint TearDown = 0x08c0fe9e;

    /// <summary>CoreADI's "not provisioned" answer.</summary>
    public const int NotProvisioned = -45061;

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public byte* Payload;
        public uint InputSize;
        public uint OutputSize;
        public uint Flags;
    }

    private readonly nint _module;
    private readonly delegate* unmanaged<uint, Parameters*, int> _dispatch;
    private readonly object _gate = new();
    private bool _disposed;

    private CoreAdi(nint module, nint dispatch)
    {
        _module = module;
        _dispatch = (delegate* unmanaged<uint, Parameters*, int>)dispatch;
    }

    /// <summary>Loads the library at <paramref name="path"/> (already checked by the caller) and initialises it.</summary>
    public static CoreAdi Load(string path)
    {
        var module = NativeLibrary.Load(path);
        try
        {
            if (!NativeLibrary.TryGetExport(module, "vdfut768ig", out var dispatch))
                throw new AnisetteException("This copy of Apple's CoreADI has no entry point RigControl knows.");
            var adi = new CoreAdi(module, dispatch);
            adi.Check("start", adi.Call(Initialize, []));
            return adi;
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    public bool IsMachineProvisioned(long dsId)
    {
        var r = Call(IsMachineProvisionedCode, new FieldWriter().I64(dsId).ToArray());
        if (r == 0) return true;
        if (r == NotProvisioned) return false;
        throw Error("check", r);
    }

    public (byte[] Mid, byte[] Otp) RequestOtp(long dsId)
    {
        nint midP = 0, otpP = 0;
        uint midL = 0, otpL = 0;
        Check("one-time code", Call(RequestOtpCode, new FieldWriter().I64(dsId).Ptr(&midP).Ptr(&midL).Ptr(&otpP).Ptr(&otpL).ToArray()));
        try
        {
            return (Copy(midP, midL), Copy(otpP, otpL));
        }
        finally
        {
            Dispose(midP);
            Dispose(otpP);
        }
    }

    public ulong GetRouting(long dsId)
    {
        ulong routing = 0;
        Check("routing", Call(GetIdmsRouting, new FieldWriter().I64(dsId).Ptr(&routing).ToArray()));
        return routing;
    }

    public void SetRouting(long dsId, ulong routing) =>
        Check("routing", Call(SetIdmsRouting, new FieldWriter().I64(dsId).U64(routing).ToArray()));

    public (byte[] Cpim, uint Session) StartProvisioning(long dsId, byte[] spim)
    {
        nint cpimP = 0;
        uint cpimL = 0, session = 0;
        fixed (byte* s = spim)
        {
            var sp = s;
            Check("provisioning", Call(ProvisioningStart,
                new FieldWriter().I64(dsId).Ptr(sp).U32((uint)spim.Length).Ptr(&cpimP).Ptr(&cpimL).Ptr(&session).ToArray()));
        }
        // The cpim buffer belongs to the provisioning session; it is not freed separately.
        return (Copy(cpimP, cpimL), session);
    }

    public void EndProvisioning(uint session, byte[] ptm, byte[] tk)
    {
        fixed (byte* p = ptm)
        fixed (byte* t = tk)
        {
            var pp = p;
            var tp = t;
            Check("provisioning", Call(ProvisioningEnd,
                new FieldWriter().U32(session).Ptr(pp).U32((uint)ptm.Length).Ptr(tp).U32((uint)tk.Length).ToArray()));
        }
    }

    public void DestroyProvisioningSession(uint session) =>
        Check("provisioning", Call(ProvisioningSessionDestroy, new FieldWriter().U32(session).ToArray()));

    public (byte[] Mid, byte[] Srm) Synchronize(long dsId, byte[] sim)
    {
        nint midP = 0, srmP = 0;
        uint midL = 0, srmL = 0;
        fixed (byte* s = sim)
        {
            var sp = s;
            Check("sync", Call(SynchronizeCode,
                new FieldWriter().I64(dsId).Ptr(sp).U32((uint)sim.Length).Ptr(&midP).Ptr(&midL).Ptr(&srmP).Ptr(&srmL).ToArray()));
        }
        try
        {
            return (Copy(midP, midL), Copy(srmP, srmL));
        }
        finally
        {
            Dispose(midP);
            Dispose(srmP);
        }
    }

    public void EraseProvisioning(long dsId) =>
        Check("reset", Call(ProvisioningErase, new FieldWriter().I64(dsId).ToArray()));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { CallUnlocked(TearDown, []); } catch (Exception) { /* closing anyway */ }
        NativeLibrary.Free(_module);
    }

    private void Dispose(nint ptr)
    {
        if (ptr != 0)
            Call(StorageDispose, new FieldWriter().U64((ulong)ptr).ToArray());
    }

    private static byte[] Copy(nint ptr, uint len)
    {
        if (ptr == 0 || len == 0)
            return [];
        if (len > 1 << 20)
            throw new AnisetteException("Apple's CoreADI returned an unexpected amount of data.");
        return new ReadOnlySpan<byte>((void*)ptr, (int)len).ToArray();
    }

    private int Call(uint code, byte[] fields)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CallUnlocked(code, fields);
        }
    }

    private int CallUnlocked(uint code, byte[] fields)
    {
        var payload = new byte[8 + Math.Max(fields.Length, 4)];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 1);
        fields.CopyTo(payload, 8);
        fixed (byte* p = payload)
        {
            var parameters = new Parameters { Payload = p, InputSize = (uint)(8 + fields.Length), OutputSize = 4 };
            var status = _dispatch(code, &parameters);
            if (status < 0)
                return status;
        }
        return BinaryPrimitives.ReadInt32BigEndian(payload);
    }

    private void Check(string what, int result)
    {
        if (result < 0)
            throw Error(what, result);
    }

    private static AnisetteException Error(string what, int code) =>
        new($"Apple's device identification failed ({what}, error {code}).", code);

    private sealed class FieldWriter
    {
        private readonly List<byte> _bytes = [];

        public FieldWriter I64(long v) => U64(unchecked((ulong)v));

        public FieldWriter U64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, v);
            _bytes.AddRange(b.ToArray());
            return this;
        }

        public FieldWriter U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            _bytes.AddRange(b.ToArray());
            return this;
        }

        public FieldWriter Ptr(void* p) => U64((ulong)p);

        public byte[] ToArray() => _bytes.ToArray();
    }
}

public sealed class AnisetteException(string message, int code = 0, Exception? inner = null) : Exception(message, inner)
{
    public int Code { get; } = code;
}

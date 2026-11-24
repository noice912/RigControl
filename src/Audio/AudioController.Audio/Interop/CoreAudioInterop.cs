using System.Runtime.InteropServices;

namespace AudioController.Audio.Interop;

/// <summary>Native PROPERTYKEY (fmtid + pid).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKeyNative
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKeyNative(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    public PropertyKeyNative(string formatId, uint propertyId)
        : this(new Guid(formatId), propertyId)
    {
    }

    public override readonly string ToString() => $"{FormatId:B},{PropertyId}";
}

/// <summary>
/// Native PROPVARIANT. Only the members this assembly reads or writes are mapped; the explicit size
/// covers the 64-bit layout (24 bytes) and is a harmless over-allocation on 32-bit (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantNative
{
    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public byte ByteValue;
    [FieldOffset(8)] public short Int16Value;
    [FieldOffset(8)] public ushort UInt16Value;
    [FieldOffset(8)] public int Int32Value;
    [FieldOffset(8)] public uint UInt32Value;
    [FieldOffset(8)] public long Int64Value;
    [FieldOffset(8)] public IntPtr Pointer;

    public static PropVariantNative FromUInt32(uint value) =>
        new() { VarType = VarTypes.UI4, UInt32Value = value };
}

/// <summary>VARTYPE constants used by the audio endpoint property stores.</summary>
internal static class VarTypes
{
    public const ushort Empty = 0;
    public const ushort I2 = 2;
    public const ushort I4 = 3;
    public const ushort Bool = 11;
    public const ushort UI1 = 17;
    public const ushort UI2 = 18;
    public const ushort UI4 = 19;
    public const ushort I8 = 20;
    public const ushort UI8 = 21;
    public const ushort Int = 22;
    public const ushort UInt = 23;
    public const ushort LpWStr = 31;
    public const ushort Clsid = 72;
}

/// <summary>A managed copy of a PROPVARIANT value.</summary>
internal readonly record struct PropValue(ushort VarType, ulong? Integer, string? Text)
{
    public static PropValue Empty { get; } = new(VarTypes.Empty, null, null);

    public bool IsEmpty => VarType == VarTypes.Empty;

    /// <summary>Copies the value out of <paramref name="variant"/> and frees any native memory it owns.</summary>
    public static PropValue FromNativeAndClear(ref PropVariantNative variant)
    {
        try
        {
            ulong? integer = null;
            string? text = null;
            switch (variant.VarType)
            {
                case VarTypes.UI1: integer = variant.ByteValue; break;
                case VarTypes.I2: integer = unchecked((ulong)variant.Int16Value); break;
                case VarTypes.UI2: integer = variant.UInt16Value; break;
                case VarTypes.I4:
                case VarTypes.Int: integer = unchecked((ulong)variant.Int32Value); break;
                case VarTypes.UI4:
                case VarTypes.UInt: integer = variant.UInt32Value; break;
                case VarTypes.I8:
                case VarTypes.UI8: integer = unchecked((ulong)variant.Int64Value); break;
                case VarTypes.Bool: integer = variant.Int16Value != 0 ? 1UL : 0UL; break;
                case VarTypes.LpWStr:
                    text = variant.Pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(variant.Pointer);
                    break;
                case VarTypes.Clsid:
                    text = variant.Pointer == IntPtr.Zero ? null : Marshal.PtrToStructure<Guid>(variant.Pointer).ToString("B");
                    break;
            }

            return new PropValue(variant.VarType, integer, text);
        }
        finally
        {
            _ = NativeMethods.PropVariantClear(ref variant);
        }
    }
}

internal static class NativeMethods
{
    public const int ClsCtxAll = 0x17;
    public const int CoENotInitialized = unchecked((int)0x800401F0);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariantNative variant);

    /// <summary>Same function for NAudio's PROPVARIANT mapping (its marshaled size matches the native struct).</summary>
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref NAudio.CoreAudioApi.Interfaces.PropVariant variant);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr instance);
}

internal static class ComHelpers
{
    /// <summary>
    /// Creates a COM object behind a private RCW (<see cref="Marshal.GetUniqueObjectForIUnknown"/>), so this
    /// code never shares, retypes or prematurely releases an RCW that other components hold for the same
    /// object. Returns null when the class is unavailable.
    /// </summary>
    public static object? CreateUnique(Guid clsid, Guid iid)
    {
        IntPtr unknown = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, NativeMethods.ClsCtxAll, ref iid, out unknown);
            if (hr == NativeMethods.CoENotInitialized)
            {
                // COM was never initialized on this thread; let the runtime create (and initialize) it.
                var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
                return type is null ? null : Activator.CreateInstance(type);
            }

            return hr < 0 || unknown == IntPtr.Zero ? null : Marshal.GetUniqueObjectForIUnknown(unknown);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
                                       or System.Reflection.TargetInvocationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary>Releases an RCW this code created; never throws.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // The object is already detached from its native counterpart; nothing left to release.
        }
    }
}

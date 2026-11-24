using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioController.Audio.Interop;

/// <summary>
/// One endpoint's property store (IMMDevice::OpenPropertyStore), opened through NAudio's public API.
/// Values are copied out and their native memory freed, so repeated reads do not leak.
/// </summary>
internal sealed class EndpointPropertyStore
{
    private readonly PropertyStore _store;
    private Dictionary<(Guid, int), int>? _index;

    private EndpointPropertyStore(PropertyStore store, bool isWritable)
    {
        _store = store;
        IsWritable = isWritable;
    }

    /// <summary>True when the store was opened with STGM_READWRITE.</summary>
    public bool IsWritable { get; }

    /// <summary>
    /// Opens the store of <paramref name="device"/>; null when it cannot be opened (read-write access
    /// normally requires administrator rights).
    /// </summary>
    public static EndpointPropertyStore? TryOpen(MMDevice device, bool readWrite)
    {
        try
        {
            device.GetPropertyInformation(readWrite ? StorageAccessMode.ReadWrite : StorageAccessMode.Read);
            var store = device.Properties;
            return store is null ? null : new EndpointPropertyStore(store, readWrite);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Reads a value; returns <see cref="PropValue.Empty"/> when absent or unreadable.</summary>
    public PropValue Get(PropertyKeyNative key)
    {
        try
        {
            var index = _index ??= BuildIndex();
            if (!index.TryGetValue((key.FormatId, (int)key.PropertyId), out int position))
            {
                return PropValue.Empty;
            }

            return Copy(_store.GetValue(position));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return PropValue.Empty;
        }
    }

    /// <summary>Reads a string value; null when absent, empty or not a string.</summary>
    public string? GetString(PropertyKeyNative key)
    {
        string? text = Get(key).Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Writes a VT_UI4 value and commits it. Returns false on any failure.</summary>
    public bool TrySetUInt32(PropertyKeyNative key, uint value)
    {
        if (!IsWritable)
        {
            return false;
        }

        try
        {
            var variant = new PropVariant { vt = (short)VarTypes.UI4, ulVal = value };
            _store.SetValue(new PropertyKey(key.FormatId, (int)key.PropertyId), variant);
            _store.Commit();
            _index = null;
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        {
            return false;
        }
    }

    private Dictionary<(Guid, int), int> BuildIndex()
    {
        int count = _store.Count;
        var index = new Dictionary<(Guid, int), int>(count);
        for (int i = 0; i < count; i++)
        {
            var k = _store.Get(i);
            index.TryAdd((k.formatId, k.propertyId), i);
        }

        return index;
    }

    /// <summary>
    /// Copies the value, then frees whatever native memory the variant owns (strings, CLSIDs, blobs,
    /// vectors, interface pointers) with PropVariantClear, whatever its type.
    /// </summary>
    private static PropValue Copy(PropVariant variant)
    {
        try
        {
            return Extract(variant);
        }
        finally
        {
            _ = NativeMethods.PropVariantClear(ref variant);
        }
    }

    private static PropValue Extract(PropVariant variant)
    {
        ushort vt = unchecked((ushort)variant.vt);
        switch (vt)
        {
            case VarTypes.LpWStr:
            {
                IntPtr p = variant.pointerValue;
                string? text = p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
                return new PropValue(vt, null, text);
            }

            case VarTypes.Clsid:
            {
                IntPtr p = variant.pointerValue;
                string? text = p == IntPtr.Zero ? null : Marshal.PtrToStructure<Guid>(p).ToString("B");
                return new PropValue(vt, null, text);
            }

            case VarTypes.UI1: return new PropValue(vt, variant.bVal, null);
            case VarTypes.I2: return new PropValue(vt, unchecked((ulong)variant.iVal), null);
            case VarTypes.UI2: return new PropValue(vt, variant.uiVal, null);
            case VarTypes.I4:
            case VarTypes.Int: return new PropValue(vt, unchecked((ulong)variant.lVal), null);
            case VarTypes.UI4:
            case VarTypes.UInt: return new PropValue(vt, variant.ulVal, null);
            case VarTypes.I8:
            case VarTypes.UI8: return new PropValue(vt, unchecked((ulong)variant.hVal), null);
            case VarTypes.Bool: return new PropValue(vt, variant.boolVal != 0 ? 1UL : 0UL, null);
            default: return new PropValue(vt, null, null);
        }
    }
}

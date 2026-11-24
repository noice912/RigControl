namespace AudioController.Core.Devices;

/// <summary>Pure logic for identifying Bluetooth audio endpoints from Windows device identifiers.</summary>
public static class BluetoothDetector
{
    /// <param name="enumeratorOrInstanceId">A device enumerator name (e.g. "BTHENUM") or a full
    /// device instance id (e.g. "BTHENUM\{0000110b-...}").</param>
    public static bool IsBluetooth(string? enumeratorOrInstanceId)
    {
        if (string.IsNullOrWhiteSpace(enumeratorOrInstanceId)) return false;

        var s = enumeratorOrInstanceId;
        return s.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
            || s.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
    }
}

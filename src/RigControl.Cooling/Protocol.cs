namespace RigControl.Cooling;

/// <summary>
/// Packets for the NZXT Kraken Elite (USB 1E71:300C). Source of truth: docs/protocols/nzxt-kraken-elite.md.
/// Every packet is a 64-byte HID output report whose first byte is the report id / command.
/// This class can only build the commands on the allow-list; nothing else can be produced.
/// </summary>
public static class KrakenProtocol
{
    public const int VendorId = 0x1E71;
    public const int ProductId = 0x300C;
    public const int ReportLength = 64;

    /// <summary>The device's curve covers liquid temperature 20..59 °C in 1 °C steps (40 points).</summary>
    public const int CurveMinTemp = 20;
    public const int CurveMaxTemp = 59;
    public const int CurvePointCount = 40;

    /// <summary>Lowest pump duty any reference implementation sends. Never go below it.</summary>
    public const int PumpMinDuty = 20;

    public enum Channel { Pump, Fan }

    private static byte[] Packet(params byte[] head)
    {
        var p = new byte[ReportLength];
        head.CopyTo(p, 0);
        return p;
    }

    public static byte[] GetFirmware() => Packet(0x10, 0x01);
    public static byte[] GetStatus() => Packet(0x74, 0x01);
    public static byte[] GetLcdInfo() => Packet(0x30, 0x01);

    /// <summary>Sets LCD brightness (0..100) and orientation (0, 90, 180 or 270) together; the device has no separate commands.</summary>
    public static byte[] SetLcd(int brightnessPercent, int orientationDegrees)
    {
        if (brightnessPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(brightnessPercent));
        if (orientationDegrees is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(orientationDegrees));
        return Packet(0x30, 0x02, 0x01, (byte)brightnessPercent, 0x00, 0x00, 0x01, (byte)(orientationDegrees / 90));
    }

    /// <summary>
    /// Builds the 40 duty bytes for liquid temperatures 20..59 °C from user points, exactly as the reference
    /// (liquidctl) does: a failsafe point (59 °C, 100 %) is always added, the curve is made non-decreasing,
    /// values are interpolated (round half to even) and finally clamped to [minDuty, 100].
    /// The last byte (59 °C) is therefore always 100: whatever is sent stays safe if the app never runs again.
    /// </summary>
    public static byte[] BuildDuties(IEnumerable<(int TempC, int Duty)> points, int minDuty)
    {
        if (minDuty is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(minDuty));
        var sorted = points.Append((TempC: CurveMaxTemp, Duty: 100))
            .OrderBy(p => p.TempC).ThenByDescending(p => p.Duty).ToList();

        var kept = new List<(int TempC, int Duty)>();
        foreach (var p in sorted)
        {
            if (kept.Count > 0 && p.TempC == kept[^1].TempC) continue;
            var duty = kept.Count > 0 ? Math.Max(p.Duty, kept[^1].Duty) : p.Duty; // running maximum
            kept.Add((p.TempC, duty));
            if (duty >= 100) break;
        }

        var result = new byte[CurvePointCount];
        for (var t = CurveMinTemp; t <= CurveMaxTemp; t++)
        {
            var lower = kept[0];
            foreach (var k in kept)
            {
                if (k.TempC <= t) lower = k;
                else break;
            }
            var upper = kept[^1];
            foreach (var k in kept)
            {
                if (k.TempC >= t)
                {
                    upper = k;
                    break;
                }
            }
            double value = lower.TempC == upper.TempC
                ? lower.Duty
                : lower.Duty + (t - lower.TempC) / (double)(upper.TempC - lower.TempC) * (upper.Duty - lower.Duty);
            var rounded = (int)Math.Round(value, MidpointRounding.ToEven);
            result[t - CurveMinTemp] = (byte)Math.Clamp(rounded, minDuty, 100);
        }
        return result;
    }

    /// <summary>A fixed duty is a flat curve (still ending in 100 % at 59 °C).</summary>
    public static byte[] BuildFixedDuties(int duty, int minDuty) => BuildDuties(new[] { (0, duty), (58, duty) }, minDuty);

    /// <summary>SET_SPEED_PROFILE for one channel from 40 duty bytes produced by <see cref="BuildDuties"/>.</summary>
    public static byte[] SetSpeedProfile(Channel channel, byte[] duties)
    {
        if (duties.Length != CurvePointCount) throw new ArgumentException("Exactly 40 duty values are required.", nameof(duties));
        var min = channel == Channel.Pump ? PumpMinDuty : 0;
        for (var i = 0; i < duties.Length; i++)
        {
            if (duties[i] > 100 || duties[i] < min) throw new ArgumentException($"Duty {duties[i]} at point {i} is outside {min}..100.", nameof(duties));
            if (i > 0 && duties[i] < duties[i - 1]) throw new ArgumentException("Duties must not decrease.", nameof(duties));
        }
        if (duties[^1] != 100) throw new ArgumentException("The last point (59 °C) must be 100 %.", nameof(duties));

        var p = channel == Channel.Pump ? Packet(0x72, 0x01, 0x01, 0x00) : Packet(0x72, 0x02, 0x01, 0x01);
        duties.CopyTo(p, 4);
        return p;
    }

    public sealed record Status(double? LiquidTempC, int PumpRpm, int PumpDuty, int FanRpm, int FanDuty, bool SensorFault);

    /// <summary>Parses a status report (75 01 requested, 75 02 pushed). Returns null if it is not one.</summary>
    public static Status? ParseStatus(ReadOnlySpan<byte> r)
    {
        if (r.Length < 26 || r[0] != 0x75 || (r[1] != 0x01 && r[1] != 0x02)) return null;
        var fault = r[15] == 0xFF && r[16] == 0xFF;
        double? temp = fault ? null : r[15] + r[16] / 10.0;
        var pumpDuty = r[19];
        var fanDuty = r[25];
        if (pumpDuty > 100 || fanDuty > 100) return null;
        if (temp is > 100) return null;
        return new Status(temp, r[17] | (r[18] << 8), pumpDuty, r[23] | (r[24] << 8), fanDuty, fault);
    }

    /// <summary>Parses the firmware reply (11 01): "major.minor.patch".</summary>
    public static string? ParseFirmware(ReadOnlySpan<byte> r)
        => r.Length >= 20 && r[0] == 0x11 && r[1] == 0x01 ? $"{r[17]}.{r[18]}.{r[19]}" : null;

    public sealed record LcdInfo(int BrightnessPercent, int OrientationDegrees);

    /// <summary>Parses the LCD info reply (31 01).</summary>
    public static LcdInfo? ParseLcdInfo(ReadOnlySpan<byte> r)
    {
        if (r.Length < 27 || r[0] != 0x31 || r[1] != 0x01) return null;
        if (r[24] > 100 || r[26] > 3) return null;
        return new LcdInfo(r[24], r[26] * 90);
    }

    /// <summary>True if the first two bytes are a command RigControl is allowed to send to the Kraken.</summary>
    public static bool IsAllowed(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != ReportLength) return false;
        return (packet[0], packet[1]) switch
        {
            (0x10, 0x01) => true,
            (0x30, 0x01) => true,
            (0x30, 0x02) => true,
            (0x74, 0x01) => true,
            (0x72, 0x01) => packet[2] == 0x01 && packet[3] == 0x00,
            (0x72, 0x02) => packet[2] == 0x01 && packet[3] == 0x01,
            _ => false,
        };
    }
}

/// <summary>
/// Packets for the NZXT RGB &amp; Fan Controller (USB 1E71:2011, 3 fan channels).
/// Source of truth: docs/protocols/nzxt-controllers.md. The lighting-only 1E71:2021 units are never addressed.
/// </summary>
public static class FanControllerProtocol
{
    public const int VendorId = 0x1E71;
    public const int ProductId = 0x2011;
    public const int ReportLength = 64;
    public const int ChannelCount = 3;

    /// <summary>
    /// App policy: this device has no watchdog and keeps the last duty after the app exits, and a duty of 0
    /// stops the fan completely, so RigControl never sends less than this.
    /// </summary>
    public const int MinDuty = 25;

    private static byte[] Packet(params byte[] head)
    {
        var p = new byte[ReportLength];
        head.CopyTo(p, 0);
        return p;
    }

    public static byte[] DetectFans() => Packet(0x60, 0x03);

    /// <summary>Asks the device to push status reports once per second.</summary>
    public static byte[] SetIntervalOneSecond() => Packet(0x60, 0x02, 0x01, 0xE8, 0x03, 0x01, 0xE8, 0x03);

    public static byte[] GetFirmware() => Packet(0x10, 0x01);

    /// <summary>Sets one fan channel (0..2) to a duty. Exactly one mask bit is set, and its duty byte is never 0.</summary>
    public static byte[] SetDuty(int channel, int dutyPercent)
    {
        if (channel is < 0 or >= ChannelCount) throw new ArgumentOutOfRangeException(nameof(channel));
        if (dutyPercent is < MinDuty or > 100) throw new ArgumentOutOfRangeException(nameof(dutyPercent));
        var p = Packet(0x62, 0x01, (byte)(0x01 << channel));
        p[3 + channel] = (byte)dutyPercent;
        return p;
    }

    public sealed record FanStatus(int Type, int Rpm, int Duty)
    {
        /// <summary>0 = no fan detected, 1 = DC (3-pin), 2 = PWM (4-pin).</summary>
        public bool Present => Type != 0;
    }

    /// <summary>Parses the pushed fan status (67 02). Returns null if it is not one or fails validation.</summary>
    public static FanStatus[]? ParseStatus(ReadOnlySpan<byte> r)
    {
        if (r.Length < 57 || r[0] != 0x67 || r[1] != 0x02) return null;
        var fans = new FanStatus[ChannelCount];
        for (var i = 0; i < ChannelCount; i++)
        {
            int type = r[16 + i];
            int duty = r[40 + i];
            if (type > 2 || duty > 100) return null;
            fans[i] = new FanStatus(type, r[24 + 2 * i] | (r[25 + 2 * i] << 8), duty);
        }
        return fans;
    }

    public static string? ParseFirmware(ReadOnlySpan<byte> r)
        => r.Length >= 20 && r[0] == 0x11 && r[1] == 0x01 ? $"{r[17]}.{r[18]}.{r[19]}" : null;

    /// <summary>True if the packet is a command RigControl is allowed to send to the fan controller.</summary>
    public static bool IsAllowed(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != ReportLength) return false;
        switch ((packet[0], packet[1]))
        {
            case (0x10, 0x01):
            case (0x60, 0x03):
                return true;
            case (0x60, 0x02):
                return packet[2] == 0x01 && packet[3] == 0xE8 && packet[4] == 0x03 && packet[5] == 0x01 && packet[6] == 0xE8 && packet[7] == 0x03;
            case (0x62, 0x01):
            {
                var mask = packet[2];
                if (mask is not (0x01 or 0x02 or 0x04)) return false;
                var channel = mask == 0x01 ? 0 : mask == 0x02 ? 1 : 2;
                for (var i = 0; i < 8; i++)
                {
                    var d = packet[3 + i];
                    if (i == channel ? d is < MinDuty or > 100 : d != 0) return false;
                }
                return true;
            }
            default:
                return false;
        }
    }
}

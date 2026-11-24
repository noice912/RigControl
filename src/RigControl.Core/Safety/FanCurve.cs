namespace RigControl.Core.Safety;

/// <summary>One point of a fan / pump curve: at <see cref="TempC"/> degrees Celsius run at <see cref="DutyPercent"/> percent.</summary>
public sealed record CurvePoint(int TempC, int DutyPercent);

/// <summary>
/// A temperature-to-duty curve shared by every cooling feature. Immutable and always valid:
/// construction through <see cref="Create"/> rejects anything that could under-cool the system.
/// </summary>
public sealed class FanCurve
{
    public IReadOnlyList<CurvePoint> Points { get; }

    private FanCurve(IReadOnlyList<CurvePoint> points)
    {
        Points = points;
    }

    /// <summary>
    /// Validates and builds a curve. Rules:
    /// at least 2 points; temperatures strictly increasing and within 0..100 C;
    /// duties within <paramref name="minDutyPercent"/>..100 and never decreasing as temperature rises;
    /// the curve must reach at least <paramref name="requiredDutyAtCriticalPercent"/> at or before
    /// <paramref name="criticalTempC"/> (so a hot system is always cooled hard).
    /// Returns null and sets <paramref name="error"/> when a rule is broken.
    /// </summary>
    public static FanCurve? Create(
        IEnumerable<CurvePoint>? points,
        int minDutyPercent,
        out string? error,
        int criticalTempC = 60,
        int requiredDutyAtCriticalPercent = 100,
        bool enforceCritical = false)
    {
        error = null;
        if (points is null)
        {
            error = "No curve points were given.";
            return null;
        }
        var list = points.ToList();
        if (list.Count < 2)
        {
            error = "A curve needs at least 2 points.";
            return null;
        }
        if (minDutyPercent < 0 || minDutyPercent > 100)
        {
            error = "Minimum duty must be between 0 and 100.";
            return null;
        }
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p is null)
            {
                error = $"Point {i + 1} is missing.";
                return null;
            }
            if (p.TempC < 0 || p.TempC > 100)
            {
                error = $"Point {i + 1}: temperature {p.TempC} C is outside 0-100 C.";
                return null;
            }
            if (p.DutyPercent < minDutyPercent || p.DutyPercent > 100)
            {
                error = $"Point {i + 1}: duty {p.DutyPercent}% is outside the allowed {minDutyPercent}-100%.";
                return null;
            }
            if (i > 0)
            {
                if (p.TempC <= list[i - 1].TempC)
                {
                    error = $"Point {i + 1}: temperatures must strictly increase.";
                    return null;
                }
                if (p.DutyPercent < list[i - 1].DutyPercent)
                {
                    error = $"Point {i + 1}: duty must not go down as temperature goes up.";
                    return null;
                }
            }
        }
        var curve = new FanCurve(list.AsReadOnly());
        if (enforceCritical && curve.Evaluate(criticalTempC) < requiredDutyAtCriticalPercent)
        {
            error = $"The curve must reach at least {requiredDutyAtCriticalPercent}% by {criticalTempC} C.";
            return null;
        }
        return curve;
    }

    /// <summary>
    /// Duty for a temperature, linearly interpolated between points, clamped to the first / last point outside the range.
    /// A NaN (unreadable) temperature returns 100: when in doubt, cool.
    /// </summary>
    public int Evaluate(double tempC)
    {
        if (double.IsNaN(tempC)) return 100;
        var pts = Points;
        if (tempC <= pts[0].TempC) return pts[0].DutyPercent;
        var last = pts[^1];
        if (tempC >= last.TempC) return last.DutyPercent;
        for (var i = 1; i < pts.Count; i++)
        {
            var b = pts[i];
            if (tempC > b.TempC) continue;
            var a = pts[i - 1];
            var t = (tempC - a.TempC) / (b.TempC - a.TempC);
            var duty = a.DutyPercent + t * (b.DutyPercent - a.DutyPercent);
            return (int)Math.Round(duty, MidpointRounding.AwayFromZero);
        }
        return last.DutyPercent;
    }

    /// <summary>Samples the curve at each integer temperature from <paramref name="fromTempC"/> to <paramref name="toTempC"/> inclusive.</summary>
    public int[] Sample(int fromTempC, int toTempC)
    {
        if (toTempC < fromTempC) throw new ArgumentException("toTempC must be >= fromTempC.");
        var result = new int[toTempC - fromTempC + 1];
        for (var t = fromTempC; t <= toTempC; t++) result[t - fromTempC] = Evaluate(t);
        return result;
    }
}

public static class Duty
{
    /// <summary>Clamps a duty request into [min, 100]. Throws when min itself is out of range.</summary>
    public static int Clamp(int requestedPercent, int minPercent)
    {
        if (minPercent < 0 || minPercent > 100) throw new ArgumentOutOfRangeException(nameof(minPercent));
        return Math.Clamp(requestedPercent, minPercent, 100);
    }

    public static bool IsValid(int percent, int minPercent) => percent >= minPercent && percent <= 100;
}

namespace AudioController.Core.Validation;

public sealed record LengthValidation(bool Accepted, double ActualSeconds, string? Error);

/// <summary>
/// Enforces the 30.000-second maximum sound length with exact integer math:
/// exactly 30.000 s is accepted, 30.001 s is rejected, zero length is rejected.
/// Files are never trimmed — only accepted or rejected.
/// </summary>
public static class SoundLengthValidator
{
    public const double MaxSeconds = 30.0;

    public static LengthValidation Validate(long samplesPerChannel, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        double actual = (double)samplesPerChannel / sampleRate;

        if (samplesPerChannel <= 0)
            return new(false, 0, "The file contains no audio (zero length).");

        // Accepted iff samplesPerChannel <= sampleRate * 30, exactly.
        if (samplesPerChannel > (long)sampleRate * 30L)
        {
            // Display value: nearest millisecond, but never at or below the limit — a sound
            // over the limit by less than half a millisecond must not be shown as "30.000 s",
            // which would contradict the rejection. ActualSeconds keeps the exact value.
            decimal shownMs = Math.Round((decimal)samplesPerChannel * 1000m / sampleRate, 0, MidpointRounding.AwayFromZero);
            if (shownMs <= (decimal)MaxSeconds * 1000m)
                shownMs = (decimal)MaxSeconds * 1000m + 1m;
            double shown = (double)(shownMs / 1000m);
            return new(false, actual,
                $"Sound is {shown:0.000} s long; the maximum is {MaxSeconds:0.000} s. The file was not imported (files are never trimmed).");
        }

        return new(true, actual, null);
    }
}

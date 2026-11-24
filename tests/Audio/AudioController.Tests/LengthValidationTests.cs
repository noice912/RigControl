using System.Globalization;
using AudioController.Core.Validation;
using Xunit;

namespace AudioController.Tests;

/// <summary>SPEC 4.2 / 7 "Length validation": exactly 30.000 s accepted, longer rejected, zero rejected, never trimmed.</summary>
public sealed class LengthValidationTests
{
    [Fact]
    public void MaxSeconds_IsThirty()
    {
        Assert.Equal(30.0, SoundLengthValidator.MaxSeconds);
    }

    [Fact]
    public void Validate_Exactly30SecondsAt48kHz_IsAccepted()
    {
        var result = SoundLengthValidator.Validate(1_440_000, 48_000);

        Assert.True(result.Accepted);
        Assert.Null(result.Error);
        Assert.Equal(30.0, result.ActualSeconds);
    }

    [Fact]
    public void Validate_Exactly30SecondsAt44100Hz_IsAccepted()
    {
        var result = SoundLengthValidator.Validate(1_323_000, 44_100);

        Assert.True(result.Accepted);
        Assert.Null(result.Error);
        Assert.Equal(30.0, result.ActualSeconds);
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(11_025)]
    [InlineData(16_000)]
    [InlineData(22_050)]
    [InlineData(32_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(88_200)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    [InlineData(384_000)]
    public void Validate_Exactly30SecondsAtAnyRate_IsAccepted_AndOneMoreSampleIsRejected(int sampleRate)
    {
        long limit = 30L * sampleRate;

        Assert.True(SoundLengthValidator.Validate(limit, sampleRate).Accepted);
        Assert.True(SoundLengthValidator.Validate(limit - 1, sampleRate).Accepted);
        Assert.False(SoundLengthValidator.Validate(limit + 1, sampleRate).Accepted);
    }

    [Fact]
    public void Validate_OneSampleOver30SecondsAt48kHz_IsRejected()
    {
        var result = SoundLengthValidator.Validate(1_440_001, 48_000);

        Assert.False(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.True(result.ActualSeconds > 30.0);
    }

    [Fact]
    public void Validate_OneSampleOver30SecondsAt44100Hz_IsRejected()
    {
        var result = SoundLengthValidator.Validate(1_323_001, 44_100);

        Assert.False(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Validate_30Point001SecondsAt48kHz_IsRejectedWithActualLengthToThreeDecimals()
    {
        using var _ = CultureScope.Invariant();

        var result = SoundLengthValidator.Validate(1_440_048, 48_000);

        Assert.False(result.Accepted);
        Assert.Equal(30.001, result.ActualSeconds, precision: 9);
        Assert.NotNull(result.Error);
        Assert.Contains("30.001", result.Error);
        Assert.Contains("30.000", result.Error); // the limit is stated too
        Assert.Contains("trim", result.Error, StringComparison.OrdinalIgnoreCase); // and that nothing was trimmed
    }

    [Fact]
    public void Validate_30Point001SecondsAt44100Hz_IsRejectedWithActualLength()
    {
        using var _ = CultureScope.Invariant();

        // 30.001 s * 44 100 = 1 323 044.1 samples -> 1 323 045 samples is 30.00102... s
        var result = SoundLengthValidator.Validate(1_323_045, 44_100);

        Assert.False(result.Accepted);
        Assert.Contains("30.001", result.Error);
    }

    [Theory]
    [InlineData(1_440_001L, 48_000)]  // 30.0000208 s
    [InlineData(1_440_023L, 48_000)]  // 30.0004792 s
    [InlineData(1_323_001L, 44_100)]  // 30.0000227 s
    public void Validate_JustOver30Seconds_MessageShowsALengthGreaterThanTheMaximum(long samples, int sampleRate)
    {
        // SPEC 4.2: "Longer files are rejected with a clear message showing the actual length."
        // A rejection that reads "Sound is 30.000 s long; the maximum is 30.000 s" contradicts itself, so the
        // length shown to the user must itself be greater than the 30.000 s maximum it is compared against.
        using var _ = CultureScope.Invariant();

        var result = SoundLengthValidator.Validate(samples, sampleRate);

        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        var shown = System.Text.RegularExpressions.Regex.Match(result.Error!, @"\d+\.\d+");
        Assert.True(shown.Success, $"No length found in: {result.Error}");
        double shownSeconds = double.Parse(shown.Value, CultureInfo.InvariantCulture);
        Assert.True(
            shownSeconds > SoundLengthValidator.MaxSeconds,
            $"Rejected a {result.ActualSeconds:R} s sound, but the message shows {shown.Value} s, which is not over the " +
            $"{SoundLengthValidator.MaxSeconds:0.000} s maximum: \"{result.Error}\"");
    }

    [Fact]
    public void Validate_LongFile_ReportsItsRealLength()
    {
        using var _ = CultureScope.Invariant();

        var result = SoundLengthValidator.Validate(48_000L * 3600 + 24_000, 48_000); // one hour and half a second

        Assert.False(result.Accepted);
        Assert.Equal(3600.5, result.ActualSeconds);
        Assert.Contains("3600.500", result.Error);
    }

    [Fact]
    public void Validate_RejectionMessage_UsesCurrentCultureConsistently()
    {
        // The message is user-facing; whatever culture it is shown in, the actual length must be present
        // with three decimals in that culture's own format.
        var culture = CultureInfo.GetCultureInfo("de-DE");
        using var _ = new CultureScope(culture);

        var result = SoundLengthValidator.Validate(1_440_048, 48_000);

        Assert.False(result.Accepted);
        Assert.Contains(30.001.ToString("0.000", culture), result.Error);
    }

    [Fact]
    public void Validate_ZeroSamples_IsRejected()
    {
        var result = SoundLengthValidator.Validate(0, 48_000);

        Assert.False(result.Accepted);
        Assert.Equal(0, result.ActualSeconds);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("zero", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-48_000L)]
    [InlineData(long.MinValue)]
    public void Validate_NegativeSamples_IsRejected(long samples)
    {
        var result = SoundLengthValidator.Validate(samples, 48_000);

        Assert.False(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Theory]
    [InlineData(1L, 48_000)]
    [InlineData(1L, 44_100)]
    [InlineData(48_000L, 48_000)]
    [InlineData(1_392_000L, 48_000)] // 29 s
    public void Validate_PositiveLengthUpTo30Seconds_IsAccepted(long samples, int sampleRate)
    {
        var result = SoundLengthValidator.Validate(samples, sampleRate);

        Assert.True(result.Accepted);
        Assert.Null(result.Error);
        Assert.Equal((double)samples / sampleRate, result.ActualSeconds);
    }

    [Fact]
    public void Validate_HugeSampleCount_IsRejectedWithoutOverflow()
    {
        var result = SoundLengthValidator.Validate(long.MaxValue, 48_000);

        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-48_000)]
    [InlineData(int.MinValue)]
    public void Validate_ZeroOrNegativeSampleRate_ThrowsArgumentOutOfRangeException(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SoundLengthValidator.Validate(48_000, sampleRate));
        Assert.Throws<ArgumentOutOfRangeException>(() => SoundLengthValidator.Validate(0, sampleRate));
    }
}

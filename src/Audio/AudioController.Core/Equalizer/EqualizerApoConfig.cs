using System.Globalization;
using System.Text;

namespace AudioController.Core.Equalizer;

/// <summary>
/// Pure text generation for Equalizer APO (https://sourceforge.net/projects/equalizerapo/): the app's 10 EQ sliders
/// as an Equalizer APO configuration file, scoped to one playback device, plus the one-line
/// <c>Include: AudioController.txt</c> edit of Equalizer APO's own <c>config.txt</c>. No I/O.
/// </summary>
/// <remarks>
/// <para>Syntax follows the official Configuration reference
/// (https://sourceforge.net/p/equalizerapo/wiki/Configuration%20reference/):</para>
/// <list type="bullet">
/// <item><c>Device: &lt;pattern&gt;[; &lt;pattern&gt;…]</c> — every space-separated word of a pattern must be found in
/// the current output device's "device name + connection name + GUID" string; one matching pattern is enough; when
/// none matches, all following commands except <c>Device</c> are ignored; the pattern <c>all</c> always matches.
/// The app matches the endpoint GUID alone, in braces (<c>{9a828b4a-…}</c>) — the most precise selector: a GUID is
/// unique to one endpoint, unlike device or connection names (the Crusher's headphones and hands-free mic share
/// "Crusher ANC 2"). Measured with Benchmark.exe's verbose trace: the braced GUID matches in any letter case; the bare
/// GUID does not. The file resets with <c>Device: all</c> at the end so nothing after the include is scoped.</item>
/// <item><c>Channel: &lt;list&gt;</c> — selects the channels the following filters apply to; <c>all</c> selects every
/// channel. Like the device scope it carries over from config.txt into an included file, so the app's file starts
/// with <c>Channel: all</c> (after <c>Device:</c>, so other devices never see it). <c>Stage:</c> is left untouched.</item>
/// <item><c>Preamp: &lt;number&gt; dB</c> — pre-amplification; preamps that apply to the same channel add up.</item>
/// <item><c>Filter &lt;n&gt;: ON PK Fc &lt;Hz&gt; Hz Gain &lt;dB&gt; dB Q &lt;Q&gt;</c> — a peaking filter (the RBJ peaking EQ,
/// the same biquad as NAudio's <c>BiQuadFilter.PeakingEQ</c> used by the app's own <c>TenBandEq</c>).</item>
/// <item><c>Include: &lt;file&gt;</c> — loads another configuration file (relative to the config folder).</item>
/// </list>
/// <para>Numbers use the invariant culture ("." decimal separator) whatever the user's Windows culture is.</para>
/// </remarks>
public static class EqualizerApoConfig
{
    /// <summary>The file the app owns inside Equalizer APO's config folder.</summary>
    public const string FileName = "AudioController.txt";

    /// <summary>The line the app keeps (exactly once) in Equalizer APO's <c>config.txt</c>.</summary>
    public const string IncludeLine = "Include: " + FileName;

    /// <summary>Equalizer APO's main configuration file.</summary>
    public const string MainConfigFileName = "config.txt";

    /// <summary>First line of every generated file.</summary>
    public const string HeaderLine =
        "# Written by Audio Controller from its EQ sliders - edits here are overwritten. Applies to your headset only.";

    /// <summary>
    /// Written right after the <c>Device:</c> line: Equalizer APO processes an <c>Include:</c> inline and keeps the
    /// channel selection across files, so a <c>Channel: L</c> left active at the end of the user's own config.txt
    /// would otherwise narrow the app's filters to that channel. <c>Stage:</c> is deliberately left as it is (the
    /// user's choice, Equalizer APO's default being the normal playback stage).
    /// </summary>
    public const string ChannelResetLine = "Channel: all";

    /// <summary>Quality factor of every band (same as the app's TenBandEq).</summary>
    public const float BandQ = 1.0f;

    /// <summary>Lowest band gain in dB (same clamp as the app's TenBandEq).</summary>
    public const float MinGainDb = -24f;

    /// <summary>Highest band gain in dB (same clamp as the app's TenBandEq).</summary>
    public const float MaxGainDb = 24f;

    /// <summary>Bands within this many dB of 0 are left out, exactly like TenBandEq.</summary>
    public const float IdentityThresholdDb = 0.01f;

    private const string Newline = "\r\n";

    private static readonly float[] Frequencies = [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];

    /// <summary>Band centre frequencies in Hz: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k (TenBandEq's bands).</summary>
    public static IReadOnlyList<float> BandFrequenciesHz { get; } = Array.AsReadOnly(Frequencies);

    /// <summary>The content written while Audio Controller's EQ is not applied through Equalizer APO: no commands at all.</summary>
    public static string NeutralText { get; } =
        HeaderLine + Newline + "# Neutral: \"EQ everything I hear\" is off, so nothing is applied." + Newline;

    /// <summary>
    /// The configuration for the 10 sliders. One <c>PK</c> filter per band whose gain is not within 0.01 dB of 0 (gains
    /// clamped to ±24 dB, NaN = 0, missing bands = 0, extra values ignored — like TenBandEq). When
    /// <paramref name="eqEnabled"/> is false or every band is flat there are no filters. With
    /// <paramref name="autoPreamp"/> the preamp is −(largest positive band gain) dB, so the boosted band peaks at 0 dB
    /// and cannot clip; otherwise (or when nothing is boosted) it is 0 dB. Everything is scoped to
    /// <paramref name="deviceSelector"/>, applies to all channels (<see cref="ChannelResetLine"/>, whatever config.txt
    /// selected before the include) and is reset with <c>Device: all</c>.
    /// </summary>
    /// <param name="gainsDb">The ten band gains in dB.</param>
    /// <param name="eqEnabled">The app's "Enable EQ" switch.</param>
    /// <param name="deviceSelector">A <c>Device:</c> pattern, normally <see cref="DeviceSelectorFor"/> of the headset.</param>
    /// <param name="autoPreamp">Lower the level by the biggest boost (default on in the app).</param>
    /// <returns>The complete file text (CRLF line endings).</returns>
    public static string Generate(float[] gainsDb, bool eqEnabled, string deviceSelector, bool autoPreamp)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        var frequencies = Frequencies.Select(static f => (double)f).ToArray();
        return Generate(FxState.FromGains(frequencies, gainsDb, eqEnabled, BandQ), deviceSelector, autoPreamp);
    }

    /// <summary>
    /// The configuration for the whole Effects panel (<paramref name="fx"/>), scoped to <paramref name="deviceSelector"/>.
    /// Order: <c>Preamp</c>; one <c>Filter N: ON PK …</c> per non-flat graphic-EQ band; the effect filters (Bass Boost
    /// <c>LS</c>, Fidelity <c>HS</c> + <c>PK</c>, Dynamic Boost <c>LS</c> + <c>HS</c>, numbered on); Surround as
    /// <c>Copy: L=(1+k)*L+-k*R R=(1+k)*R+-k*L</c> (stereo devices only, left out at 0 %); Ambience as
    /// <c>Convolution: ambience.wav</c> (left out at 0 %; the caller writes <see cref="AmbienceImpulse"/> next to this
    /// file). Power off or everything flat gives the neutral scoped file (<c>Preamp: 0 dB</c>, no filters).
    /// </summary>
    /// <param name="fx">The Effects snapshot (<see cref="FxState.From"/>).</param>
    /// <param name="deviceSelector">A <c>Device:</c> pattern, normally <see cref="DeviceSelectorFor"/> of the headset.</param>
    /// <param name="autoPreamp">Pre-amp = <see cref="FxState.AutoPreampDb"/> (else 0 dB).</param>
    /// <param name="channels">The device's channel count: Surround's <c>Copy</c> is written only for 2.</param>
    /// <returns>The complete file text (CRLF line endings, invariant numbers).</returns>
    public static string Generate(FxState fx, string deviceSelector, bool autoPreamp, int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(fx);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSelector);
        if (deviceSelector.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("The device selector must be a single line.", nameof(deviceSelector));
        }

        double preamp = autoPreamp ? fx.AutoPreampDb : 0;

        var sb = new StringBuilder();
        sb.Append(HeaderLine).Append(Newline);
        sb.Append("Device: ").Append(deviceSelector.Trim()).Append(Newline);
        sb.Append(ChannelResetLine).Append(Newline);
        sb.Append("Preamp: ").Append(Number(preamp)).Append(" dB").Append(Newline);
        int n = 1;
        foreach (var filter in fx.EqFilters.Concat(fx.EffectFilters))
        {
            sb.Append(FilterLine(n++, filter)).Append(Newline);
        }

        if (channels == 2 && fx.SurroundWidth > 0)
        {
            sb.Append(CopyLine(fx.SurroundWidth)).Append(Newline);
        }

        if (fx.AmbienceWet > 0)
        {
            sb.Append(ConvolutionLine).Append(Newline);
        }

        sb.Append("Device: all").Append(Newline);
        return sb.ToString();
    }

    /// <summary>The Ambience line (Equalizer APO resolves the file relative to this configuration file's folder).</summary>
    public const string ConvolutionLine = "Convolution: " + AmbienceImpulse.FileName;

    /// <summary>
    /// <c>Filter n: ON PK Fc 1000 Hz Gain 6 dB Q 1</c>, <c>… ON LS … Q 0.7071</c>, <c>… ON HS …</c> — the Configuration
    /// reference's "Filter &lt;n&gt;: ON &lt;Type&gt; Fc &lt;Frequency&gt; Hz Gain &lt;Gain value&gt; dB Q &lt;Q value&gt;"
    /// (Q on LS/HS since Equalizer APO 1.2.1; Fc is then the shelf's centre frequency, as in the RBJ cookbook).
    /// </summary>
    public static string FilterLine(int number, FxFilter filter)
    {
        string type = filter.Type switch
        {
            FxFilterType.LowShelf => "LS",
            FxFilterType.HighShelf => "HS",
            _ => "PK",
        };

        return "Filter " + number.ToString(CultureInfo.InvariantCulture) + ": ON " + type
               + " Fc " + Number(filter.FrequencyHz) + " Hz Gain " + Number(filter.GainDb) + " dB Q " + Factor(filter.Q);
    }

    /// <summary>
    /// Stereo widening with width k: <c>Copy: L=(1+k)*L+-k*R R=(1+k)*R+-k*L</c> (the mid stays, the side is ×(1+2k)).
    /// Equalizer APO's Copy takes "&lt;Target&gt;=&lt;Factor&gt;*&lt;Source&gt;+…" terms; a negative factor is written
    /// "+-", as in the reference's "Copy: L=R+-6dB*C" example.
    /// </summary>
    public static string CopyLine(double width)
    {
        string a = Factor(1 + width);
        string k = Factor(width);
        return "Copy: L=" + a + "*L+-" + k + "*R R=" + a + "*R+-" + k + "*L";
    }

    /// <summary>
    /// The bands that get a filter: (frequency, clamped gain) for every band at least 0.01 dB from 0, in band order;
    /// empty when <paramref name="eqEnabled"/> is false.
    /// </summary>
    public static IReadOnlyList<(float FrequencyHz, float GainDb)> ActiveBands(float[] gainsDb, bool eqEnabled)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        var result = new List<(float, float)>(Frequencies.Length);
        if (!eqEnabled)
        {
            return result;
        }

        for (int i = 0; i < Frequencies.Length; i++)
        {
            float g = i < gainsDb.Length ? gainsDb[i] : 0f;
            g = float.IsNaN(g) ? 0f : Math.Clamp(g, MinGainDb, MaxGainDb);
            if (MathF.Abs(g) >= IdentityThresholdDb)
            {
                result.Add((Frequencies[i], g));
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>Device:</c> pattern for a Windows endpoint id such as
    /// <c>{0.0.0.00000000}.{9a828b4a-e626-46d3-b084-908d7994216b}</c>: its endpoint GUID in braces, lower case
    /// (<c>{9a828b4a-e626-46d3-b084-908d7994216b}</c>, the form of the endpoint's registry key). Null when the id holds
    /// no GUID.
    /// </summary>
    /// <remarks>
    /// Verified with Equalizer APO 1.4.2's Benchmark.exe (<c>--guid</c>, verbose trace): a braced GUID matches the
    /// device's GUID in either letter case, while the same GUID without braces does not match.
    /// </remarks>
    public static string? DeviceSelectorFor(string? endpointId)
    {
        var guid = EndpointGuid(endpointId);
        return guid?.ToString("B", CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    /// <summary>
    /// The endpoint GUID of a Windows endpoint id (<c>{0.0.X.00000000}.{guid}</c>), or of a bare <c>{guid}</c> /
    /// <c>guid</c>; null when there is none. This GUID is also the endpoint's registry key name under
    /// <c>MMDevices\Audio\Render|Capture</c>.
    /// </summary>
    public static Guid? EndpointGuid(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        string s = endpointId.Trim();
        int dot = s.LastIndexOf("}.{", StringComparison.Ordinal);
        if (dot >= 0)
        {
            s = s[(dot + 2)..];
        }

        return Guid.TryParse(s, out var g) ? g : null;
    }

    /// <summary>
    /// The Windows endpoint id for an <c>MMDevices\Audio\Render|Capture\{guid}</c> registry key:
    /// <c>{0.0.0.00000000}.{guid}</c> for playback, <c>{0.0.1.00000000}.{guid}</c> for recording (lower case, like
    /// the ids Windows hands out). Null when <paramref name="registryKeyName"/> is not a GUID.
    /// </summary>
    public static string? EndpointIdFor(bool capture, string? registryKeyName)
    {
        var guid = EndpointGuid(registryKeyName);
        return guid is null
            ? null
            : (capture ? "{0.0.1.00000000}." : "{0.0.0.00000000}.") + guid.Value.ToString("B", CultureInfo.InvariantCulture);
    }

    /// <summary>True when the endpoint id is a recording (capture) endpoint, judged by its <c>{0.0.1.…}</c> prefix.</summary>
    public static bool IsCaptureEndpointId(string? endpointId) =>
        endpointId is not null && endpointId.TrimStart().StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Equalizer APO's <c>config.txt</c> with exactly one <see cref="IncludeLine"/>: appended when missing (on a new line,
    /// using the file's own line ending), extra copies removed; every other line is kept exactly as it was (comments,
    /// the user's own filters, other includes — <c>example.txt</c> is never re-added). Lines that already include
    /// <see cref="FileName"/> with other spacing or letter case count as the include. Returns
    /// <paramref name="configText"/> itself (same reference) when nothing needs to change.
    /// </summary>
    public static string EnsureInclude(string? configText)
    {
        string text = configText ?? "";
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n"
            : "\r\n";

        // Split keeping each line's own terminator so untouched lines come back byte for byte.
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                lines.Add(text[start..]);
                break;
            }

            lines.Add(text[start..(nl + 1)]);
            start = nl + 1;
        }

        int found = 0;
        var kept = new List<string>(lines.Count + 1);
        foreach (var line in lines)
        {
            if (IsOurInclude(line))
            {
                found++;
                if (found > 1)
                {
                    continue; // duplicate
                }
            }

            kept.Add(line);
        }

        if (found == 1)
        {
            return configText ?? "";
        }

        var sb = new StringBuilder(text.Length + IncludeLine.Length + 4);
        foreach (var line in kept)
        {
            sb.Append(line);
        }

        if (found == 0)
        {
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.Append(newline);
            }

            sb.Append(IncludeLine).Append(newline);
        }

        return sb.ToString();
    }

    /// <summary>True for an (uncommented) <c>Include:</c> line naming <see cref="FileName"/>.</summary>
    public static bool IsOurInclude(string? line)
    {
        if (line is null)
        {
            return false;
        }

        string t = line.Trim();
        if (!t.StartsWith("Include", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int colon = t.IndexOf(':');
        if (colon < 0 || !string.IsNullOrWhiteSpace(t[7..colon]))
        {
            return false;
        }

        return string.Equals(t[(colon + 1)..].Trim(), FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Invariant, up to 3 decimals: 31, 1000, -3.5, 0.25, 1.</summary>
    private static string Number(double value)
    {
        if (Math.Abs(value) < 0.0005)
        {
            return "0"; // never "-0"
        }

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Invariant, up to 4 decimals (Q values and Copy factors): 1, 4.32, 0.7071, 1.25.</summary>
    private static string Factor(double value)
    {
        if (Math.Abs(value) < 0.00005)
        {
            return "0";
        }

        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

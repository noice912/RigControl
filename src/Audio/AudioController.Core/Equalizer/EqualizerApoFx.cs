namespace AudioController.Core.Equalizer;

/// <summary>
/// Pure rules for reading Equalizer APO's attachment out of an endpoint's FxProperties registry key
/// (<c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{Render|Capture}\{guid}\FxProperties</c>).
/// </summary>
public static class EqualizerApoFx
{
    /// <summary>Equalizer APO's pre-mix APO CLSID.</summary>
    public const string PreMixClsid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";

    /// <summary>Equalizer APO's post-mix APO CLSID.</summary>
    public const string PostMixClsid = "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}";

    /// <summary>The FX property set whose ",N" values hold the endpoint's APO CLSIDs (PKEY_FX_*EffectClsid etc.).</summary>
    public const string FxClsidPropertySet = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";

    /// <summary>
    /// True when the registry value <paramref name="valueName"/> (e.g. <c>{d04e05a6-…},5</c>) of an FxProperties key is
    /// one of the FX CLSID properties and its data (a string or a multi-string) names Equalizer APO's pre- or post-mix
    /// APO. Case-insensitive; anything else (other properties, binary data, null) is false.
    /// </summary>
    public static bool NamesEqualizerApo(string? valueName, object? value)
    {
        if (valueName is null
            || !valueName.Trim().StartsWith(FxClsidPropertySet + ",", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value switch
        {
            string s => MentionsApo(s),
            string[] many => many.Any(MentionsApo),
            _ => false,
        };
    }

    private static bool MentionsApo(string? s) =>
        s is not null
        && (s.Contains(PreMixClsid, StringComparison.OrdinalIgnoreCase)
            || s.Contains(PostMixClsid, StringComparison.OrdinalIgnoreCase));
}

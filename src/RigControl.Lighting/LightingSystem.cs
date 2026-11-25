namespace RigControl.Lighting;

/// <summary>The screen's picture, shrunk to the sync grid.</summary>
internal interface IScreenPicture : IDisposable
{
    /// <summary>The last picture read (48x27 BGRA).</summary>
    byte[] Grid { get; }

    /// <summary>Reads the screen now and returns its one colour (0xRRGGBB).</summary>
    int Read();
}

/// <summary>A capture card's picture.</summary>
internal interface ICapturePicture : IDisposable
{
    string? Size { get; }
    bool Shared { get; }
    byte[]? Grid { get; }
    Task StartAsync(string deviceId, bool allowExclusive = true);
    /// <summary>The newest frame's one colour, or null when there is no new frame.</summary>
    int? Read();
}

/// <summary>A capture card's picture as a streaming program (OBS) shows it.</summary>
internal interface IObsPicture : IDisposable
{
    string? SourceName { get; }
    byte[]? Grid { get; }
    Task StartAsync(string cardName, CancellationToken ct = default);
    int? Read();
}

/// <summary>How hard the bass, the mid range and the treble of what the computer plays hit right now, 0 to 1 each.</summary>
internal interface IAudioMeter : IDisposable
{
    float Level { get; }
    float MidLevel { get; }
    float TrebleLevel { get; }
    /// <summary>Why the sound can't be heard, in plain words; null when it can.</summary>
    string? Problem { get; }
}

/// <summary>
/// What the system RigControl runs on gives the lights to follow: a picture (the screen, a capture card, OBS) and
/// a sound meter. The Windows build has all of them. The Linux and macOS build has none in this version: there the
/// lights play the effects that need neither a picture nor sound.
/// A part that is null is not offered: it is left out of the lists, and a direct call for it answers
/// "Not available on this system".
/// (Starting OpenRGB and GIGABYTE Control Center are Windows-only too; those are decided by the
/// <see cref="IOpenRgb"/> and <see cref="IGigabyteControlCenter"/> the module is given.)
/// </summary>
internal sealed class LightingSystem
{
    /// <summary>The effect played when the saved one isn't offered here.</summary>
    public const string FallbackEffect = "solid";

    public Func<IScreenPicture>? Screen { get; init; }
    public Func<Task<IReadOnlyList<CaptureDeviceDto>>>? CaptureDevices { get; init; }
    public Func<ICapturePicture>? CaptureCard { get; init; }
    public Func<IObsPicture>? Obs { get; init; }
    public Func<IAudioMeter>? Audio { get; init; }

    /// <summary>A capture card falls back to the screen while it can't be read, so it needs the screen too.</summary>
    public bool HasCaptureCard => Screen is not null && CaptureCard is not null && CaptureDevices is not null;

    /// <summary>True when this effect can play here ("screen" needs the screen; the sound effects need the meter).</summary>
    public bool Offers(string effect) =>
        effect == EffectRenderer.Screen ? Screen is not null : Audio is not null || !EffectRenderer.IsAudio(effect);

    /// <summary>The saved effect, or <see cref="FallbackEffect"/> when the saved one isn't offered here.</summary>
    public string EffectOr(string saved) => Offers(saved) ? saved : FallbackEffect;

    /// <summary>The effects library as it is on this system.</summary>
    public IReadOnlyList<EffectInfo> Effects => _effects ??= EffectRenderer.Catalog.Where(e => Offers(e.Id)).ToList();
    private IReadOnlyList<EffectInfo>? _effects;

    /// <summary>The names sent to the interface in app.getInfo ("capabilities"), as listed in the module catalogue.</summary>
    /// <param name="startsOpenRgb">RigControl finds and starts OpenRGB itself (false: the user starts it).</param>
    /// <param name="gcc">GIGABYTE Control Center can be looked for, taken over from and given back to.</param>
    public IReadOnlyCollection<string> Capabilities(bool startsOpenRgb, bool gcc)
    {
        var list = new List<string> { "lighting.openrgb", "lighting.network", "lighting.effects", "lighting.layout", "lighting.scenes" };
        if (Screen is not null) list.Add("lighting.screen");
        if (HasCaptureCard) list.Add("lighting.captureCard");
        if (Audio is not null) list.Add("lighting.audio");
        if (startsOpenRgb) list.Add("lighting.openrgbServer");
        if (gcc) list.Add("lighting.gcc");
        return list;
    }

    /// <summary>Nothing to follow: what the Linux and macOS build has.</summary>
    public static LightingSystem Portable() => new();

    /// <summary>What this build of the program has.</summary>
    public static LightingSystem ThisBuild()
    {
#if WINDOWS
        return new LightingSystem
        {
            Screen = () => new ScreenSource(),
            CaptureDevices = CaptureSource.ListAsync,
            CaptureCard = () => new CaptureSource(),
            Obs = () => new ObsSource(),
            Audio = () => new BassMeter(),
        };
#else
        return Portable();
#endif
    }
}

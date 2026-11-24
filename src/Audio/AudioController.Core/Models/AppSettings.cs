namespace AudioController.Core.Models;

/// <summary>Everything the user can configure. Stored inside the encrypted library.dat.</summary>
public sealed class AppSettings
{
    /// <summary>The real microphone (capture endpoint id). One of the two things the user picks in Setup.</summary>
    public string? InputDeviceId { get; set; }

    /// <summary>
    /// The user's headset / headphones (playback endpoint id) — the other Setup pick. Soundboard monitoring,
    /// "EQ everything I hear" and the Windows default output all follow it (see Routing.RoutingPlanner).
    /// Null until the user picks one or <c>RoutingPlanner.MigrateSettings</c> fills it from older settings.
    /// </summary>
    public string? HeadsetDeviceId { get; set; }

    /// <summary>
    /// Friendly name of <see cref="InputDeviceId"/> when it was picked (or last seen). Re-pairing a Bluetooth device
    /// makes Windows create new endpoints with new ids; the name lets <c>RoutingPlanner.ReadoptRenamedIds</c> find it.
    /// </summary>
    public string? InputDeviceName { get; set; }

    /// <summary>Friendly name of <see cref="HeadsetDeviceId"/> when it was picked (or last seen). See <see cref="InputDeviceName"/>.</summary>
    public string? HeadsetDeviceName { get; set; }

    /// <summary>
    /// "EQ everything I hear": the EQ sliders apply to all PC audio on the headset — through Equalizer APO when it is
    /// attached to the headset, otherwise through the System EQ router when a spare virtual playback device exists.
    /// Null = never chosen: on only when System EQ was on before 1.4 (Equalizer APO on the headset is offered, not
    /// switched on, until the user ticks it).
    /// </summary>
    public bool? EqAllAudio { get; set; }

    /// <summary>
    /// Equalizer APO auto pre-amp. On: the whole level is lowered by your biggest boost (EQ band plus effects), so a
    /// boost can never clip — but raising one slider then makes everything else quieter. Off (the default since 1.6, for
    /// new installs and for libraries that never stored it): raising a slider changes only that slider's sound and the
    /// overall level stays put; big boosts can distort on loud songs. A value the user stored is never changed.
    /// </summary>
    public bool EqAutoPreamp { get; set; }

    /// <summary>
    /// The virtual cable playback endpoint ("CABLE Input") the mix is written to. Advanced override; when it is
    /// not set or not connected, CABLE Input is found automatically.
    /// </summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Local output for "Hear soundboard locally". Written from the routing plan (= the headset); choosing
    /// another device in Settings → Advanced overrides it until the headset is picked again.
    /// </summary>
    public string? MonitorDeviceId { get; set; }

    /// <summary>"Hear soundboard locally" (on by default).</summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>"Mute Mic": the engine releases the microphone completely; the soundboard keeps playing.</summary>
    public bool MicMuted { get; set; }

    public string StopAllHotkey { get; set; } = "Ctrl+Alt+End";

    /// <summary>
    /// The EQ section's own on/off switch (graphic EQ only; the effects follow <see cref="FxEnabled"/>). Applies to the
    /// soundboard bus and, with "EQ everything I hear", to all PC audio on the headset — never to the microphone.
    /// </summary>
    public bool EqEnabled { get; set; }

    /// <summary>
    /// The 10 band gains in dB as written by versions up to 1.4. Since the Effects panel the source of truth is
    /// <see cref="EqBands"/>; this array is still kept equal to its gains so an older version reading the same library
    /// keeps the user's EQ.
    /// </summary>
    public float[] EqGainsDb { get; set; } = new float[10];

    /// <summary>
    /// The Effects panel's power button (FXSound's "FX On/Off"): when false nothing is applied anywhere (EQ and effects
    /// neutral). Default on, so an EQ that was on before the Effects panel existed stays on.
    /// </summary>
    public bool FxEnabled { get; set; } = true;

    /// <summary>The EQ layout a new install (or a library that never stored one) starts with: 31 third-octave bands.</summary>
    public const EqMode DefaultEqMode = EqMode.ThirtyOneBandThirdOctave;

    /// <summary>
    /// 31 fixed third-octave bands (the default since 1.6) or 10 adjustable bands. A stored value is never changed; a
    /// library from before the Effects panel whose 10-slider EQ was not flat keeps the 10 bands (its gains live there).
    /// </summary>
    public EqMode EqMode { get; set; } = DefaultEqMode;

    /// <summary>
    /// The 10 adjustable bands (centre frequency + gain). Null in a library written before the Effects panel: it is then
    /// filled from <see cref="EqGainsDb"/> on the default frequencies (<c>Equalizer.FxSettings.Normalize</c>).
    /// </summary>
    public List<EqBand>? EqBands { get; set; }

    /// <summary>The 31 third-octave band gains in dB (ISO 20 Hz … 20 kHz). Null until first normalized.</summary>
    public float[]? EqGains31Db { get; set; }

    /// <summary>The five effect sliders (0–100 % each).</summary>
    public EffectLevels Effects { get; set; } = new();

    /// <summary>Name of the preset last loaded or saved; null = "Custom" (no preset).</summary>
    public string? FxPresetName { get; set; }

    /// <summary>A control was changed after <see cref="FxPresetName"/> was loaded (shown as "(modified)").</summary>
    public bool FxPresetModified { get; set; }

    /// <summary>
    /// System EQ: loopback-capture <see cref="SystemEqSourceDeviceId"/> (a virtual playback device set as the
    /// Windows default output), run it through the same EQ, and play it on <see cref="SystemEqOutputDeviceId"/>.
    /// Independent of the virtual microphone; the microphone is never equalized. Since v1.4 the app keeps it true
    /// exactly while "EQ everything I hear" runs through the router (kept for older versions reading the same library).
    /// </summary>
    public bool SystemEqEnabled { get; set; }

    /// <summary>
    /// The playback endpoint System EQ captures (never "CABLE Input"). Written from the routing plan (the
    /// automatically chosen inlet); a valid choice made in Settings → Advanced is kept.
    /// </summary>
    public string? SystemEqSourceDeviceId { get; set; }

    /// <summary>
    /// The playback endpoint System EQ renders to. Written from the routing plan (= the headset); an Advanced
    /// override is kept until the headset is picked again.
    /// </summary>
    public string? SystemEqOutputDeviceId { get; set; }

    /// <summary>
    /// "Also play on" master switch (default off): everything that plays on the headset — after Equalizer APO, so with
    /// the user's EQ and effects — is copied to the ticked <see cref="MirrorTargets"/> while the app runs.
    /// See <c>Routing.MirrorPlanner</c>.
    /// </summary>
    public bool MirrorEnabled { get; set; }

    /// <summary>
    /// The extra outputs for "Also play on" (one entry per device the user ever ticked; unticked entries keep their
    /// volume and delay). Never the headset itself, the virtual-mic cable or a virtual device.
    /// </summary>
    public List<MirrorTarget> MirrorTargets { get; set; } = new();

    /// <summary>Whether "set default" also sets the communications role.</summary>
    public bool SetDefaultCommunications { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 760;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    /// <summary>The window was maximized; the bounds above are then its normal (restore) bounds.</summary>
    public bool WindowMaximized { get; set; }
}

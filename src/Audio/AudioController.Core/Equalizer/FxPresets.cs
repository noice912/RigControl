using AudioController.Core.Models;

namespace AudioController.Core.Equalizer;

/// <summary>
/// The Effects panel's presets (pure): the built-in ones as data, and the user's own in
/// <see cref="Library.UserPresets"/> ("Save as…" / "Delete"). Loading a preset copies its EQ mode, bands and effect
/// levels into <see cref="AppSettings"/>; the power switch is left alone.
/// </summary>
public static class FxPresets
{
    /// <summary>The neutral preset (every band 0 dB, every effect 0 %).</summary>
    public const string FlatName = "Flat";

    /// <summary>Longest preset name accepted.</summary>
    public const int MaxNameLength = 40;

    private static readonly FxPreset[] BuiltIns =
    [
        Make(FlatName, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], bass: 0, fidelity: 0, ambience: 0, surround: 0, dynamic: 0),
        Make("Music", [3, 2, 1, 0, 0, 0, 1, 2, 2, 1], bass: 20, fidelity: 30, ambience: 10, surround: 20, dynamic: 30),
        Make("Rock", [4, 3, 1, -1, -2, 0, 2, 3, 4, 3], bass: 30, fidelity: 30, ambience: 5, surround: 20, dynamic: 40),
        Make("Pop", [-1, 1, 3, 4, 2, 0, -1, 1, 2, 2], bass: 15, fidelity: 25, ambience: 10, surround: 15, dynamic: 35),
        Make("Hip-Hop", [5, 4, 2, 1, -1, -1, 1, 1, 2, 2], bass: 50, fidelity: 20, ambience: 5, surround: 15, dynamic: 40),
        Make("Electronic", [4, 3, 1, 0, -2, 1, 0, 2, 4, 4], bass: 40, fidelity: 35, ambience: 10, surround: 30, dynamic: 45),
        Make("Gaming", [2, 1, 0, -1, -1, 1, 3, 4, 3, 1], bass: 10, fidelity: 40, ambience: 0, surround: 50, dynamic: 30),
        Make("Movies", [3, 2, 1, 0, 0, 1, 2, 2, 1, 0], bass: 25, fidelity: 20, ambience: 20, surround: 40, dynamic: 35),
        Make("Voice & Podcast", [-6, -4, -2, 0, 2, 3, 3, 2, 0, -2], bass: 0, fidelity: 20, ambience: 0, surround: 0, dynamic: 20),
        Make("Treble Clarity", [0, 0, 0, 0, 0, 0, 1, 3, 4, 5], bass: 0, fidelity: 50, ambience: 0, surround: 0, dynamic: 10),

        // For headphones with their own haptic bass (e.g. Skullcandy Crusher's Sensory Bass): the headset already adds
        // the low end, so this trims the sub-bass slightly and lifts presence and air instead.
        Make("Crusher Sensory", [-2, -1, 0, 0, 0, 0, 1, 2, 2, 1], bass: 0, fidelity: 25, ambience: 0, surround: 10, dynamic: 20),
    ];

    /// <summary>The built-in presets, in menu order (do not modify the instances; <see cref="Apply"/> copies them).</summary>
    public static IReadOnlyList<FxPreset> BuiltIn { get; } = Array.AsReadOnly(BuiltIns);

    /// <summary>True when <paramref name="name"/> is a built-in preset's name (case-insensitive).</summary>
    public static bool IsBuiltIn(string? name) =>
        name is not null && BuiltIns.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset named <paramref name="name"/>: a built-in one first, else the user's; null when none.</summary>
    public static FxPreset? Find(string? name, IEnumerable<FxPreset>? userPresets)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string n = name.Trim();
        return BuiltIns.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
               ?? userPresets?.FirstOrDefault(p => p is not null && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads <paramref name="preset"/> into <paramref name="settings"/>: EQ mode, bands (10 adjustable with their
    /// frequencies, or the 31 third-octave gains) and effect levels; switches the EQ on; the preset becomes current and
    /// unmodified. The power switch (<see cref="AppSettings.FxEnabled"/>) is not touched.
    /// </summary>
    public static void Apply(AppSettings settings, FxPreset preset)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preset);
        FxSettings.EnsureNormalized(settings);
        var p = preset.Clone();
        NormalizePreset(p);

        settings.EqMode = p.EqMode;
        if (p.EqMode == EqMode.ThirtyOneBandThirdOctave)
        {
            settings.EqGains31Db = p.Bands.Select(b => b.GainDb).ToArray();
        }
        else
        {
            settings.EqBands = p.Bands;
        }

        settings.Effects = p.Effects;
        settings.EqEnabled = true;
        settings.FxPresetName = p.Name;
        settings.FxPresetModified = false;
        FxSettings.MirrorLegacyGains(settings);
    }

    /// <summary>A preset holding the current EQ mode, bands and effect levels of <paramref name="settings"/>.</summary>
    public static FxPreset Capture(AppSettings settings, string name)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FxSettings.EnsureNormalized(settings);
        var bands = settings.EqMode == EqMode.ThirtyOneBandThirdOctave
            ? FxSettings.ThirdOctaveFrequenciesHz.Select((f, i) => new EqBand(f, settings.EqGains31Db![i])).ToList()
            : settings.EqBands!.Select(b => b.Clone()).ToList();
        return new FxPreset
        {
            Name = (name ?? "").Trim(),
            EqMode = settings.EqMode,
            Bands = bands,
            Effects = (settings.Effects ?? new EffectLevels()).Clone().Clamp(),
        };
    }

    /// <summary>Null when <paramref name="name"/> can be used for a user preset, else a readable reason.</summary>
    public static string? ValidateName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0)
        {
            return "Type a name for the preset.";
        }

        if (n.Length > MaxNameLength)
        {
            return $"Preset names can be at most {MaxNameLength} characters.";
        }

        if (n.Any(char.IsControl))
        {
            return "Preset names can't contain line breaks or control characters.";
        }

        if (IsBuiltIn(n))
        {
            return $"\"{n}\" is a built-in preset. Choose another name.";
        }

        return null;
    }

    /// <summary>
    /// "Save as…": stores the current settings as the user preset <paramref name="name"/> (replacing one with the same
    /// name, any letter case) and makes it current and unmodified. Returns null on success, else a readable reason
    /// (nothing changes then).
    /// </summary>
    public static string? SaveUserPreset(Library library, string? name)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (ValidateName(name) is { } error)
        {
            return error;
        }

        library.Settings ??= new AppSettings();
        library.UserPresets ??= new List<FxPreset>();
        var preset = Capture(library.Settings, name!);
        int existing = library.UserPresets.FindIndex(p => p is not null && string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            library.UserPresets[existing] = preset;
        }
        else
        {
            library.UserPresets.Add(preset);
        }

        library.Settings.FxPresetName = preset.Name;
        library.Settings.FxPresetModified = false;
        return null;
    }

    /// <summary>
    /// Deletes the user preset <paramref name="name"/>. Built-in presets can't be deleted. When it was the current
    /// preset the settings keep their values and become "Custom" (no preset). Returns true when one was removed.
    /// </summary>
    public static bool DeleteUserPreset(Library library, string? name)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrWhiteSpace(name) || IsBuiltIn(name) || library.UserPresets is null)
        {
            return false;
        }

        string n = name.Trim();
        int removed = library.UserPresets.RemoveAll(p => p is not null && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
        if (removed > 0 && library.Settings is { } s && string.Equals(s.FxPresetName, n, StringComparison.OrdinalIgnoreCase))
        {
            s.FxPresetName = null;
            s.FxPresetModified = false;
        }

        return removed > 0;
    }

    /// <summary>
    /// Repairs a loaded list of user presets in place (never drops a usable one): null entries removed; names trimmed
    /// (an empty name becomes "My preset"; one that clashes with a built-in or an earlier preset gets " (2)", " (3)" …);
    /// bands and effects brought into range. Returns true when anything changed.
    /// </summary>
    public static bool NormalizeUserPresets(List<FxPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        bool changed = presets.RemoveAll(static p => p is null) > 0;
        var used = new HashSet<string>(BuiltIns.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in presets)
        {
            string baseName = string.IsNullOrWhiteSpace(p.Name) ? "My preset" : new string(p.Name.Trim().Where(c => !char.IsControl(c)).ToArray());
            if (baseName.Length > MaxNameLength)
            {
                baseName = baseName[..MaxNameLength].TrimEnd();
            }

            if (baseName.Length == 0)
            {
                baseName = "My preset";
            }

            string unique = baseName;
            for (int n = 2; used.Contains(unique); n++)
            {
                string suffix = $" ({n})";
                unique = (baseName.Length + suffix.Length > MaxNameLength ? baseName[..(MaxNameLength - suffix.Length)] : baseName) + suffix;
            }

            used.Add(unique);
            if (!string.Equals(unique, p.Name, StringComparison.Ordinal))
            {
                p.Name = unique;
                changed = true;
            }

            changed |= NormalizePreset(p);
        }

        return changed;
    }

    /// <summary>Bands and effects of one preset into range (10 strictly increasing bands, or the 31 ISO bands).</summary>
    public static bool NormalizePreset(FxPreset preset)
    {
        bool changed = false;
        if (!Enum.IsDefined(preset.EqMode))
        {
            preset.EqMode = EqMode.TenBandAdjustable;
            changed = true;
        }

        if (preset.Bands is null)
        {
            preset.Bands = new List<EqBand>();
            changed = true;
        }

        if (preset.EqMode == EqMode.ThirtyOneBandThirdOctave)
        {
            preset.Bands.RemoveAll(static b => b is null);
            var fixedBands = new List<EqBand>(FxSettings.ThirdOctaveBandCount);
            for (int i = 0; i < FxSettings.ThirdOctaveBandCount; i++)
            {
                float g = i < preset.Bands.Count ? FxSettings.ClampGain(preset.Bands[i].GainDb) : 0f;
                fixedBands.Add(new EqBand(FxSettings.ThirdOctaveFrequenciesHz[i], g));
            }

            changed |= preset.Bands.Count != fixedBands.Count
                       || preset.Bands.Where((b, i) => b.FrequencyHz != fixedBands[i].FrequencyHz || b.GainDb != fixedBands[i].GainDb).Any();
            preset.Bands = fixedBands;
        }
        else
        {
            changed |= FxSettings.NormalizeTenBands(preset.Bands);
        }

        if (preset.Effects is null)
        {
            preset.Effects = new EffectLevels();
            changed = true;
        }
        else
        {
            var before = preset.Effects.Clone();
            changed |= !preset.Effects.Clamp().SameAs(before);
        }

        return changed;
    }

    private static FxPreset Make(string name, float[] gains, int bass, int fidelity, int ambience, int surround, int dynamic) => new()
    {
        Name = name,
        EqMode = EqMode.TenBandAdjustable,
        Bands = FxSettings.DefaultTenBands(gains),
        Effects = new EffectLevels { Bass = bass, Fidelity = fidelity, Ambience = ambience, Surround = surround, DynamicBoost = dynamic },
    };
}

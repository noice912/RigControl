using System.IO;
using Microsoft.Win32;
using RigControl.App;
using RigControl.Bundled;
using RigControl.Core.Logging;
using RigControl.Core.Settings;

namespace RigControl.App.Tests;

/// <summary>
/// The one yes for the helper programs is not taken from the settings file. These tests use a throwaway key of
/// their own under HKCU (deleted afterwards) in place of the administrators-only key.
/// </summary>
public sealed class AdminConsentStoreTests : IDisposable
{
    private readonly string _key = @"Software\RigControl-Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigcontrol-consent-" + Guid.NewGuid().ToString("N"));
    private readonly AdminConsentStore _admin;
    private readonly SettingsStore _settings;

    public AdminConsentStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _admin = new AdminConsentStore(Registry.CurrentUser, _key);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance);
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false); } catch { /* best effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    [Fact]
    public void Nothing_saved_means_no_yes()
    {
        Assert.False(_admin.Load().AllHelpers);
    }

    [Fact]
    public void The_yes_is_kept_as_one_value_and_can_be_taken_away()
    {
        _admin.Save(new BundledConsent { AllHelpers = true });
        Assert.True(_admin.Load().AllHelpers);
        using (var k = Registry.CurrentUser.OpenSubKey(_key)!)
            Assert.Equal(new[] { AdminConsentStore.AllHelpersValue }, k.GetValueNames());

        _admin.Save(new BundledConsent());
        Assert.False(_admin.Load().AllHelpers);
    }

    [Fact]
    public void Answers_per_program_from_an_earlier_build_do_not_count_as_the_yes_and_are_tidied_away()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key, writable: true))
        {
            k.SetValue("openrgb", 1, RegistryValueKind.DWord);
            k.SetValue("openrgbService", 0, RegistryValueKind.DWord);
            k.SetValue("pawnio", 1, RegistryValueKind.DWord);
            k.SetValue("equalizerapo", 1, RegistryValueKind.DWord);
        }
        Assert.False(_admin.Load().AllHelpers);

        _admin.Save(new BundledConsent { AllHelpers = true });
        using var after = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(new[] { AdminConsentStore.AllHelpersValue }, after.GetValueNames());
    }

    [Fact]
    public void Something_else_than_the_number_one_is_not_a_yes()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_key, writable: true))
            k.SetValue(AdminConsentStore.AllHelpersValue, "1", RegistryValueKind.String);
        Assert.False(_admin.Load().AllHelpers);
    }

    [Fact]
    public void A_yes_written_into_the_settings_file_installs_nothing()
    {
        // What any program the user runs could write.
        var file = Path.Combine(_dir, "written-by-another-program.json");
        File.WriteAllText(file, """{ "bundled": { "consent": { "allHelpers": true, "openRgb": true, "pawnIo": true, "equalizerApo": true } } }""");
        var settings = new SettingsStore(file, NullLog.Instance);

        Assert.False(BundledConsentSource.Load(elevated: true, _admin, settings).AllHelpers);
        Assert.False(BundledConsentSource.Load(elevated: false, _admin, settings).AllHelpers);
    }

    [Fact]
    public void Without_administrator_rights_a_yes_is_saved_nowhere()
    {
        BundledConsentSource.Save(elevated: false, _admin, _settings, new BundledConsent { AllHelpers = true });

        Assert.False(_admin.Load().AllHelpers);
        Assert.False(BundledConsentSource.Load(elevated: true, _admin, _settings).AllHelpers); // asked again by the copy that can install
        var file = Path.Combine(_dir, "settings.json");
        Assert.False(File.Exists(file) && File.ReadAllText(file).Contains("allHelpers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_yes_given_as_administrator_is_also_seen_by_a_copy_without_administrator_rights()
    {
        BundledConsentSource.Save(elevated: true, _admin, _settings, new BundledConsent { AllHelpers = true });

        Assert.True(BundledConsentSource.Load(elevated: true, _admin, _settings).AllHelpers);
        Assert.True(BundledConsentSource.Load(elevated: false, _admin, _settings).AllHelpers);
    }
}

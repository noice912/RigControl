using System.IO;
using RigControl.App.Install;
using RigControl.App.Updates;
using RigControl.Core.Logging;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Updates;

namespace RigControl.App.Tests;

/// <summary>The "updates.channel" setting: test only for a maintainer, and the switch-back flag.</summary>
public sealed class UpdateChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-channel-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _settings;

    public UpdateChannelTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"), NullLog.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private UpdateSettings Saved => _settings.Get<UpdateSettings>(UpdateSettings.Section);

    private static AppVersion V(string s) => AppVersion.Parse(s);

    [Fact]
    public void Public_is_the_default()
    {
        Assert.Equal("public", Saved.Channel);
        Assert.False(Saved.SwitchBackPending);
        Assert.Equal(UpdateChannel.Public, UpdateChannels.Effective(Saved, maintainer: true));
    }

    [Fact]
    public void Only_a_maintainer_may_choose_the_test_channel()
    {
        var ex = Assert.Throws<RpcException>(() => UpdateChannels.Choose(_settings, "test", maintainer: false, V("1.1.2")));
        Assert.Equal(RpcErrorCodes.InvalidArgs, ex.Code);
        Assert.Equal("public", Saved.Channel);

        Assert.True(UpdateChannels.Choose(_settings, "test", maintainer: true, V("1.1.2")));
        Assert.Equal("test", Saved.Channel);
        Assert.Equal(UpdateChannel.Test, UpdateChannels.Effective(Saved, maintainer: true));
    }

    [Fact]
    public void A_file_that_says_test_means_public_for_anyone_else()
    {
        _settings.Update<UpdateSettings>(UpdateSettings.Section, s => s.Channel = "test");
        Assert.Equal(UpdateChannel.Public, UpdateChannels.Effective(Saved, maintainer: false));
        Assert.Equal(UpdateChannel.Test, UpdateChannels.Effective(Saved, maintainer: true));
    }

    [Fact]
    public void Unknown_channels_are_refused()
    {
        Assert.Throws<RpcException>(() => UpdateChannels.Choose(_settings, "beta", maintainer: true, V("1.1.2")));
        Assert.Throws<RpcException>(() => UpdateChannels.Choose(_settings, "Test", maintainer: true, V("1.1.2")));
    }

    [Fact]
    public void Choosing_public_on_a_test_build_allows_the_switch_back_and_test_clears_it()
    {
        UpdateChannels.Choose(_settings, "test", maintainer: true, V("1.1.2"));
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.2")));

        UpdateChannels.Choose(_settings, "public", maintainer: true, V("1.1.3-rc.1"));
        Assert.True(Saved.SwitchBackPending);
        Assert.Equal("1.1.3-rc.1", Saved.SwitchBackFor);
        Assert.True(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3-rc.1")));

        UpdateChannels.Choose(_settings, "test", maintainer: true, V("1.1.3-rc.1"));
        Assert.False(Saved.SwitchBackPending);
        Assert.Null(Saved.SwitchBackFor);
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3-rc.1")));
    }

    [Fact]
    public void Without_a_channel_change_no_downgrade_is_ever_allowed()
    {
        // A public build that never left the public channel.
        UpdateChannels.Choose(_settings, "public", maintainer: false, V("1.1.2"));
        Assert.False(Saved.SwitchBackPending);
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: false, V("1.1.2")));
    }

    [Fact]
    public void The_switch_back_flag_only_counts_for_the_version_it_was_set_on()
    {
        UpdateChannels.Choose(_settings, "public", maintainer: true, V("1.1.3-rc.1"));
        Assert.True(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3-rc.1")));
        // Another version runs now (an update that did not clear the flag): no downgrade.
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3-rc.2")));
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3")));
        // A flag without a version (an older settings file) never counts.
        _settings.Update<UpdateSettings>(UpdateSettings.Section, s => s.SwitchBackFor = null);
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3-rc.1")));
    }

    [Fact]
    public void The_switch_back_flag_only_counts_on_the_public_channel()
    {
        _settings.Update<UpdateSettings>(UpdateSettings.Section, s =>
        {
            s.Channel = "test";
            s.SwitchBackPending = true;
            s.SwitchBackFor = "1.1.3-rc.1";
        });
        Assert.False(UpdateChannels.SwitchBack(Saved, maintainer: true, V("1.1.3-rc.1")));
        // Signed out, the same file means public, and the flag (set by an explicit choice) counts.
        Assert.True(UpdateChannels.SwitchBack(Saved, maintainer: false, V("1.1.3-rc.1")));
    }

    [Theory]
    [InlineData("1.1.3", "1.1.3-rc.1+4b970f9", "1.1.3-rc.1")]
    [InlineData("1.1.3", "1.1.3+4b970f9", "1.1.3")]
    [InlineData("1.1.3", "1.1.4-rc.1", "1.1.3")] // different numbers: the given version wins
    [InlineData("1.1.3", null, "1.1.3")]
    [InlineData(null, "1.1.3-rc.2", "1.1.3-rc.2")]
    [InlineData("garbage", null, "0.0.0")]
    public void The_running_version_keeps_a_test_builds_pre_release_part(string? env, string? informational, string expected)
        => Assert.Equal(expected, RunningVersion.Resolve(env, informational).ToString());

    [Fact]
    public void This_builds_version_text_has_three_numbers_and_maybe_a_pre_release_part()
    {
        Assert.True(AppVersion.TryParse(RunningVersion.Text, out _), RunningVersion.Text);
        Assert.DoesNotContain("+", RunningVersion.Text);
    }

    [Theory]
    [InlineData("1.1.3", "1.1.3-rc.1", true)]
    [InlineData("1.1.3-rc.1", "1.1.3", false)]
    [InlineData("1.1.3-rc.2", "1.1.3-rc.1", true)]
    [InlineData("1.1.3-rc.1", "1.1.3-rc.2", false)]
    public void The_setup_compares_test_build_versions_fully(string installed, string mine, bool expected)
        => Assert.Equal(expected, InstallVersion.InstalledIsSameOrNewer(installed, mine));
}

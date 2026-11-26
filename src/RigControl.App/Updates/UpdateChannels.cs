using System.Reflection;
using RigControl.Core.Rpc;
using RigControl.Core.Settings;
using RigControl.Updates;

namespace RigControl.App.Updates;

/// <summary>
/// The running app's version, pre-release part included. The assembly version (and the exe's file version) is always
/// three numbers; a test build's "-rc.1" lives only in the informational version, so that is read here.
/// </summary>
internal static class RunningVersion
{
    /// <summary>The app's informational version as built ("1.1.3-rc.1+abcdef"), or null.</summary>
    public static string? Informational { get; } =
        typeof(RunningVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>"1.1.2" or "1.1.3-rc.1" (build metadata dropped).</summary>
    public static string Text { get; } = AppVersion.TryParseLoose(Informational, out var v)
        ? v.Value.ToString()
        : typeof(RunningVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>The version updates compare against: the environment's, with this build's pre-release part when it has one.</summary>
    public static AppVersion Resolve(string? environmentVersion) => Resolve(environmentVersion, Informational);

    /// <summary>
    /// The start-up code passes the three-number assembly version. When the informational version is the same three
    /// numbers plus a pre-release part, that is the real version (a test build); anything else keeps the given one.
    /// </summary>
    internal static AppVersion Resolve(string? environmentVersion, string? informational)
    {
        AppVersion.TryParseLoose(informational, out var info);
        if (AppVersion.TryParseLoose(environmentVersion, out var env))
        {
            var e = env.Value;
            return !e.IsPreRelease && info is { IsPreRelease: true } i && i.Core == e ? i : e;
        }
        return info ?? new AppVersion(0, 0, 0);
    }
}

/// <summary>
/// The update channel setting ("updates.channel"): "public" for everyone; "test" (pre-releases too) only while the
/// signed-in GitHub account may push to RigControl. A file that says "test" for anyone else still means public.
/// </summary>
internal static class UpdateChannels
{
    public const string Public = "public";
    public const string Test = "test";

    /// <summary>The channel that is actually used.</summary>
    public static UpdateChannel Effective(UpdateSettings s, bool maintainer)
        => maintainer && string.Equals(s.Channel, Test, StringComparison.Ordinal) ? UpdateChannel.Test : UpdateChannel.Public;

    /// <summary>Whether a check may offer the public release as a downgrade: only on the public channel, right after leaving test.</summary>
    public static bool SwitchBack(UpdateSettings s, bool maintainer)
        => s.SwitchBackPending && Effective(s, maintainer) == UpdateChannel.Public;

    /// <summary>
    /// Saves the user's choice. "test" is refused unless <paramref name="maintainer"/>. Choosing "public" while running a
    /// test build (or coming from the test channel) sets switchBackPending, so the next check may offer the public
    /// version even though it is older; choosing "test" clears it.
    /// </summary>
    /// <returns>True when the channel actually changed.</returns>
    public static bool Choose(SettingsStore settings, string? channel, bool maintainer, AppVersion running)
    {
        var before = settings.Get<UpdateSettings>(UpdateSettings.Section);
        var wasEffective = Effective(before, maintainer);
        switch (channel)
        {
            case Test:
                if (!maintainer)
                    throw RpcException.InvalidArgs("Development builds are only for GitHub accounts that may publish RigControl.");
                settings.Update<UpdateSettings>(UpdateSettings.Section, s =>
                {
                    s.Channel = Test;
                    s.SwitchBackPending = false;
                });
                return wasEffective != UpdateChannel.Test;
            case Public:
                settings.Update<UpdateSettings>(UpdateSettings.Section, s =>
                {
                    var wasTest = string.Equals(s.Channel, Test, StringComparison.Ordinal);
                    s.Channel = Public;
                    if (running.IsPreRelease || wasTest) s.SwitchBackPending = true;
                });
                return wasEffective != UpdateChannel.Public || running.IsPreRelease;
            default:
                throw RpcException.InvalidArgs("The update channel is \"public\" or \"test\".");
        }
    }
}

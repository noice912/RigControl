using RigControl.Nvidia;
using Xunit;

namespace RigControl.Nvidia.Tests;

public class InstalledDriverTests
{
    [Theory]
    [InlineData("32.0.16.2140", "621.40")]
    [InlineData("32.0.16.2124", "621.24")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.3640", "536.40")]
    [InlineData("32.0.16.105", "601.05")] // a fourth part under four digits is padded: 6 + 0105
    public void Windows_driver_versions_become_NVIDIAs(string windows, string nvidia)
        => Assert.Equal(nvidia, InstalledDriver.FromWindowsVersion(windows));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("621.40")]
    [InlineData("32.0.16")]
    [InlineData("32.0.16.21400")]
    [InlineData("32.0.x.2140")]
    public void Anything_else_is_not_a_version(string? text) => Assert.Null(InstalledDriver.FromWindowsVersion(text));
}

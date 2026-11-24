using System.IO;
using System.Security.Cryptography;
using RigControl.Bundled;

namespace RigControl.App.Tests;

public class BundledTests
{
    [Theory]
    [InlineData("\"D:\\Some folder\\OpenRGB.exe\"", "D:\\Some folder\\OpenRGB.exe", "")]
    [InlineData("\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\" --service --x 1", "C:\\Program Files\\OpenRGB\\OpenRGB.exe", "--service --x 1")]
    [InlineData("C:\\Program Files\\GIGABYTE\\Control Center\\uninst.exe", "C:\\Program Files\\GIGABYTE\\Control Center\\uninst.exe", "")]
    [InlineData("MsiExec.exe /X{F489BC39-265B-4B86-9D77-DD7AE4843A63}", "MsiExec.exe", "/X{F489BC39-265B-4B86-9D77-DD7AE4843A63}")]
    public void Commands_split_into_program_and_arguments(string command, string exe, string args)
    {
        Assert.Equal((exe, args), BundledApps.SplitCommand(command));
    }

    [Fact]
    public void Every_carried_file_has_a_recorded_SHA256_that_matches_it()
    {
        // A build without the third_party files carries nothing; then there is nothing to check.
        var asm = typeof(BundledApps).Assembly;
        var apps = new BundledApps(_ => { }, _ => { });
        var resources = asm.GetManifestResourceNames().Where(n => n.StartsWith("bundled/", StringComparison.Ordinal)).ToList();
        var files = new[] { "openrgb", "pawnio", "equalizerapo" }.SelectMany(apps.Files).ToList();
        Assert.Equal(resources.Count, files.Count);
        foreach (var f in files)
        {
            using var s = asm.GetManifestResourceStream("bundled/" + f.Rel)!;
            Assert.Equal(f.Sha256, Convert.ToHexString(SHA256.HashData(s)));
        }
    }

    [Fact]
    public void A_file_only_matches_its_own_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), "rigcontrol-bundled-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "hello");
        try
        {
            Assert.True(BundledApps.Matches(path, "2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824"));
            Assert.False(BundledApps.Matches(path, new string('0', 64)));
            Assert.False(BundledApps.Matches(path + ".missing", new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

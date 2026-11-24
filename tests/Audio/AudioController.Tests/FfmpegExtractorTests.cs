using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using AudioController.App.Hosting;
using AudioController.Decode;
using AudioController.Ffmpeg;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// RigControl: <see cref="FfmpegExtractor"/> with small fake payloads in a temp folder (never
/// %LOCALAPPDATA%\RigControl), plus the search-order change in <see cref="FfmpegLocator"/>.
/// </summary>
public sealed class FfmpegExtractorTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Payload
    {
        public required string Name { get; init; }
        public required byte[] Bytes { get; init; }
        public string? DeclaredHash { get; init; }
        public int Opens;
        public Exception? OpenError { get; init; }

        public EmbeddedFile ToFile() => new(Name, DeclaredHash ?? Sha(Bytes), () =>
        {
            Interlocked.Increment(ref Opens);
            if (OpenError is not null) throw OpenError;
            return new MemoryStream(Bytes, writable: false);
        });
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static (Payload Ffmpeg, Payload Probe) FakeTools() => (
        new Payload { Name = "ffmpeg.exe", Bytes = TestBytes.Deterministic(70_000, seed: 1) },
        new Payload { Name = "ffprobe.exe", Bytes = TestBytes.Deterministic(50_000, seed: 2) });

    [Fact]
    public void FirstUse_ExtractsEveryFile_IntoRootKey_WithItsExactBytes()
    {
        var (ffmpeg, probe) = FakeTools();
        var files = new[] { ffmpeg.ToFile(), probe.ToFile() };

        string? dir = FfmpegExtractor.TryExtract(files, _dir.Path);

        Assert.NotNull(dir);
        Assert.Equal(Path.Combine(_dir.Path, FfmpegExtractor.ComputeKey(files)), dir);
        Assert.Equal(ffmpeg.Bytes, File.ReadAllBytes(Path.Combine(dir!, "ffmpeg.exe")));
        Assert.Equal(probe.Bytes, File.ReadAllBytes(Path.Combine(dir!, "ffprobe.exe")));
        Assert.Empty(Directory.GetFiles(dir!, "*.tmp"));
    }

    [Fact]
    public void Key_IsTheFirst16HexDigitsOfTheManifestSha256_AndChangesWithTheContent()
    {
        var (ffmpeg, probe) = FakeTools();
        var files = new[] { ffmpeg.ToFile(), probe.ToFile() };

        string manifest = $"ffmpeg.exe:{Sha(ffmpeg.Bytes)}\nffprobe.exe:{Sha(probe.Bytes)}\n";
        string expected = Sha(Encoding.UTF8.GetBytes(manifest))[..16];

        Assert.Equal(expected, FfmpegExtractor.ComputeKey(files));
        Assert.Equal(expected, FfmpegExtractor.ComputeKey(files.Reverse().ToArray())); // order does not matter
        Assert.Matches("^[0-9a-f]{16}$", expected);

        var other = new Payload { Name = "ffmpeg.exe", Bytes = TestBytes.Deterministic(70_000, seed: 3) };
        Assert.NotEqual(expected, FfmpegExtractor.ComputeKey(new[] { other.ToFile(), probe.ToFile() }));
    }

    [Fact]
    public void LaterUse_ReusesVerifiedFiles_WithoutRewritingThem()
    {
        var (ffmpeg, probe) = FakeTools();
        var files = new[] { ffmpeg.ToFile(), probe.ToFile() };
        string dir = FfmpegExtractor.TryExtract(files, _dir.Path)!;
        var written = File.GetLastWriteTimeUtc(Path.Combine(dir, "ffmpeg.exe"));

        string? again = FfmpegExtractor.TryExtract(files, _dir.Path);

        Assert.Equal(dir, again);
        Assert.Equal(1, ffmpeg.Opens);
        Assert.Equal(1, probe.Opens);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(dir, "ffmpeg.exe")));
    }

    [Fact]
    public void DamagedCopyOnDisk_IsReplaced()
    {
        var (ffmpeg, probe) = FakeTools();
        var files = new[] { ffmpeg.ToFile(), probe.ToFile() };
        string dir = FfmpegExtractor.TryExtract(files, _dir.Path)!;
        File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), new byte[] { 1, 2, 3 }); // truncated / tampered

        Assert.Equal(dir, FfmpegExtractor.TryExtract(files, _dir.Path));

        Assert.Equal(ffmpeg.Bytes, File.ReadAllBytes(Path.Combine(dir, "ffmpeg.exe")));
        Assert.Equal(2, ffmpeg.Opens);
        Assert.Equal(1, probe.Opens); // the intact file is left alone
    }

    [Fact]
    public void PayloadNotMatchingItsHash_IsNotUsed_AndLeavesNothingBehind()
    {
        var (ffmpeg, probe) = FakeTools();
        var bad = new Payload { Name = "ffmpeg.exe", Bytes = ffmpeg.Bytes, DeclaredHash = Sha(new byte[] { 42 }) };

        string? dir = FfmpegExtractor.TryExtract(new[] { bad.ToFile(), probe.ToFile() }, _dir.Path);

        Assert.Null(dir);
        Assert.Empty(Directory.GetFiles(_dir.Path, "ffmpeg.exe", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void UnreadablePayload_ReturnsNull_NeverThrows()
    {
        var (ffmpeg, _) = FakeTools();
        var broken = new Payload { Name = "ffprobe.exe", Bytes = Array.Empty<byte>(), DeclaredHash = Sha(Array.Empty<byte>()), OpenError = new IOException("resource gone") };

        Assert.Null(FfmpegExtractor.TryExtract(new[] { ffmpeg.ToFile(), broken.ToFile() }, _dir.Path));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(@"..\ffmpeg.exe")]
    [InlineData(@"sub\ffmpeg.exe")]
    [InlineData("sub/ffmpeg.exe")]
    [InlineData("..")]
    [InlineData("")]
    public void FileNamesThatAreNotPlain_AreRefused_AndNothingIsWritten(string name)
    {
        var payload = new Payload { Name = name, Bytes = new byte[] { 1, 2, 3 } };

        Assert.Null(FfmpegExtractor.TryExtract(new[] { payload.ToFile() }, _dir.Path));
        Assert.Equal(0, payload.Opens);
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(_dir.Path)!, "ffmpeg.exe"));
    }

    [Fact]
    public void NothingToExtract_OrARelativeRoot_ReturnsNull()
    {
        var (ffmpeg, _) = FakeTools();

        Assert.Null(FfmpegExtractor.TryExtract(Array.Empty<EmbeddedFile>(), _dir.Path));
        Assert.Null(FfmpegExtractor.TryExtract(new[] { ffmpeg.ToFile() }, "relative\\ffmpeg"));
        Assert.Null(FfmpegExtractor.TryExtract(new[] { ffmpeg.ToFile() }, ""));
        Assert.Equal(0, ffmpeg.Opens);
    }

    [Fact]
    public void AfterExtracting_OlderPayloadFolders_AreRemoved_OtherFoldersKept()
    {
        var (ffmpeg, probe) = FakeTools();
        string old = Path.Combine(_dir.Path, "0123456789abcdef");
        string unrelated = Path.Combine(_dir.Path, "keep-me");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "ffmpeg.exe"), "old build");
        Directory.CreateDirectory(unrelated);

        string dir = FfmpegExtractor.TryExtract(new[] { ffmpeg.ToFile(), probe.ToFile() }, _dir.Path)!;

        Assert.True(Directory.Exists(dir));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task ConcurrentFirstUse_AllGetTheSameVerifiedFolder()
    {
        var (ffmpeg, probe) = FakeTools();
        var files = new[] { ffmpeg.ToFile(), probe.ToFile() };

        string?[] results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => FfmpegExtractor.TryExtract(files, _dir.Path))));

        string expected = Path.Combine(_dir.Path, FfmpegExtractor.ComputeKey(files));
        foreach (string? r in results)
        {
            // A racing writer may lose the final move (file in use) and fall back; it must never return a bad folder.
            if (r is not null) Assert.Equal(expected, r);
        }

        Assert.Contains(results, r => r is not null);
        Assert.Equal(ffmpeg.Bytes, File.ReadAllBytes(Path.Combine(expected, "ffmpeg.exe")));
        Assert.Equal(probe.Bytes, File.ReadAllBytes(Path.Combine(expected, "ffprobe.exe")));
    }

    [Fact]
    public void FromAssembly_WithoutMetadata_IsEmpty()
    {
        Assert.Empty(FfmpegExtractor.FromAssembly(typeof(FfmpegExtractor).Assembly));
    }

    [Fact]
    public void EmbeddedPayload_CarriesExactlyTheToolsTheDecoderRuns_WithTheirRealHashes()
    {
        var files = FfmpegExtractor.FromAssembly(EmbeddedFfmpeg.Assembly);
        var names = files.Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(FfmpegLocator.FfmpegExeName, names);
        Assert.Contains(FfmpegLocator.FfprobeExeName, names);
        Assert.Equal(2, files.Count(f => f.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        Assert.All(files, f => Assert.Matches("^[0-9a-fA-F]{64}$", f.Sha256Hex));

        // The test project copies the same third_party binaries next to itself; the build-time hashes must match them.
        foreach (string exe in new[] { FfmpegLocator.FfmpegExeName, FfmpegLocator.FfprobeExeName })
        {
            string copy = Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe);
            if (!File.Exists(copy)) continue;
            using var stream = File.OpenRead(copy);
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(actual, files.Single(f => f.FileName == exe).Sha256Hex.ToLowerInvariant());
        }
    }

    [Fact]
    public void HostAssembly_CarriesTheSameNamesAndHashes_AsThePayloadAssembly()
    {
        var fromHost = AudioHostOptions.EmbeddedFfmpegFiles();
        var fromPayload = FfmpegExtractor.FromAssembly(EmbeddedFfmpeg.Assembly);

        Assert.NotEmpty(fromHost);
        Assert.Equal(
            fromPayload.Select(f => (f.FileName, f.Sha256Hex)).OrderBy(f => f.FileName, StringComparer.Ordinal),
            fromHost.Select(f => (f.FileName, f.Sha256Hex)).OrderBy(f => f.FileName, StringComparer.Ordinal));
        Assert.Equal(FfmpegExtractor.ComputeKey(fromPayload), FfmpegExtractor.ComputeKey(fromHost)); // same folder
    }

    /// <summary>An in-memory assembly that only carries <c>ffmpeg-sha256:</c> metadata, like AudioController.dll does.</summary>
    private static Assembly ManifestAssembly(params Payload[] payloads)
    {
        var ctor = typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
        var attributes = payloads
            .Select(p => new CustomAttributeBuilder(ctor, new object[] { FfmpegExtractor.MetadataPrefix + p.Name, Sha(p.Bytes) }))
            .ToArray();
        return AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("FakeFfmpegManifest." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run, attributes);
    }

    [Fact]
    public void FromManifest_AnIntactCopyOnDisk_IsReused_WithoutEverLoadingThePayloadAssembly()
    {
        var (ffmpeg, probe) = FakeTools();
        int payloadLoads = 0;
        var files = FfmpegExtractor.FromManifest(ManifestAssembly(ffmpeg, probe), () =>
        {
            Interlocked.Increment(ref payloadLoads);
            throw new InvalidOperationException("The payload assembly must not be needed.");
        });
        Assert.Equal(new[] { "ffmpeg.exe", "ffprobe.exe" }, files.Select(f => f.FileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(Sha(ffmpeg.Bytes), files.Single(f => f.FileName == "ffmpeg.exe").Sha256Hex);

        // What an earlier run left behind.
        string dir = _dir.Combine(FfmpegExtractor.ComputeKey(files));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), ffmpeg.Bytes);
        File.WriteAllBytes(Path.Combine(dir, "ffprobe.exe"), probe.Bytes);

        Assert.Equal(dir, FfmpegExtractor.TryExtract(files, _dir.Path));
        Assert.Equal(0, payloadLoads);
    }

    [Fact]
    public void FromManifest_ADamagedCopy_AsksForThePayload_AndAnUnavailablePayloadFallsBack()
    {
        var (ffmpeg, probe) = FakeTools();
        int payloadLoads = 0;
        var files = FfmpegExtractor.FromManifest(ManifestAssembly(ffmpeg, probe), () =>
        {
            Interlocked.Increment(ref payloadLoads);
            throw new FileNotFoundException("AudioController.Ffmpeg is missing.");
        });

        string dir = _dir.Combine(FfmpegExtractor.ComputeKey(files));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), new byte[] { 1, 2, 3 }); // damaged
        File.WriteAllBytes(Path.Combine(dir, "ffprobe.exe"), probe.Bytes);

        Assert.Null(FfmpegExtractor.TryExtract(files, _dir.Path)); // the locator then tries the original locations
        Assert.True(payloadLoads >= 1);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void FromManifest_AnAssemblyWithoutMetadata_IsEmpty()
    {
        Assert.Empty(FfmpegExtractor.FromManifest(typeof(FfmpegExtractor).Assembly, () => typeof(FfmpegExtractor).Assembly));
    }

    /// <summary>
    /// Opt-in (it writes the real ~200 MB payload to a temp folder, never to %LOCALAPPDATA%\RigControl): set
    /// RIGCONTROL_TEST_REAL_FFMPEG_EXTRACT=1. Exactly what AudioHost does on a first run and on the run after it.
    /// </summary>
    [Fact]
    public void RealEmbeddedPayload_ExtractsVerifiesAndIsReused_WhenOptedIn()
    {
        if (Environment.GetEnvironmentVariable("RIGCONTROL_TEST_REAL_FFMPEG_EXTRACT") != "1") return;

        var files = AudioHostOptions.EmbeddedFfmpegFiles();
        string? dir = FfmpegExtractor.TryExtract(files, _dir.Path);

        Assert.NotNull(dir);
        Assert.Equal(Path.Combine(_dir.Path, FfmpegExtractor.ComputeKey(files)), dir);
        foreach (var file in files)
        {
            using var stream = File.OpenRead(Path.Combine(dir!, file.FileName));
            Assert.Equal(file.Sha256Hex.ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        var candidates = FfmpegLocator.GetCandidates(FfmpegLocator.FfmpegExeName, dir);
        Assert.True(File.Exists(candidates[0]));
        Assert.Empty(Directory.GetFiles(dir!, "*.tmp"));

        // The next start: nothing is read from the exe again.
        int payloadLoads = 0;
        var again = FfmpegExtractor.FromManifest(typeof(AudioHost).Assembly, () =>
        {
            Interlocked.Increment(ref payloadLoads);
            return EmbeddedFfmpeg.Assembly;
        });
        Assert.Equal(dir, FfmpegExtractor.TryExtract(again, _dir.Path));
        Assert.Equal(0, payloadLoads);
    }

    [Fact]
    public void Locator_TriesTheExtractedFolderFirst_ThenEveryOriginalLocation()
    {
        string extracted = _dir.Combine("0123456789abcdef");
        var regular = FfmpegLocator.GetCandidates("ffmpeg.exe", null);
        var withEmbedded = FfmpegLocator.GetCandidates("ffmpeg.exe", extracted);

        Assert.Equal(Path.Combine(extracted, "ffmpeg.exe"), withEmbedded[0]);
        Assert.Equal(regular, withEmbedded.Skip(1).ToList());
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), regular[0]); // published layout next

        // A relative or empty folder is never used (no current-directory planting).
        Assert.Equal(regular, FfmpegLocator.GetCandidates("ffmpeg.exe", "relative"));
        Assert.Equal(regular, FfmpegLocator.GetCandidates("ffmpeg.exe", " "));
    }
}

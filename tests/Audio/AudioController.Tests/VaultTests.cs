using System.Security.Cryptography;
using System.Text;
using AudioController.Core.Equalizer;
using AudioController.Core.Models;
using AudioController.Storage;
using Xunit;

namespace AudioController.Tests;

/// <summary>
/// SPEC 4.4 / 7 "Vault". Every test runs against <c>new Vault(tempDir, randomKey)</c> in its own fresh
/// temp directory (xUnit creates a new instance of this class per test). DPAPI and %LOCALAPPDATA% are
/// never touched.
/// </summary>
public sealed class VaultTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly byte[] _key = TestBytes.RandomKey();

    public void Dispose() => _temp.Dispose();

    private string VaultRoot => _temp.Combine("vault");

    private string LibraryPath => Path.Combine(VaultRoot, "library.dat");

    private string SoundPath(Guid id) => Path.Combine(VaultRoot, "sounds", id.ToString("D") + ".enc");

    private Vault OpenVault() => new(VaultRoot, _key);

    // ---------------------------------------------------------------- construction / empty vault

    [Fact]
    public void Constructor_NewDirectory_CreatesRootAndSoundsFolderWithoutLibraryFile()
    {
        var vault = OpenVault();

        Assert.Equal(Path.GetFullPath(VaultRoot), vault.RootDir);
        Assert.True(Directory.Exists(VaultRoot));
        Assert.True(Directory.Exists(Path.Combine(VaultRoot, "sounds")));
        Assert.False(File.Exists(LibraryPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(64)]
    public void Constructor_KeyNot32Bytes_ThrowsArgumentException(int keyLength)
    {
        Assert.Throws<ArgumentException>(() => new Vault(VaultRoot, new byte[keyLength]));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.ThrowsAny<ArgumentException>(() => new Vault(null!, _key));
        Assert.ThrowsAny<ArgumentException>(() => new Vault("   ", _key));
        Assert.Throws<ArgumentNullException>(() => new Vault(VaultRoot, null!));
    }

    [Fact]
    public void Constructor_StaleTmpFilesFromInterruptedWrites_AreDeletedAndLiveFilesKept()
    {
        var vault = OpenVault();
        vault.SaveLibrary(BuildSampleLibrary());
        var id = vault.AddSound(TestBytes.Deterministic(300));
        // A crash mid-AddSound / mid-SaveLibrary leaves these behind; nothing else would ever remove the sound one.
        var orphanSoundTmp = Path.Combine(VaultRoot, "sounds", Guid.NewGuid().ToString("D") + ".enc.tmp");
        File.WriteAllBytes(orphanSoundTmp, TestBytes.Deterministic(64));
        File.WriteAllBytes(LibraryPath + ".tmp", TestBytes.Deterministic(64));

        var reopened = OpenVault();

        Assert.Empty(Directory.GetFiles(VaultRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(TestBytes.Deterministic(300), reopened.GetSound(id));
        Assert.Equal("Memes", reopened.LoadLibrary().Boards[0].Name);
    }

    [Fact]
    public void LoadLibrary_EmptyDirectory_ReturnsFreshUsableLibrary()
    {
        var vault = OpenVault();

        var library = vault.LoadLibrary();

        Assert.NotNull(library);
        Assert.Equal(1, library.SchemaVersion);
        Assert.NotNull(library.Boards);
        Assert.Empty(library.Boards);
        Assert.NotNull(library.Settings);
        Assert.Equal(10, library.Settings.EqGainsDb.Length);
        Assert.False(File.Exists(LibraryPath)); // loading never creates the file

        // "Usable": it can be modified, saved and read back.
        library.Boards.Add(new Board { Name = "First" });
        vault.SaveLibrary(library);
        var reloaded = OpenVault().LoadLibrary();
        Assert.Equal("First", Assert.Single(reloaded.Boards).Name);
    }

    [Fact]
    public void SaveLibrary_EmptyLibrary_RoundTrips()
    {
        var vault = OpenVault();

        vault.SaveLibrary(new Library());
        var loaded = OpenVault().LoadLibrary();

        Assert.Empty(loaded.Boards);
        Assert.Equal(1, loaded.SchemaVersion);
        // A fresh library comes back normalized: the Effects fields filled with their defaults.
        var expected = new AppSettings();
        FxSettings.Normalize(expected);
        AssertSettingsEqual(expected, loaded.Settings);
        Assert.Empty(loaded.UserPresets);
    }

    [Fact]
    public void LoadLibrary_FromBeforeTheEffectsPanel_MigratesEqGainsDbIntoBands()
    {
        // A 1.4 library: only EqGainsDb (EqBands, EqGains31Db, Effects, UserPresets absent → null in JSON).
        var legacy = new[] { -12f, -6.5f, -1.25f, 0f, 0.1f, 3f, 6f, 9.75f, 11.5f, 12f };
        var library = new Library { Settings = new AppSettings { EqEnabled = true, EqGainsDb = (float[])legacy.Clone(), Effects = null! }, UserPresets = null! };

        OpenVault().SaveLibrary(library);
        var loaded = OpenVault().LoadLibrary();

        var s = loaded.Settings;
        Assert.Equal(FxSettings.DefaultTenBandFrequenciesHz, s.EqBands!.Select(b => b.FrequencyHz));
        Assert.Equal(legacy, s.EqBands!.Select(b => b.GainDb));
        Assert.Equal(legacy, s.EqGainsDb);
        Assert.True(s.EqEnabled);
        Assert.True(s.FxEnabled);
        Assert.Equal(EqMode.TenBandAdjustable, s.EqMode);
        Assert.Equal(31, s.EqGains31Db!.Length);
        Assert.True(s.Effects.IsZero);
        Assert.NotNull(loaded.UserPresets);
    }

    [Fact]
    public void LoadLibrary_NeverResetsEffectsDataWhenTheLegacyArrayIsMissing()
    {
        var bands = FxSettings.DefaultTenBands(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        bands[4].FrequencyHz = 470;
        var library = new Library { Settings = new AppSettings { EqBands = bands, EqGainsDb = null! } };

        OpenVault().SaveLibrary(library);
        var s = OpenVault().LoadLibrary().Settings;

        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f }, s.EqBands!.Select(b => b.GainDb));
        Assert.Equal(470, s.EqBands![4].FrequencyHz);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f }, s.EqGainsDb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveThenLoad_EqAllAudio_KeepsNeverChosenApartFromOff(bool? eqAll)
    {
        var library = new Library { Settings = new AppSettings { EqAllAudio = eqAll, EqAutoPreamp = eqAll != true } };

        OpenVault().SaveLibrary(library);
        var loaded = OpenVault().LoadLibrary().Settings;

        Assert.Equal(eqAll, loaded.EqAllAudio);
        Assert.Equal(eqAll != true, loaded.EqAutoPreamp);
    }

    [Fact]
    public void LoadLibrary_WithoutMirrorFields_IsOffWithNoTargets()
    {
        var library = new Library { Settings = new AppSettings { MirrorTargets = null! } }; // as a 1.5 library: no such fields

        OpenVault().SaveLibrary(library);
        var s = OpenVault().LoadLibrary().Settings;

        Assert.False(s.MirrorEnabled);
        Assert.NotNull(s.MirrorTargets);
        Assert.Empty(s.MirrorTargets);
    }

    [Fact]
    public void LoadLibrary_BrokenMirrorTargets_AreRepaired_ValidOnesKept()
    {
        var digital = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}";
        var library = new Library
        {
            Settings = new AppSettings
            {
                MirrorEnabled = true,
                MirrorTargets = new List<MirrorTarget>
                {
                    new() { DeviceId = digital, DeviceName = "Digital Output", DelayMs = 9999, VolumePercent = 400 },
                    null!,
                    new() { DeviceId = "  " },
                    new() { DeviceId = digital.ToUpperInvariant(), DelayMs = 1 },
                },
            },
        };

        OpenVault().SaveLibrary(library);
        var s = OpenVault().LoadLibrary().Settings;

        Assert.True(s.MirrorEnabled); // a stored choice is never flipped
        var t = Assert.Single(s.MirrorTargets);
        Assert.Equal((digital, "Digital Output", true, 500, 100), (t.DeviceId, t.DeviceName, t.Enabled, t.DelayMs, t.VolumePercent));
    }

    // ---------------------------------------------------------------- create / read / update / delete

    [Fact]
    public void SaveThenLoad_FullLibrary_RoundTripsBoardsSlotsAndSettingsExactly()
    {
        var original = BuildSampleLibrary();

        OpenVault().SaveLibrary(original);
        // A brand-new Vault instance on the same folder simulates an app restart.
        var loaded = OpenVault().LoadLibrary();

        AssertLibrariesEqual(original, loaded);
    }

    [Fact]
    public void SaveThenLoad_DefaultWindowPositionNaN_SurvivesRoundTrip()
    {
        var library = new Library();
        Assert.True(double.IsNaN(library.Settings.WindowLeft));

        OpenVault().SaveLibrary(library);
        var loaded = OpenVault().LoadLibrary();

        Assert.True(double.IsNaN(loaded.Settings.WindowLeft));
        Assert.True(double.IsNaN(loaded.Settings.WindowTop));
        Assert.Equal(1280, loaded.Settings.WindowWidth);
        Assert.Equal(760, loaded.Settings.WindowHeight);
    }

    [Fact]
    public void SaveThenLoad_ManyBoardsAndSlots_AllSurvive()
    {
        var library = new Library();
        for (int b = 0; b < 25; b++)
        {
            var board = new Board { Name = $"Board {b}" };
            for (int s = 0; s < 40; s++)
            {
                board.Slots.Add(new SoundSlot
                {
                    Name = $"Sound {b}.{s}",
                    Volume = s / 20f,
                    PlayMode = (PlayMode)(s % 3),
                    SoundId = Guid.NewGuid(),
                });
            }

            library.Boards.Add(board);
        }

        OpenVault().SaveLibrary(library);
        var loaded = OpenVault().LoadLibrary();

        AssertLibrariesEqual(library, loaded);
    }

    [Fact]
    public void UpdateBoardsAndSlots_SaveThenReload_ChangesSurvive()
    {
        var vault = OpenVault();
        vault.SaveLibrary(BuildSampleLibrary());

        var library = vault.LoadLibrary();
        var board = library.Boards[0];
        board.Name = "Renamed board";
        var slot = board.Slots[0];
        slot.Name = "Renamed slot";
        slot.Volume = 0.25f;
        slot.Hotkey = "Ctrl+Shift+F9";
        slot.PlayMode = PlayMode.HoldToPlay;
        slot.ColorHex = "#000000";
        board.Slots.Add(new SoundSlot { Name = "Added later", Volume = 1.75f, PlayMode = PlayMode.Toggle });
        library.Boards.Add(new Board { Name = "Brand new board" });
        library.Settings.EqGainsDb[5] = -3.5f;
        library.Settings.EqEnabled = false;
        library.Settings.StopAllHotkey = "Ctrl+Alt+Pause";
        vault.SaveLibrary(library);

        var reloaded = OpenVault().LoadLibrary();

        AssertLibrariesEqual(library, reloaded);
        Assert.Equal("Renamed board", reloaded.Boards[0].Name);
        Assert.Equal("Renamed slot", reloaded.Boards[0].Slots[0].Name);
        Assert.Equal(0.25f, reloaded.Boards[0].Slots[0].Volume);
        Assert.Equal("Ctrl+Shift+F9", reloaded.Boards[0].Slots[0].Hotkey);
        Assert.Equal(PlayMode.HoldToPlay, reloaded.Boards[0].Slots[0].PlayMode);
        Assert.Equal("Added later", reloaded.Boards[0].Slots[^1].Name);
        Assert.Equal("Brand new board", reloaded.Boards[^1].Name);
        Assert.Equal(-3.5f, reloaded.Settings.EqGainsDb[5]);
        Assert.Equal(-3.5f, reloaded.Settings.EqBands![5].GainDb); // the 1.4 array edit is adopted…
        Assert.Equal(1100, reloaded.Settings.EqBands![5].FrequencyHz); // …on the band's own frequency
        Assert.Equal("Ctrl+Alt+Pause", reloaded.Settings.StopAllHotkey);
    }

    [Fact]
    public void DeleteBoardsAndSlots_SaveThenReload_DeletionsSurvive()
    {
        var vault = OpenVault();
        var original = BuildSampleLibrary();
        vault.SaveLibrary(original);

        var library = vault.LoadLibrary();
        var removedBoardId = library.Boards[1].Id;
        var removedSlotId = library.Boards[0].Slots[1].Id;
        library.Boards.RemoveAt(1);
        library.Boards[0].Slots.RemoveAt(1);
        vault.SaveLibrary(library);

        var reloaded = OpenVault().LoadLibrary();

        Assert.Equal(original.Boards.Count - 1, reloaded.Boards.Count);
        Assert.DoesNotContain(reloaded.Boards, b => b.Id == removedBoardId);
        Assert.DoesNotContain(reloaded.Boards.SelectMany(b => b.Slots), s => s.Id == removedSlotId);
        Assert.Equal(original.Boards[0].Slots.Count - 1, reloaded.Boards[0].Slots.Count);
        AssertLibrariesEqual(library, reloaded);
    }

    [Fact]
    public void DeleteAllBoards_SaveThenReload_LibraryIsEmpty()
    {
        var vault = OpenVault();
        vault.SaveLibrary(BuildSampleLibrary());

        var library = vault.LoadLibrary();
        library.Boards.Clear();
        vault.SaveLibrary(library);

        Assert.Empty(OpenVault().LoadLibrary().Boards);
    }

    [Fact]
    public void SaveLibrary_Null_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => OpenVault().SaveLibrary(null!));
    }

    // ---------------------------------------------------------------- sounds

    [Fact]
    public void AddSoundThenGetSound_WavBytes_RoundTripExactly()
    {
        var vault = OpenVault();
        var wav = TestWav.CanonicalWavBytes(frames: 4800);

        var id = vault.AddSound(wav);

        Assert.NotEqual(Guid.Empty, id);
        Assert.True(vault.SoundExists(id));
        Assert.True(File.Exists(SoundPath(id)));
        Assert.Equal(wav, vault.GetSound(id));
        Assert.Equal(wav, OpenVault().GetSound(id)); // survives "restart"
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    public void AddSoundThenGetSound_ArbitraryBytes_RoundTripExactly(int size)
    {
        var vault = OpenVault();
        var data = TestBytes.Deterministic(size, seed: size);

        var id = vault.AddSound(data);

        Assert.Equal(data, vault.GetSound(id));
    }

    [Fact]
    public void AddSound_TwoSounds_GetDistinctIdsAndContents()
    {
        var vault = OpenVault();
        var a = TestBytes.Deterministic(1000, seed: 1);
        var b = TestBytes.Deterministic(1000, seed: 2);

        var idA = vault.AddSound(a);
        var idB = vault.AddSound(b);

        Assert.NotEqual(idA, idB);
        Assert.Equal(a, vault.GetSound(idA));
        Assert.Equal(b, vault.GetSound(idB));
    }

    [Fact]
    public void AddSound_EmptyData_ThrowsArgumentException()
    {
        var vault = OpenVault();

        Assert.Throws<ArgumentException>(() => vault.AddSound(Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => vault.AddSound(null!));
    }

    [Fact]
    public void SoundExists_UnknownId_ReturnsFalse()
    {
        Assert.False(OpenVault().SoundExists(Guid.NewGuid()));
    }

    [Fact]
    public void GetSound_MissingId_ThrowsFileNotFoundException()
    {
        var vault = OpenVault();

        Assert.Throws<FileNotFoundException>(() => vault.GetSound(Guid.NewGuid()));
    }

    [Fact]
    public void DeleteSound_ExistingSound_RemovesFileAndFurtherReadsThrow()
    {
        var vault = OpenVault();
        var keep = vault.AddSound(TestBytes.Deterministic(100, seed: 7));
        var id = vault.AddSound(TestBytes.Deterministic(100, seed: 8));

        vault.DeleteSound(id);

        Assert.False(vault.SoundExists(id));
        Assert.False(File.Exists(SoundPath(id)));
        Assert.Throws<FileNotFoundException>(() => vault.GetSound(id));
        Assert.True(vault.SoundExists(keep)); // other sounds untouched
        Assert.Equal(TestBytes.Deterministic(100, seed: 7), vault.GetSound(keep));
    }

    [Fact]
    public void DeleteSound_UnknownId_IsNoOp()
    {
        var vault = OpenVault();

        var ex = Record.Exception(() => vault.DeleteSound(Guid.NewGuid()));

        Assert.Null(ex);
    }

    // ---------------------------------------------------------------- tamper / wrong key: throw, never partial data

    [Theory]
    [InlineData(4)]    // nonce
    [InlineData(20)]   // tag
    [InlineData(32)]   // first ciphertext byte
    [InlineData(-1)]   // last ciphertext byte
    public void LoadLibrary_TamperedLibraryFile_ThrowsCryptographicException(int index)
    {
        OpenVault().SaveLibrary(BuildSampleLibrary());
        var bytes = File.ReadAllBytes(LibraryPath);
        bytes[index >= 0 ? index : bytes.Length + index] ^= 0x01;
        File.WriteAllBytes(LibraryPath, bytes);

        var vault = OpenVault();

        Assert.ThrowsAny<CryptographicException>(() => vault.LoadLibrary());
    }

    [Fact]
    public void LoadLibrary_TamperedLibraryFile_ErrorNamesTheFileAndKeepsTheCause()
    {
        OpenVault().SaveLibrary(BuildSampleLibrary());
        var bytes = File.ReadAllBytes(LibraryPath);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(LibraryPath, bytes);

        var ex = Assert.Throws<CryptographicException>(() => OpenVault().LoadLibrary());

        Assert.Contains(Path.GetFullPath(LibraryPath), ex.Message);
        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void LoadLibrary_LibraryFileWithBadMagic_ThrowsInvalidDataException()
    {
        OpenVault().SaveLibrary(BuildSampleLibrary());
        var bytes = File.ReadAllBytes(LibraryPath);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(LibraryPath, bytes);

        Assert.Throws<InvalidDataException>(() => OpenVault().LoadLibrary());
    }

    [Fact]
    public void LoadLibrary_TruncatedLibraryFile_Throws()
    {
        OpenVault().SaveLibrary(BuildSampleLibrary());
        var bytes = File.ReadAllBytes(LibraryPath);
        File.WriteAllBytes(LibraryPath, bytes[..(bytes.Length / 2)]);

        Assert.ThrowsAny<CryptographicException>(() => OpenVault().LoadLibrary());

        File.WriteAllBytes(LibraryPath, bytes[..10]);
        Assert.Throws<InvalidDataException>(() => OpenVault().LoadLibrary());

        File.WriteAllBytes(LibraryPath, Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => OpenVault().LoadLibrary());
    }

    [Fact]
    public void LoadLibrary_PlaintextJsonPlantedAsLibraryFile_IsRejected()
    {
        // An attacker (or a buggy tool) writing unencrypted JSON must not be accepted as a library.
        var vault = OpenVault();
        File.WriteAllText(LibraryPath,"{\"SchemaVersion\":1,\"Boards\":[{\"Name\":\"Injected\"}]}");

        Assert.Throws<InvalidDataException>(() => vault.LoadLibrary());
    }

    [Fact]
    public void LoadLibrary_WrongKey_ThrowsCryptographicException()
    {
        OpenVault().SaveLibrary(BuildSampleLibrary());

        var otherVault = new Vault(VaultRoot, TestBytes.RandomKey());

        Assert.ThrowsAny<CryptographicException>(() => otherVault.LoadLibrary());
    }

    [Fact]
    public void LoadLibrary_AfterFailedTamperedLoad_OriginalFileIsNotModified()
    {
        OpenVault().SaveLibrary(BuildSampleLibrary());
        var bytes = File.ReadAllBytes(LibraryPath);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(LibraryPath, bytes);

        Assert.ThrowsAny<CryptographicException>(() => OpenVault().LoadLibrary());

        // A failed load must not "repair" (overwrite) the file with defaults.
        Assert.Equal(bytes, File.ReadAllBytes(LibraryPath));
    }

    [Theory]
    [InlineData(4)]    // nonce
    [InlineData(16)]   // tag
    [InlineData(40)]   // ciphertext (inside the RIFF header)
    [InlineData(-1)]   // last ciphertext byte
    public void GetSound_TamperedEncFile_ThrowsCryptographicException(int index)
    {
        var vault = OpenVault();
        var id = vault.AddSound(TestWav.CanonicalWavBytes(frames: 2400));
        var path = SoundPath(id);
        var bytes = File.ReadAllBytes(path);
        bytes[index >= 0 ? index : bytes.Length + index] ^= 0x04;
        File.WriteAllBytes(path, bytes);

        Assert.ThrowsAny<CryptographicException>(() => vault.GetSound(id));
    }

    [Fact]
    public void GetSound_WrongKey_ErrorNamesTheFileAndKeepsTheCause()
    {
        var id = OpenVault().AddSound(TestBytes.Deterministic(100));

        var ex = Assert.Throws<CryptographicException>(() => new Vault(VaultRoot, TestBytes.RandomKey()).GetSound(id));

        Assert.Contains(Path.GetFullPath(SoundPath(id)), ex.Message);
        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void GetSound_EncFileWithBadMagicOrTruncated_ThrowsInvalidDataException()
    {
        var vault = OpenVault();
        var id = vault.AddSound(TestWav.CanonicalWavBytes(frames: 2400));
        var path = SoundPath(id);
        var bytes = File.ReadAllBytes(path);

        File.WriteAllBytes(path, bytes[..20]);
        Assert.Throws<InvalidDataException>(() => vault.GetSound(id));

        bytes[2] = 0;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => vault.GetSound(id));
    }

    [Fact]
    public void GetSound_WrongKey_ThrowsCryptographicException()
    {
        var id = OpenVault().AddSound(TestWav.CanonicalWavBytes(frames: 480));

        var otherVault = new Vault(VaultRoot, TestBytes.RandomKey());

        Assert.ThrowsAny<CryptographicException>(() => otherVault.GetSound(id));
    }

    [Fact]
    public void GetSound_EncFileSwappedWithAnotherValidEncFile_ReturnsThatFilesContentNotPartialData()
    {
        // Sanity check of the "one .enc per sound" layout: each file is self-contained and authenticated.
        var vault = OpenVault();
        var a = TestBytes.Deterministic(500, seed: 1);
        var b = TestBytes.Deterministic(500, seed: 2);
        var idA = vault.AddSound(a);
        var idB = vault.AddSound(b);

        File.Copy(SoundPath(idB), SoundPath(idA), overwrite: true);

        Assert.Equal(b, vault.GetSound(idA));
    }

    // ---------------------------------------------------------------- atomic writes

    [Fact]
    public void SaveLibrary_AfterSave_NoTmpFileRemains()
    {
        var vault = OpenVault();
        vault.SaveLibrary(BuildSampleLibrary());
        vault.AddSound(TestBytes.Deterministic(1000));
        vault.SaveLibrary(BuildSampleLibrary());

        Assert.Empty(Directory.GetFiles(VaultRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.True(File.Exists(LibraryPath));
    }

    [Fact]
    public void LoadLibrary_StaleGarbageTmpFromInterruptedWrite_StillReturnsLastGoodLibrary()
    {
        var good = BuildSampleLibrary();
        OpenVault().SaveLibrary(good);
        // Simulated crash part-way through a later save: the .tmp holds random junk and the rename never happened.
        File.WriteAllBytes(LibraryPath + ".tmp", TestBytes.Deterministic(777, seed: 99));

        var loaded = OpenVault().LoadLibrary();

        AssertLibrariesEqual(good, loaded);
    }

    [Fact]
    public void LoadLibrary_StalePartialTmpOfNewerSave_StillReturnsLastGoodLibrary()
    {
        var good = BuildSampleLibrary();
        OpenVault().SaveLibrary(good);

        // Simulate an interruption after half of a NEWER encrypted library was written to the .tmp file.
        var newer = BuildSampleLibrary();
        newer.Boards.Clear();
        var scratch = new TempDirectory();
        try
        {
            var scratchVault = new Vault(scratch.Path, _key);
            scratchVault.SaveLibrary(newer);
            var newerBytes = File.ReadAllBytes(Path.Combine(scratch.Path, "library.dat"));
            File.WriteAllBytes(LibraryPath + ".tmp", newerBytes[..(newerBytes.Length / 2)]);
        }
        finally
        {
            scratch.Dispose();
        }

        var loaded = OpenVault().LoadLibrary();

        AssertLibrariesEqual(good, loaded);
    }

    [Fact]
    public void SaveLibrary_WithStaleGarbageTmpOnDisk_SucceedsAndRemovesTmp()
    {
        var vault = OpenVault();
        vault.SaveLibrary(BuildSampleLibrary());
        File.WriteAllBytes(LibraryPath + ".tmp", TestBytes.Deterministic(4096, seed: 5));

        var updated = vault.LoadLibrary();
        updated.Boards[0].Name = "Written after the crash";
        vault.SaveLibrary(updated);

        Assert.False(File.Exists(LibraryPath + ".tmp"));
        var reloaded = OpenVault().LoadLibrary();
        AssertLibrariesEqual(updated, reloaded);
        Assert.Equal("Written after the crash", reloaded.Boards[0].Name);
    }

    [Fact]
    public void SaveLibrary_FirstSaveWithStaleTmpButNoLibrary_Succeeds()
    {
        // Crash during the very first save: only the .tmp exists.
        Directory.CreateDirectory(VaultRoot);
        File.WriteAllBytes(LibraryPath + ".tmp", TestBytes.Deterministic(100));
        var vault = OpenVault();

        var fresh = vault.LoadLibrary(); // the .tmp is ignored, library.dat does not exist yet
        Assert.Empty(fresh.Boards);

        var library = BuildSampleLibrary();
        vault.SaveLibrary(library);

        Assert.False(File.Exists(LibraryPath + ".tmp"));
        AssertLibrariesEqual(library, OpenVault().LoadLibrary());
    }

    [Fact]
    public void SaveLibrary_InterruptedBecauseTmpCannotBeWritten_ThrowsAndKeepsLastGoodLibrary()
    {
        var vault = OpenVault();
        var good = BuildSampleLibrary();
        vault.SaveLibrary(good);
        var goodBytes = File.ReadAllBytes(LibraryPath);

        var changed = vault.LoadLibrary();
        changed.Boards.Clear();
        Exception? ex;
        // Another process (say, a crashed earlier instance) still holds the .tmp exclusively, so the new save
        // fails before anything is written.
        using (new FileStream(LibraryPath + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            ex = Record.Exception(() => vault.SaveLibrary(changed));
        }

        Assert.True(ex is IOException or UnauthorizedAccessException, $"Unexpected: {ex}");
        Assert.Equal(goodBytes, File.ReadAllBytes(LibraryPath)); // original untouched
        AssertLibrariesEqual(good, OpenVault().LoadLibrary());

        // Once the obstruction is gone, saving works again and cleans up.
        vault.SaveLibrary(changed);
        Assert.Empty(OpenVault().LoadLibrary().Boards);
        Assert.False(File.Exists(LibraryPath + ".tmp"));
    }

    [Fact]
    public void SaveLibrary_InterruptedBecauseTargetCannotBeReplaced_ThrowsKeepsLastGoodLibraryAndRemovesTmp()
    {
        var vault = OpenVault();
        var good = BuildSampleLibrary();
        vault.SaveLibrary(good);
        var goodBytes = File.ReadAllBytes(LibraryPath);

        var changed = vault.LoadLibrary();
        changed.Boards[0].Name = "never persisted";
        Exception? ex;
        // The .tmp gets fully written, but the final rename over library.dat is blocked (open without delete sharing).
        using (new FileStream(LibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Record.Exception(() => vault.SaveLibrary(changed));
        }

        Assert.True(ex is IOException or UnauthorizedAccessException, $"Unexpected: {ex}");
        Assert.Equal(goodBytes, File.ReadAllBytes(LibraryPath));
        Assert.False(File.Exists(LibraryPath + ".tmp"));
        AssertLibrariesEqual(good, OpenVault().LoadLibrary());
    }

    [Fact]
    public void ConcurrentSaves_OnOneVault_AllSucceedAndLeaveALoadableLibrary()
    {
        var vault = OpenVault();
        var libraries = Enumerable.Range(0, 16).Select(i =>
        {
            var lib = BuildSampleLibrary();
            lib.Boards[0].Name = $"Writer {i}";
            return lib;
        }).ToArray();

        Parallel.ForEach(libraries, lib => vault.SaveLibrary(lib));

        var loaded = OpenVault().LoadLibrary();
        var winner = Assert.Single(libraries, l => l.Boards[0].Name == loaded.Boards[0].Name);
        AssertLibrariesEqual(winner, loaded);
        Assert.Empty(Directory.GetFiles(VaultRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ConcurrentAddSound_ProducesDistinctReadableSounds()
    {
        var vault = OpenVault();
        var payloads = Enumerable.Range(0, 20).Select(i => TestBytes.Deterministic(2000 + i, seed: i)).ToArray();
        var ids = new Guid[payloads.Length];

        Parallel.For(0, payloads.Length, i => ids[i] = vault.AddSound(payloads[i]));

        Assert.Equal(payloads.Length, ids.Distinct().Count());
        for (int i = 0; i < payloads.Length; i++)
        {
            Assert.Equal(payloads[i], vault.GetSound(ids[i]));
        }
    }

    [Fact]
    public void GetSound_SoundsFolderDeleted_ThrowsFileNotFoundException()
    {
        var vault = OpenVault();
        var id = vault.AddSound(TestBytes.Deterministic(10));
        Directory.Delete(Path.Combine(VaultRoot, "sounds"), recursive: true);

        Assert.Throws<FileNotFoundException>(() => vault.GetSound(id));
        Assert.False(vault.SoundExists(id));
    }

    [Fact]
    public void AtomicFileWrite_ReplacesExistingFileAndLeavesNoTmp()
    {
        var path = _temp.Combine("sub", "file.bin");
        AtomicFile.Write(path, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(path + AtomicFile.TempSuffix, new byte[] { 9, 9, 9, 9, 9 });

        AtomicFile.Write(path, new byte[] { 4, 5 });

        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + AtomicFile.TempSuffix));
        Assert.Equal(".tmp", AtomicFile.TempSuffix);
    }

    // ---------------------------------------------------------------- dispose

    [Fact]
    public void Dispose_MakesTheVaultUnusableAndIsIdempotent()
    {
        var vault = OpenVault();
        var saved = BuildSampleLibrary();
        vault.SaveLibrary(saved);
        var id = vault.AddSound(TestBytes.Deterministic(100));
        var libraryBytes = File.ReadAllBytes(LibraryPath);

        vault.Dispose();
        vault.Dispose(); // second call is a no-op

        Assert.Throws<ObjectDisposedException>(() => vault.LoadLibrary());
        Assert.Throws<ObjectDisposedException>(() => vault.SaveLibrary(new Library()));
        Assert.Throws<ObjectDisposedException>(() => vault.AddSound(new byte[] { 1 }));
        Assert.Throws<ObjectDisposedException>(() => vault.GetSound(id));
        Assert.Throws<ObjectDisposedException>(() => vault.DeleteSound(id));
        Assert.Throws<ObjectDisposedException>(() => vault.SoundExists(id));

        // Nothing on disk changed, and a fresh vault with the real key still reads it all.
        Assert.Equal(libraryBytes, File.ReadAllBytes(LibraryPath));
        var reopened = OpenVault();
        AssertLibrariesEqual(saved, reopened.LoadLibrary());
        Assert.Equal(TestBytes.Deterministic(100), reopened.GetSound(id));
    }

    // ---------------------------------------------------------------- ciphertext on disk

    [Fact]
    public void SaveLibrary_BytesOnDisk_AreCiphertextWithoutJsonOrNames()
    {
        var library = BuildSampleLibrary();
        OpenVault().SaveLibrary(library);

        var onDisk = File.ReadAllBytes(LibraryPath);

        Assert.Equal("ACV1", Encoding.ASCII.GetString(onDisk, 0, 4));
        // Every marker is at least 4 bytes: a shorter one (such as the 2-byte "{\"") turns up in random
        // ciphertext often enough to make this test flaky.
        foreach (var marker in new[] { "Boards", "Slots", "Settings", "SchemaVersion", "Airhorn", "Ctrl+Alt", "{\"SchemaVersion\"" })
        {
            Assert.False(TestBytes.Contains(onDisk, Encoding.UTF8.GetBytes(marker)), $"library.dat contains plaintext \"{marker}\".");
        }

        // Positive control: the decrypted payload really does contain those markers, so the check above is meaningful.
        var plaintext = Encoding.UTF8.GetString(AesGcmCrypto.Decrypt(_key, onDisk));
        Assert.StartsWith("{\"SchemaVersion\"", plaintext);
        Assert.Contains("Boards", plaintext);
        Assert.Contains("Airhorn", plaintext);
    }

    [Fact]
    public void AddSound_BytesOnDisk_AreCiphertextWithoutRiffOrWaveHeaders()
    {
        var vault = OpenVault();
        var wav = TestWav.CanonicalWavBytes(frames: 9600);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4)); // positive control

        var id = vault.AddSound(wav);
        var onDisk = File.ReadAllBytes(SoundPath(id));

        Assert.Equal("ACV1", Encoding.ASCII.GetString(onDisk, 0, 4));
        Assert.Equal(4 + 12 + 16 + wav.Length, onDisk.Length);
        Assert.False(TestBytes.Contains(onDisk, Encoding.ASCII.GetBytes("RIFF")));
        Assert.False(TestBytes.Contains(onDisk, Encoding.ASCII.GetBytes("WAVE")));
        Assert.False(TestBytes.Contains(onDisk, Encoding.ASCII.GetBytes("fmt ")));
        Assert.False(TestBytes.Contains(onDisk, wav[44..300])); // no run of raw sample data
    }

    [Fact]
    public void VaultFolder_AfterSavesAndAdds_ContainsOnlyEncryptedFiles()
    {
        var vault = OpenVault();
        vault.SaveLibrary(BuildSampleLibrary());
        vault.AddSound(TestWav.CanonicalWavBytes(frames: 480));
        vault.AddSound(TestWav.CanonicalWavBytes(frames: 960));

        var files = Directory.GetFiles(VaultRoot, "*", SearchOption.AllDirectories);

        Assert.Equal(3, files.Length);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan().StartsWith("ACV1"u8), $"{file} is not an ACV1 encrypted file.");
            Assert.True(file.EndsWith("library.dat", StringComparison.Ordinal) || file.EndsWith(".enc", StringComparison.Ordinal), file);
        }
    }

    [Fact]
    public void SaveLibrary_SameLibraryTwice_ProducesDifferentCiphertext()
    {
        var vault = OpenVault();
        var library = BuildSampleLibrary();

        vault.SaveLibrary(library);
        var first = File.ReadAllBytes(LibraryPath);
        vault.SaveLibrary(library);
        var second = File.ReadAllBytes(LibraryPath);

        Assert.NotEqual(first, second); // fresh nonce per write
        AssertLibrariesEqual(library, vault.LoadLibrary());
    }

    // ---------------------------------------------------------------- helpers

    private static Library BuildSampleLibrary()
    {
        var soundA = Guid.NewGuid();
        var soundB = Guid.NewGuid();

        return new Library
        {
            SchemaVersion = 1,
            Boards =
            {
                new Board
                {
                    Name = "Memes",
                    Slots =
                    {
                        new SoundSlot
                        {
                            Name = "Airhorn",
                            ColorHex = "#FF0000",
                            Hotkey = "Ctrl+Alt+D1",
                            Volume = 1.5f,
                            PlayMode = PlayMode.OneShot,
                            SoundId = soundA,
                            DurationSeconds = 2.345,
                            OriginalFileName = "airhorn.mp3",
                            OriginalSizeBytes = 123_456,
                        },
                        new SoundSlot
                        {
                            Name = "Drum roll",
                            ColorHex = "#00FF00",
                            Hotkey = null,
                            Volume = 0f,
                            PlayMode = PlayMode.Toggle,
                            SoundId = soundB,
                            DurationSeconds = 30.0,
                            OriginalFileName = "drum roll (final) v2.flac",
                            OriginalSizeBytes = 9_876_543_210,
                        },
                        new SoundSlot
                        {
                            Name = "Ünïcödé ✓ 音",
                            ColorHex = "#4F8EF7",
                            Hotkey = "Shift+F12",
                            Volume = 2.0f,
                            PlayMode = PlayMode.HoldToPlay,
                            SoundId = soundA, // two slots may share one sound
                            DurationSeconds = 0.001,
                            OriginalFileName = "quote\"and\\backslash.wav",
                            OriginalSizeBytes = 0,
                        },
                    },
                },
                new Board { Name = "Empty board" },
                new Board
                {
                    Name = "Third",
                    Slots = { new SoundSlot { Name = "Lonely", Volume = 0.333333f } },
                },
            },
            Settings = new AppSettings
            {
                InputDeviceId = "{0.0.1.00000000}.{11111111-2222-3333-4444-555555555555}",
                HeadsetDeviceId = "{0.0.0.00000000}.{c3c3c3c3-d4d4-e5e5-f6f6-070707070707}",
                InputDeviceName = "Headset (Crusher ANC 2)",
                HeadsetDeviceName = "Headphones (Crusher ANC 2)",
                EqAllAudio = true,
                EqAutoPreamp = false,
                OutputDeviceId = "{0.0.0.00000000}.{66666666-7777-8888-9999-000000000000}",
                MonitorDeviceId = "{0.0.0.00000000}.{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}",
                MonitorEnabled = true,
                MicMuted = true,
                StopAllHotkey = "Ctrl+Alt+End",
                EqEnabled = true,
                EqGainsDb = new[] { -12f, -6.5f, -1.25f, 0f, 0.1f, 3f, 6f, 9.75f, 11.5f, 12f },
                FxEnabled = false,
                EqMode = EqMode.ThirtyOneBandThirdOctave,
                EqBands = new List<EqBand>
                {
                    new(30, -12f), new(70, -6.5f), new(120, -1.25f), new(260, 0f), new(480, 0.1f),
                    new(1100, 3f), new(2100, 6f), new(3900, 9.75f), new(8200, 11.5f), new(15000, 12f),
                },
                EqGains31Db = Enumerable.Range(0, 31).Select(i => (i - 15) * 1.5f).ToArray(),
                Effects = new EffectLevels { Bass = 40, Fidelity = 25, Ambience = 10, Surround = 60, DynamicBoost = 75 },
                FxPresetName = "Late night",
                FxPresetModified = true,
                SystemEqEnabled = true,
                SystemEqSourceDeviceId = "{0.0.0.00000000}.{12121212-3434-5656-7878-909090909090}",
                SystemEqOutputDeviceId = "{0.0.0.00000000}.{abababab-cdcd-efef-0101-232323232323}",
                SetDefaultCommunications = true,
                WindowWidth = 1600,
                WindowHeight = 900.5,
                WindowLeft = -1920,
                WindowTop = 12.25,
                WindowMaximized = true,
                MirrorEnabled = true,
                MirrorTargets =
                {
                    new MirrorTarget
                    {
                        DeviceId = "{0.0.0.00000000}.{c8c00f88-f4fc-4ece-84a9-a04737e700df}",
                        DeviceName = "Digital Output (Realtek USB2.0 Audio)",
                        Enabled = true,
                        DelayMs = 185,
                        VolumePercent = 72,
                    },
                    new MirrorTarget
                    {
                        DeviceId = "{0.0.0.00000000}.{5a5a5a5a-6b6b-7c7c-8d8d-9e9e9e9e9e9e}",
                        DeviceName = "Ünïcödé ✓ Speakers",
                        Enabled = false,
                        DelayMs = 0,
                        VolumePercent = 0,
                    },
                },
            },
            UserPresets =
            {
                new FxPreset
                {
                    Name = "Late night",
                    EqMode = EqMode.TenBandAdjustable,
                    Bands = FxSettings.DefaultTenBands(new[] { 2f, 1f, 0f, 0f, -1f, 0f, 1f, 2f, 3f, 4f }),
                    Effects = new EffectLevels { Bass = 5, Fidelity = 15, Ambience = 25, Surround = 35, DynamicBoost = 45 },
                },
                new FxPreset
                {
                    Name = "Ünïcödé ✓ thirds",
                    EqMode = EqMode.ThirtyOneBandThirdOctave,
                    Bands = FxSettings.ThirdOctaveFrequenciesHz.Select((f, i) => new EqBand(f, i % 3 - 1)).ToList(),
                    Effects = new EffectLevels { Ambience = 100 },
                },
            },
        };
    }

    private static void AssertLibrariesEqual(Library expected, Library actual)
    {
        Assert.Equal(expected.UserPresets.Count, actual.UserPresets.Count);
        for (int p = 0; p < expected.UserPresets.Count; p++)
        {
            var ep = expected.UserPresets[p];
            var ap = actual.UserPresets[p];
            Assert.Equal(ep.Name, ap.Name);
            Assert.Equal(ep.EqMode, ap.EqMode);
            Assert.Equal(ep.Bands.Select(b => (b.FrequencyHz, b.GainDb)), ap.Bands.Select(b => (b.FrequencyHz, b.GainDb)));
            Assert.True(ep.Effects.SameAs(ap.Effects));
        }

        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Boards.Count, actual.Boards.Count);
        for (int b = 0; b < expected.Boards.Count; b++)
        {
            var eb = expected.Boards[b];
            var ab = actual.Boards[b];
            Assert.Equal(eb.Id, ab.Id);
            Assert.Equal(eb.Name, ab.Name);
            Assert.Equal(eb.Slots.Count, ab.Slots.Count);
            for (int s = 0; s < eb.Slots.Count; s++)
            {
                var es = eb.Slots[s];
                var @as = ab.Slots[s];
                Assert.Equal(es.Id, @as.Id);
                Assert.Equal(es.Name, @as.Name);
                Assert.Equal(es.ColorHex, @as.ColorHex);
                Assert.Equal(es.Hotkey, @as.Hotkey);
                Assert.Equal(BitConverter.SingleToInt32Bits(es.Volume), BitConverter.SingleToInt32Bits(@as.Volume));
                Assert.Equal(es.PlayMode, @as.PlayMode);
                Assert.Equal(es.SoundId, @as.SoundId);
                Assert.Equal(es.DurationSeconds, @as.DurationSeconds);
                Assert.Equal(es.OriginalFileName, @as.OriginalFileName);
                Assert.Equal(es.OriginalSizeBytes, @as.OriginalSizeBytes);
            }
        }

        AssertSettingsEqual(expected.Settings, actual.Settings);
    }

    private static void AssertSettingsEqual(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.InputDeviceId, actual.InputDeviceId);
        Assert.Equal(expected.HeadsetDeviceId, actual.HeadsetDeviceId);
        Assert.Equal(expected.InputDeviceName, actual.InputDeviceName);
        Assert.Equal(expected.HeadsetDeviceName, actual.HeadsetDeviceName);
        Assert.Equal(expected.EqAllAudio, actual.EqAllAudio);
        Assert.Equal(expected.EqAutoPreamp, actual.EqAutoPreamp);
        Assert.Equal(expected.OutputDeviceId, actual.OutputDeviceId);
        Assert.Equal(expected.MonitorDeviceId, actual.MonitorDeviceId);
        Assert.Equal(expected.MonitorEnabled, actual.MonitorEnabled);
        Assert.Equal(expected.MicMuted, actual.MicMuted);
        Assert.Equal(expected.StopAllHotkey, actual.StopAllHotkey);
        Assert.Equal(expected.EqEnabled, actual.EqEnabled);
        Assert.Equal(expected.EqGainsDb, actual.EqGainsDb);
        Assert.Equal(expected.FxEnabled, actual.FxEnabled);
        Assert.Equal(expected.EqMode, actual.EqMode);
        // Fields an Effects-unaware library leaves null come back filled the way the migration fills them, and a direct
        // edit of the 1.4 EqGainsDb array is adopted into the bands (the array wins; frequencies are kept).
        var expectedBands = (expected.EqBands ?? FxSettings.DefaultTenBands(expected.EqGainsDb))
            .Select((b, i) => (b.FrequencyHz, expected.EqGainsDb is { Length: 10 } g ? g[i] : b.GainDb));
        Assert.Equal(expectedBands, actual.EqBands!.Select(b => (b.FrequencyHz, b.GainDb)));
        Assert.Equal(expected.EqGains31Db ?? new float[31], actual.EqGains31Db);
        Assert.True((expected.Effects ?? new EffectLevels()).SameAs(actual.Effects), "effect levels differ");
        Assert.Equal(expected.FxPresetName, actual.FxPresetName);
        Assert.Equal(expected.FxPresetModified, actual.FxPresetModified);
        Assert.Equal(expected.SystemEqEnabled, actual.SystemEqEnabled);
        Assert.Equal(expected.SystemEqSourceDeviceId, actual.SystemEqSourceDeviceId);
        Assert.Equal(expected.SystemEqOutputDeviceId, actual.SystemEqOutputDeviceId);
        Assert.Equal(expected.SetDefaultCommunications, actual.SetDefaultCommunications);
        AssertSameDouble(expected.WindowWidth, actual.WindowWidth);
        AssertSameDouble(expected.WindowHeight, actual.WindowHeight);
        AssertSameDouble(expected.WindowLeft, actual.WindowLeft);
        AssertSameDouble(expected.WindowTop, actual.WindowTop);
        Assert.Equal(expected.WindowMaximized, actual.WindowMaximized);
        Assert.Equal(expected.MirrorEnabled, actual.MirrorEnabled);
        Assert.Equal(
            (expected.MirrorTargets ?? new List<MirrorTarget>()).Select(t => (t.DeviceId, t.DeviceName, t.Enabled, t.DelayMs, t.VolumePercent)),
            actual.MirrorTargets.Select(t => (t.DeviceId, t.DeviceName, t.Enabled, t.DelayMs, t.VolumePercent)));
    }

    private static void AssertSameDouble(double expected, double actual)
    {
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual), $"Expected NaN but got {actual}.");
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }
}

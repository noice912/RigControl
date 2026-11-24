using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioController.Core.Abstractions;
using AudioController.Core.Models;

namespace AudioController.Storage;

/// <summary>
/// Encrypted on-disk store for the library (boards, slots, settings) and the sound files.
/// </summary>
/// <remarks>
/// <para>Layout under <see cref="RootDir"/>:</para>
/// <code>
/// library.dat          AES-256-GCM encrypted UTF-8 JSON of <see cref="Library"/>
/// sounds\&lt;guid&gt;.enc   AES-256-GCM encrypted canonical WAV bytes, one file per sound
/// </code>
/// <para>
/// Every write is encrypt-then-<see cref="AtomicFile.Write"/>, so plaintext never reaches the disk,
/// not even in the <c>.tmp</c> file. Reads either return fully authenticated data or throw.
/// </para>
/// <para>
/// <see cref="Dispose"/> wipes the vault's copy of the master key; the vault is unusable afterwards.
/// Every use of the key is serialized under one lock with <see cref="Dispose"/>, so the key can
/// never be zeroed while an encryption or decryption is running.
/// </para>
/// </remarks>
public sealed class Vault : IVault, IDisposable
{
    private const string AppFolderName = "AudioController";
    private const string VaultFolderName = "vault";
    private const string KeysFolderName = "keys";
    private const string LibraryFileName = "library.dat";
    private const string SoundsFolderName = "sounds";
    private const string SoundFileExtension = ".enc";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // AppSettings defaults window position to double.NaN ("not yet placed").
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly byte[] _masterKey;
    private readonly string _libraryPath;
    private readonly string _soundsDirectory;
    private readonly object _libraryLock = new();
    private volatile bool _disposed;

    /// <summary>
    /// Opens (creating directories if needed) the vault rooted at <paramref name="rootDir"/>.
    /// </summary>
    /// <param name="rootDir">Vault root; will contain <c>library.dat</c> and <c>sounds\</c>.</param>
    /// <param name="masterKey">The 32-byte AES-256 master key. The vault keeps its own copy.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootDir"/> is empty, or <paramref name="masterKey"/> is not 32 bytes.
    /// </exception>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public Vault(string rootDir, byte[] masterKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        ArgumentNullException.ThrowIfNull(masterKey);

        if (masterKey.Length != AesGcmCrypto.KeyBytes)
        {
            throw new ArgumentException(
                $"The master key must be exactly {AesGcmCrypto.KeyBytes} bytes; got {masterKey.Length} bytes.",
                nameof(masterKey));
        }

        RootDir = Path.GetFullPath(rootDir);
        _masterKey = (byte[])masterKey.Clone();
        _libraryPath = Path.Combine(RootDir, LibraryFileName);
        _soundsDirectory = Path.Combine(RootDir, SoundsFolderName);

        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(_soundsDirectory);
        DeleteStaleTempFiles();
    }

    /// <summary>The absolute path of the vault root directory.</summary>
    public string RootDir { get; }

    /// <summary>
    /// Opens the per-user vault at <c>%LOCALAPPDATA%\AudioController\vault</c>, using the
    /// DPAPI-protected master key in <c>%LOCALAPPDATA%\AudioController\keys</c> (created on first run).
    /// </summary>
    /// <returns>The default vault.</returns>
    /// <exception cref="InvalidOperationException">The local application data folder cannot be resolved.</exception>
    /// <exception cref="CryptographicException">The stored master key cannot be unlocked.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    public static Vault OpenDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data folder (%LOCALAPPDATA%) could not be resolved for the current user.");
        }

        var appDirectory = Path.Combine(localAppData, AppFolderName);
        var masterKey = MasterKeyProvider.GetOrCreate(Path.Combine(appDirectory, KeysFolderName));

        try
        {
            return new Vault(Path.Combine(appDirectory, VaultFolderName), masterKey);
        }
        finally
        {
            // The vault holds its own copy; clear this one.
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The file is truncated, has a bad signature, or holds no library.</exception>
    /// <exception cref="CryptographicException">The file was tampered with or the key is wrong.</exception>
    /// <exception cref="JsonException">The decrypted content is not a valid library document.</exception>
    /// <exception cref="ObjectDisposedException">The vault has been disposed.</exception>
    public Library LoadLibrary()
    {
        lock (_libraryLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!File.Exists(_libraryPath))
            {
                var fresh = new Library();
                Normalize(fresh);
                return fresh;
            }

            var encrypted = File.ReadAllBytes(_libraryPath);
            byte[] json;
            try
            {
                json = AesGcmCrypto.Decrypt(_masterKey, encrypted);
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException(
                    $"\"{_libraryPath}\" could not be decrypted: it was tampered with, or the key is wrong.", ex);
            }

            try
            {
                var library = JsonSerializer.Deserialize<Library>(json, JsonOptions)
                    ?? throw new InvalidDataException(
                        $"\"{_libraryPath}\" decrypted successfully but does not contain a library.");

                Normalize(library);
                return library;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="library"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The vault has been disposed.</exception>
    public void SaveLibrary(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var json = JsonSerializer.SerializeToUtf8Bytes(library, JsonOptions);
        try
        {
            lock (_libraryLock)
            {
                // The master key is only ever used under this lock (see Dispose), so it can
                // never be wiped mid-encryption and stale ciphertext can never be written.
                ObjectDisposedException.ThrowIf(_disposed, this);
                var encrypted = AesGcmCrypto.Encrypt(_masterKey, json);
                AtomicFile.Write(_libraryPath, encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="wavBytes"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="wavBytes"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The vault has been disposed.</exception>
    public Guid AddSound(byte[] wavBytes)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);

        if (wavBytes.Length == 0)
        {
            throw new ArgumentException("Sound data is empty.", nameof(wavBytes));
        }

        lock (_libraryLock)
        {
            // Key use is serialized with Dispose: no mid-encryption wipe, no garbage ciphertext.
            ObjectDisposedException.ThrowIf(_disposed, this);
            var encrypted = AesGcmCrypto.Encrypt(_masterKey, wavBytes);

            Guid id;
            string path;
            do
            {
                id = Guid.NewGuid();
                path = GetSoundPath(id);
            }
            while (File.Exists(path));

            AtomicFile.Write(path, encrypted);
            return id;
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">No sound with <paramref name="soundId"/> exists.</exception>
    /// <exception cref="InvalidDataException">The sound file is truncated or has a bad signature.</exception>
    /// <exception cref="CryptographicException">The sound file was tampered with or the key is wrong.</exception>
    /// <exception cref="ObjectDisposedException">The vault has been disposed.</exception>
    public byte[] GetSound(Guid soundId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = GetSoundPath(soundId);

        byte[] encrypted;
        try
        {
            encrypted = File.ReadAllBytes(path);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileNotFoundException($"Sound {soundId:D} is not in the vault.", path, ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new FileNotFoundException($"Sound {soundId:D} is not in the vault.", path, ex);
        }

        try
        {
            lock (_libraryLock)
            {
                // Key use is serialized with Dispose: never decrypt with a half-wiped key.
                ObjectDisposedException.ThrowIf(_disposed, this);
                return AesGcmCrypto.Decrypt(_masterKey, encrypted);
            }
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException(
                $"\"{path}\" could not be decrypted: it was tampered with, or the key is wrong.", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>Deleting a sound that does not exist is a no-op.</remarks>
    /// <exception cref="ObjectDisposedException">The vault has been disposed.</exception>
    public void DeleteSound(Guid soundId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = GetSoundPath(soundId);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var tempPath = path + AtomicFile.TempSuffix;
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The vault has been disposed.</exception>
    public bool SoundExists(Guid soundId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return File.Exists(GetSoundPath(soundId));
    }

    /// <summary>
    /// Wipes the vault's copy of the master key. Every later call on this vault throws
    /// <see cref="ObjectDisposedException"/>. Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_libraryLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CryptographicOperations.ZeroMemory(_masterKey);
        }
    }

    private string GetSoundPath(Guid soundId) =>
        Path.Combine(_soundsDirectory, soundId.ToString("D") + SoundFileExtension);

    /// <summary>
    /// Removes every <c>*.tmp</c> under the vault root left by an interrupted write (for example a crash
    /// during <see cref="AddSound"/> leaves <c>sounds\&lt;guid&gt;.enc.tmp</c>, which nothing else would
    /// ever clean up). A <c>.tmp</c> is never the live file, so deleting it is always safe. Best effort:
    /// a file that cannot be removed now is left for the next start.
    /// </summary>
    private void DeleteStaleTempFiles()
    {
        string[] stale;
        try
        {
            stale = Directory.GetFiles(RootDir, "*" + AtomicFile.TempSuffix, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in stale)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort only (for example the file is still open in another process).
            }
        }
    }

    /// <summary>
    /// Replaces nulls that an older or hand-edited document could deserialize into
    /// non-nullable members, so callers never see a null collection or settings object.
    /// </summary>
    private static void Normalize(Library library)
    {
        library.Boards ??= new List<Board>();
        library.Boards.RemoveAll(static board => board is null);

        foreach (var board in library.Boards)
        {
            board.Slots ??= new List<SoundSlot>();
            board.Slots.RemoveAll(static slot => slot is null);
        }

        library.Settings ??= new AppSettings();

        // Effects panel: fills EqBands from EqGainsDb for a pre-Effects library (on the default frequencies), clamps
        // every band / effect, and keeps EqGainsDb mirrored for older versions. User presets are repaired, never dropped.
        AudioController.Core.Equalizer.FxSettings.Normalize(library.Settings);
        library.UserPresets ??= new List<FxPreset>();
        AudioController.Core.Equalizer.FxPresets.NormalizeUserPresets(library.UserPresets);

        // "Also play on": a missing list becomes empty; broken entries are repaired (delay 0–500 ms, volume 0–100 %).
        // Stored choices (EqAutoPreamp, EqMode, MirrorEnabled) are never flipped here.
        AudioController.Core.Routing.MirrorPlanner.Normalize(library.Settings);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace AudioController.Storage;

/// <summary>
/// Loads or creates the vault's 256-bit master key. The key is stored on disk only in its
/// DPAPI-protected form (<see cref="DataProtectionScope.CurrentUser"/>), so it can be unwrapped
/// only by the same Windows user account.
/// </summary>
public static class MasterKeyProvider
{
    /// <summary>File name of the protected key inside the keys directory.</summary>
    public const string KeyFileName = "master.key.dpapi";

    // Application-specific DPAPI entropy: another app running as the same user cannot
    // unprotect this blob with a plain ProtectedData.Unprotect(blob, null, CurrentUser) call.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AudioController.MasterKey.v1");

    /// <summary>
    /// Returns the raw master key, creating and storing a new random key on first run.
    /// </summary>
    /// <param name="keysDirectory">Directory that holds <c>master.key.dpapi</c>; created if missing.</param>
    /// <returns>The raw 32-byte AES-256 key. Callers should clear it when no longer needed.</returns>
    /// <exception cref="ArgumentException"><paramref name="keysDirectory"/> is null, empty or whitespace.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows (DPAPI is unavailable).</exception>
    /// <exception cref="CryptographicException">
    /// The stored key cannot be unprotected (for example it was created by a different Windows user).
    /// </exception>
    /// <exception cref="InvalidDataException">The stored key unprotected to the wrong length.</exception>
    /// <remarks>
    /// An existing key file is never overwritten, even when it cannot be read: replacing it would
    /// make every file already in the vault permanently undecryptable.
    /// </remarks>
    public static byte[] GetOrCreate(string keysDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keysDirectory);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The master key is protected with Windows DPAPI, which is only available on Windows.");
        }

        var directory = Path.GetFullPath(keysDirectory);
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, KeyFileName);

        if (File.Exists(keyPath))
        {
            return LoadExisting(keyPath);
        }

        var key = RandomNumberGenerator.GetBytes(AesGcmCrypto.KeyBytes);
        var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);

        if (TryCreateKeyFile(keyPath, protectedKey))
        {
            return key;
        }

        // Another process created the key between our existence check and our move.
        // Use its key so both processes agree.
        CryptographicOperations.ZeroMemory(key);
        return LoadExisting(keyPath);
    }

    private static byte[] LoadExisting(string keyPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The master key is protected with Windows DPAPI, which is only available on Windows.");
        }

        var protectedKey = File.ReadAllBytes(keyPath);

        byte[] key;
        try
        {
            key = ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException(
                $"The vault master key at \"{keyPath}\" could not be unlocked. It may have been created " +
                "by a different Windows user account, or the file is damaged.",
                ex);
        }

        if (key.Length != AesGcmCrypto.KeyBytes)
        {
            var length = key.Length;
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException(
                $"The vault master key at \"{keyPath}\" has an invalid length ({length} bytes, " +
                $"expected {AesGcmCrypto.KeyBytes}).");
        }

        return key;
    }

    /// <summary>
    /// Writes the protected key to a unique temp file, then moves it into place without
    /// overwriting. Returns false if a key file already exists at <paramref name="keyPath"/>.
    /// </summary>
    private static bool TryCreateKeyFile(string keyPath, byte[] protectedKey)
    {
        var tempPath = $"{keyPath}.{Guid.NewGuid():N}{AtomicFile.TempSuffix}";

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(protectedKey, 0, protectedKey.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, keyPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort only; the temp file holds DPAPI-protected bytes, never the raw key.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}

using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RigControl.Core.Platform;

/// <summary>Windows' own per-user protection (DPAPI, CurrentUser): only this Windows account can read it back.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Description => "Kept with Windows' own protection for your account (DPAPI).";

    public byte[] Protect(byte[] data, byte[] purpose) => ProtectedData.Protect(data, purpose, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData, byte[] purpose) => ProtectedData.Unprotect(protectedData, purpose, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Linux and macOS: secrets are encrypted with a random key that lives in one file, "secret.key", in RigControl's own
/// folder. The file is created so that only its owner can read or write it (mode 0600).
/// What this does and does not do: the settings file never holds a secret in plain text, so sharing or backing up the
/// settings alone gives nothing away. Any program running as the same user can read the key file, exactly as it could
/// read the settings; this is file permissions, not a keyring.
/// </summary>
public sealed class FileKeySecretProtector : ISecretProtector
{
    public const string KeyFileName = "secret.key";

    /// <summary>Where a key file of the wrong size is moved to when a new key has to be made.</summary>
    public const string DamagedKeyFileName = KeyFileName + ".damaged";
    private const byte Version = 1;
    private const int KeyBytes = 64; // 32 for encrypting, 32 for the integrity check
    private const int IvBytes = 16;
    private const int MacBytes = 32;

    private readonly string _directory;
    private readonly object _gate = new();
    private byte[]? _key;

    public FileKeySecretProtector(string directory)
    {
        _directory = directory;
    }

    public string Description => "Encrypted with a key kept in a file only your user account can read (" + KeyFileName + ").";

    public string KeyPath => Path.Combine(_directory, KeyFileName);

    public byte[] Protect(byte[] data, byte[] purpose)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(purpose);
        var key = Key(create: true)!;
        using var aes = Aes.Create();
        aes.Key = key.AsSpan(0, 32).ToArray();
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var cipher = aes.EncryptCbc(data, iv);
        var result = new byte[1 + IvBytes + cipher.Length + MacBytes];
        result[0] = Version;
        iv.CopyTo(result, 1);
        cipher.CopyTo(result, 1 + IvBytes);
        var mac = Mac(key, purpose, result.AsSpan(0, 1 + IvBytes + cipher.Length));
        mac.CopyTo(result, 1 + IvBytes + cipher.Length);
        return result;
    }

    public byte[] Unprotect(byte[] protectedData, byte[] purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        ArgumentNullException.ThrowIfNull(purpose);
        // At least one cipher block: an empty secret still encrypts to 16 bytes of padding.
        if (protectedData.Length < 1 + IvBytes + 16 + MacBytes || protectedData[0] != Version)
            throw new CryptographicException("This is not something RigControl protected.");
        var key = Key(create: false) ?? throw new CryptographicException("The key file is missing, so this can no longer be read.");
        var bodyLength = protectedData.Length - MacBytes;
        var expected = Mac(key, purpose, protectedData.AsSpan(0, bodyLength));
        if (!CryptographicOperations.FixedTimeEquals(expected, protectedData.AsSpan(bodyLength)))
            throw new CryptographicException("This was protected with another key or for another use.");
        using var aes = Aes.Create();
        aes.Key = key.AsSpan(0, 32).ToArray();
        return aes.DecryptCbc(protectedData.AsSpan(1 + IvBytes, bodyLength - 1 - IvBytes), protectedData.AsSpan(1, IvBytes));
    }

    private static byte[] Mac(byte[] key, byte[] purpose, ReadOnlySpan<byte> body)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.AsSpan(32, 32));
        // The length goes first so "purpose + body" can never be read as a different pair.
        h.AppendData(BitConverter.GetBytes(purpose.Length));
        h.AppendData(purpose);
        h.AppendData(body);
        return h.GetHashAndReset();
    }

    private byte[]? Key(bool create)
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            var path = KeyPath;
            if (File.Exists(path))
            {
                OwnerOnly(path);
                var stored = File.ReadAllBytes(path);
                if (stored.Length == KeyBytes) return _key = stored;
                // A key file of any other size is not a key: it was cut short while being written (a crash, a
                // full disk) or damaged later. Nothing can be read back with it either way.
                // Reading: say so and leave the file alone. Protecting something new: make a new key, or nothing
                // could ever be paired again. Whatever the old key protected has to be paired again.
                if (!create) throw new CryptographicException("The key file is damaged: " + path);
                SetAside(path, stored.Length);
            }
            if (!create) return null;

            Directory.CreateDirectory(_directory);
            var key = RandomNumberGenerator.GetBytes(KeyBytes);
            // Written in full under another name first and only then given its real name, so a crash or a full
            // disk in the middle can never leave a short "secret.key" behind.
            var temp = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            // Made owner-only in the same step that creates it, so there is no moment when others could read it.
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (var stream = new FileStream(temp, options))
                {
                    stream.Write(key);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: false);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* nothing more can be done */ }
                throw;
            }
            RemoveLeftovers(path);
            return _key = key;
        }
    }

    /// <summary>An empty file is simply removed; anything else is kept next to the new key as "secret.key.damaged".</summary>
    private void SetAside(string path, int length)
    {
        if (length == 0)
        {
            File.Delete(path);
            return;
        }
        File.Move(path, Path.Combine(_directory, DamagedKeyFileName), overwrite: true);
    }

    /// <summary>Half-written keys from a start that was cut short. They were never used.</summary>
    private void RemoveLeftovers(string path)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(_directory, KeyFileName + ".*.tmp"))
            {
                try { File.Delete(old); } catch { /* next time */ }
            }
        }
        catch
        {
            // Tidying up must never stop a key being used.
        }
    }

    /// <summary>A key file that others could read (copied about, restored from a backup) is closed again before use.</summary>
    private static void OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode wanted = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (File.GetUnixFileMode(path) != wanted) File.SetUnixFileMode(path, wanted);
    }
}

using RigControl.Mobile.Core.Link;

namespace RigControl.Mobile.Services;

/// <summary>
/// Where the real link keeps its data on the phone. The phone's private keys for each PC go to SecureStorage
/// (iPhone: the Keychain; Android: encrypted with a key held in the Android Keystore). The list of PCs holds nothing
/// secret (ids, names, the PCs' public keys, epochs, relay, counters, last seen) and goes to Preferences.
/// Android: backups are off (AndroidManifest allowBackup=false) and the data extraction rules leave the app's data out
/// of cloud backup and device-to-device transfer, so keys never leave the phone that way. iPhone: SecureStorage's
/// default Keychain class (AfterFirstUnlock) WOULD travel in an encrypted backup or a phone-to-phone transfer, so every
/// key is written as AfterFirstUnlockThisDeviceOnly (also set as SecureStorage.DefaultAccessible at startup, and keys
/// written by older builds are rewritten that way when first read). On a restored or new phone the keys are then
/// missing, and the app says the PC must be linked again.
/// </summary>
public sealed class MauiLinkStore : ILinkStore
{
    private const string ListKey = "link.pcs";

    private static string KeyName(string pcId) => "link.key." + pcId;

    public Task<string?> LoadListAsync() => Task.FromResult(Preferences.Default.Get<string?>(ListKey, null));

    public Task SaveListAsync(string json)
    {
        Preferences.Default.Set(ListKey, json);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> LoadKeyAsync(string pcId)
    {
        string? text;
        byte[] key;
        try
        {
            text = await SecureStorage.Default.GetAsync(KeyName(pcId));
            if (text is null)
                return null;
            key = Convert.FromBase64String(text);
        }
        catch (Exception)
        {
            // Unreadable (e.g. the Keystore key was reset): the service reports the keys as missing.
            return null;
        }
#if IOS
        // A key stored by an older build may still have the class that travels with backups: rewrite it once. The
        // key that was read is returned whatever happens here. SecureStorage's write deletes the old item before
        // adding the new one, so if the add fails the key is written back again (once) rather than lost.
        bool first;
        lock (Rewritten)
            first = Rewritten.Add(pcId);
        if (first)
        {
            try
            {
                await SecureStorage.SetAsync(KeyName(pcId), text, Security.SecAccessible.AfterFirstUnlockThisDeviceOnly);
            }
            catch (Exception)
            {
                // Not tried again in this run (each try deletes first); the next start tries again.
                try { await SecureStorage.Default.SetAsync(KeyName(pcId), text); }
                catch (Exception) { /* still in memory for this run */ }
            }
        }
#endif
        return key;
    }

    public Task SaveKeyAsync(string pcId, byte[] secret) =>
#if IOS
        // Never in a backup or a transfer to another iPhone.
        SecureStorage.SetAsync(KeyName(pcId), Convert.ToBase64String(secret), Security.SecAccessible.AfterFirstUnlockThisDeviceOnly);
#else
        SecureStorage.Default.SetAsync(KeyName(pcId), Convert.ToBase64String(secret));
#endif

#if IOS
    /// <summary>Called once at startup: every SecureStorage item this app writes stays on this iPhone.</summary>
    public static void KeepKeysOnThisDevice() =>
        SecureStorage.DefaultAccessible = Security.SecAccessible.AfterFirstUnlockThisDeviceOnly;

    private static readonly HashSet<string> Rewritten = new(StringComparer.Ordinal);
#endif

    public Task DeleteKeyAsync(string pcId)
    {
        SecureStorage.Default.Remove(KeyName(pcId));
        return Task.CompletedTask;
    }
}

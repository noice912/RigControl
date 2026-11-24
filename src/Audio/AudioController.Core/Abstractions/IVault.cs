using AudioController.Core.Models;

namespace AudioController.Core.Abstractions;

/// <summary>Encrypted storage for the library (boards + settings) and sound data.</summary>
public interface IVault
{
    /// <summary>
    /// Loads the library, or returns a fresh one when no vault exists yet.
    /// Throws on tampered/undecryptable data — never returns partial data.
    /// </summary>
    Library LoadLibrary();

    /// <summary>Atomically persists the library (write .tmp, then replace).</summary>
    void SaveLibrary(Library library);

    /// <summary>Encrypts and stores one sound (canonical WAV bytes); returns its id.</summary>
    Guid AddSound(byte[] wavBytes);

    /// <summary>Decrypts one sound into memory. Throws on tamper/missing.</summary>
    byte[] GetSound(Guid soundId);

    void DeleteSound(Guid soundId);

    bool SoundExists(Guid soundId);
}

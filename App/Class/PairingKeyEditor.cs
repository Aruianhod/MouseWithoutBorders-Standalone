// Standalone pairing-code editing shared by the wizard and settings window.
using System.Text.RegularExpressions;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Class;

internal static class PairingKeyEditor
{
    internal static void EnsureExistingOrNewKey(Settings settings)
    {
        string key;
        try { key = settings.MyKey; }
        catch (System.Exception ex) when (ex is System.Security.Cryptography.CryptographicException or System.ArgumentException or System.FormatException)
        {
            key = string.Empty;
        }

        if (!Encryption.IsKeyValid(key, out _)) key = Encryption.CreateRandomKey();
        if (!TrySave(settings, key, out var error)) throw new System.InvalidOperationException(error);
    }

    internal static bool TrySave(Settings settings, string input, out string error)
    {
        string key = Regex.Replace(input ?? string.Empty, @"\s+", string.Empty);
        if (!Encryption.IsKeyValid(key, out error))
        {
            return false;
        }

        // Persist successfully before adopting the new runtime secret. The exact case
        // matters because the existing key derivation is case-sensitive.
        settings.MyKey = key;
        Encryption.MyKey = key;
        Encryption.MagicNumber = Encryption.Get24BitHash(key);
        Encryption.KeyCorrupted = false;
        // Upstream uses this flag to reject a key after a delay. A validated custom
        // key is usable just like a generated key in this standalone edition.
        Encryption.GeneratedKey = true;
        return true;
    }
}

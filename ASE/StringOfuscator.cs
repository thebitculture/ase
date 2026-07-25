#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace ASE;

/// <summary>
/// Reversible protection for strings stored in the configuration file.
///
/// IMPORTANT: this is obfuscation, not security. The key ships inside the
/// binary, so anyone willing to extract it can recover every protected value.
/// Its purpose is to keep secrets from appearing in plain text in a file that
/// users open, back up, sync to the cloud, or paste into a bug report.
/// </summary>
public static class StringOfuscator
{
    private const int NonceSize = 12;   // 96 bits: the value recommended for GCM.
    private const int TagSize = 16;     // 128-bit authentication tag.

    /// <summary>
    /// Key material, injected at build time from Local.props so it stays out
    /// of source control. Falls back to a constant on unconfigured builds.
    /// </summary>
    private static readonly byte[] Key = DeriveKey(
        BuildCredentials.CryptoSeed is { Length: > 0 } seed
            ? seed
            : "ASE/fallback/unconfigured-build");

    /// <summary>Encrypts a string. Returns Base64, safe for a JSON string field.</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var plain = Encoding.UTF8.GetBytes(plainText);

        // Layout: [nonce][tag][ciphertext]
        var result = new byte[NonceSize + TagSize + plain.Length];
        var nonce = result.AsSpan(0, NonceSize);
        var tag = result.AsSpan(NonceSize, TagSize);
        var cipher = result.AsSpan(NonceSize + TagSize);

        // A fresh random nonce per call: reusing one under the same key breaks GCM.
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a value produced by <see cref="Protect"/>.
    /// Returns null if the input is malformed, tampered with, or was written
    /// with a different key.
    /// </summary>
    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;

        try
        {
            var raw = Convert.FromBase64String(protectedText);
            if (raw.Length < NonceSize + TagSize) return null;

            var nonce = raw.AsSpan(0, NonceSize);
            var tag = raw.AsSpan(NonceSize, TagSize);
            var cipher = raw.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(Key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)          // Not valid Base64.
        {
            return null;
        }
        catch (CryptographicException)   // Wrong key, or the value was tampered with.
        {
            return null;
        }
    }

    /// <summary>Stretches a passphrase into a 256-bit AES key.</summary>
    private static byte[] DeriveKey(string passphrase)
    {
        // A fixed salt is acceptable here: there is a single key for the whole
        // application, so per-value salting would buy nothing.
        var salt = Encoding.UTF8.GetBytes("ASE.Configuration.SecretProtector.v1");

        return Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(passphrase),
            salt: salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
    }
}
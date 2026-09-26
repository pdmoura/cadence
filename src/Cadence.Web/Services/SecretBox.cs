using System.Security.Cryptography;
using System.Text;

namespace Cadence.Web.Services;

/// <summary>
/// Encrypts secrets (AI provider keys) before they are written to the settings table. AES-256-GCM with a key derived from
/// <c>Secrets:Key</c>, falling back to <c>Webhooks:Secret</c>. Stored as base64(nonce | ciphertext | tag).
/// The database alone never reveals a key; rotating the server secret invalidates stored keys, which then have to be re-entered.
/// </summary>
public sealed class SecretBox(IConfiguration config)
{
    private byte[] Key => SHA256.HashData(Encoding.UTF8.GetBytes(
        new[] { config["Secrets:Key"], config["Webhooks:Secret"] }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "cadence-local-development-only"));

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            var all = Convert.FromBase64String(stored);
            if (all.Length < 29) return null;
            var nonce = all[..12];
            var tag = all[^16..];
            var cipher = all[12..^16];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null; // server secret changed: the key must be entered again
        }
    }
}

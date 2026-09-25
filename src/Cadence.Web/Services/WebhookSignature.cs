using System.Security.Cryptography;
using System.Text;

namespace Cadence.Web.Services;

/// <summary>
/// HMAC-SHA256 over the raw request body, sent as <c>X-Cadence-Signature: sha256=&lt;hex&gt;</c>.
/// Constant-time comparison so a wrong signature takes the same time as a right one.
/// </summary>
public static class WebhookSignature
{
    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(header)) return false;
        var expected = Encoding.UTF8.GetBytes(Compute(secret, body));
        var provided = Encoding.UTF8.GetBytes(header.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

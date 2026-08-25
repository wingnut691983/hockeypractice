using System.Security.Cryptography;
using System.Text;

namespace HockeyPractice.Util;

public static class Security
{
    // Excludes 0/O/1/I/L — these codes get read off a phone screen and typed by teenagers,
    // and an ambiguous character turns into a support request.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string HashCode(string input)
    {
        input ??= string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Constant-time comparison so a wrong code can't be narrowed down by timing.</summary>
    public static bool CodeMatches(string? candidate, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashCode(candidate)),
            Encoding.UTF8.GetBytes(expectedHash));
    }

    public static string NewAccessCode(int length = 6) =>
        RandomNumberGenerator.GetString(CodeAlphabet, length);

    /// <summary>URL-safe random token for email confirm / unsubscribe links.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

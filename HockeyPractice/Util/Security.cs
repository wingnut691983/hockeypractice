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

    /// <summary>
    /// Hashes an operator-chosen secret, preserving case.
    ///
    /// <see cref="HashCode"/> upper-cases, which is right for a team code and wrong for a
    /// passphrase. A team code is drawn from an uppercase alphabet by construction, and a
    /// fifteen-year-old reading one off a phone screen should not be punished for the shift key.
    /// A site admin code is whatever the operator typed into an environment variable, and folding
    /// it throws away close to a bit per letter before the hash ever sees it: a fourteen-character
    /// mixed-case code loses roughly fourteen bits, for nothing.
    ///
    /// Safe to change without a migration, unlike the team codes: the admin hash is never stored.
    /// It is derived from SITE_ADMIN_CODE at startup and compared against a hash of what was just
    /// typed, so both sides move together on the next boot.
    /// </summary>
    public static string HashSecret(string input)
    {
        input ??= string.Empty;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim()));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Constant-time comparison of an operator secret against <see cref="HashSecret"/>.</summary>
    public static bool SecretMatches(string? candidate, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashSecret(candidate)),
            Encoding.UTF8.GetBytes(expectedHash));
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

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
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(input)));
        return Convert.ToHexString(bytes);
    }

    // PBKDF2 parameters for the manager code. 600k iterations is the current OWASP figure for
    // PBKDF2-HMAC-SHA256, and it is affordable here precisely because this is a cold path: a
    // manager code is verified when it is typed, and the access cookie then carries the claim for
    // up to 180 days. Nobody meets this cost twice in a season.
    private const int Pbkdf2Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const string Pbkdf2Prefix = "pbkdf2$";

    /// <summary>
    /// Hashes a MANAGER code: PBKDF2-HMAC-SHA256, per-row random salt, stored self-describing as
    /// <c>pbkdf2$iterations$salt$hash</c> so the parameters travel with the value and can be
    /// raised later without a second column.
    ///
    /// This is the only code that needs it. The player code lives in plaintext beside its hash on
    /// purpose (see Team.ViewCode), so hashing it harder protects nothing, and the site admin code
    /// has no stored hash at all. Upgrading either to match would be work that buys nothing.
    ///
    /// Same normalisation as <see cref="HashCode"/>: manager codes are drawn from an uppercase
    /// alphabet by construction, so folding case here costs no entropy and keeps an existing code
    /// verifying exactly as it did.
    /// </summary>
    public static string HashManagerCode(string input)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Normalize(input), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

        return $"{Pbkdf2Prefix}{Pbkdf2Iterations}${Convert.ToBase64String(salt)}$" +
               Convert.ToBase64String(key);
    }

    /// <summary>
    /// Verifies a manager code against either format.
    ///
    /// The legacy branch is PERMANENT, not a migration window. A restore rolls the database back
    /// while only ever adding files, so an archive taken before the upgrade brings back rows in
    /// the old format, and that is exactly the moment a manager needs to be able to sign in.
    /// Removing this branch later would turn a recovery into a lockout. Leave it.
    /// </summary>
    public static bool ManagerCodeMatches(string? candidate, string? stored)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(stored)) return false;

        if (!stored.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            return CodeMatches(candidate, stored);

        // pbkdf2 $ iterations $ salt $ hash
        var parts = stored.Split('$');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            // A hand-edited or truncated row refuses rather than throwing out of a sign-in.
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Normalize(candidate), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// True for a manager hash still in the old format, so a caller can upgrade it in place after
    /// a successful sign-in. That self-heals a row restored from an old archive; it is NOT how the
    /// bulk of them get upgraded, because the access cookie means a coach may never type their
    /// code again. Rotating the codes is what does that.
    /// </summary>
    public static bool IsLegacyManagerHash(string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && !stored.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal);

    private static string Normalize(string? input) => (input ?? string.Empty).Trim().ToUpperInvariant();

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

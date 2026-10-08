using System.Globalization;
using System.Security.Cryptography;

namespace AccountingSystem.Core.Security;

/// <summary>هش رمز عبور با PBKDF2-SHA256 و نمک تصادفی. قالب: PBKDF2$SHA256$iterations$salt$hash</summary>
public static class PasswordHashing
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return string.Join('$',
            "PBKDF2",
            "SHA256",
            Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(key));
    }

    public static bool Verify(string password, string storedHash)
    {
        try
        {
            var parts = storedHash.Split('$');
            if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA256")
            {
                return false;
            }
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            {
                return false;
            }
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

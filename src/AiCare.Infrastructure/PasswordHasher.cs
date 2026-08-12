using System.Security.Cryptography;

namespace AiCare.Infrastructure;

public static class PasswordHasher
{
    public static bool VerifyPassword(string password, string passwordHash)
    {
        if (passwordHash.StartsWith("pbkdf2$", StringComparison.Ordinal))
        {
            var parts = passwordHash.Split('$');
            if (parts.Length != 5 ||
                !int.TryParse(parts[1], out var iterations) ||
                !int.TryParse(parts[2], out var saltLength))
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            if (salt.Length != saltLength)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        return passwordHash == password;
    }

    public static string HashPassword(string password)
    {
        const int iterations = 210_000;
        const int saltLength = 16;
        const int keyLength = 32;
        var salt = RandomNumberGenerator.GetBytes(saltLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, keyLength);
        return $"pbkdf2${iterations}${saltLength}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }
}

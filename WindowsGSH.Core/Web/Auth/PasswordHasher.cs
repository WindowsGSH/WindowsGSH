using System.Security.Cryptography;
using System.Text;

namespace WindowsGSH.Core.Web.Auth;

public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;

    public static (string Hash, string Salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var stored = Convert.FromBase64String(hashBase64);
            var computed = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashBytes);
            return CryptographicOperations.FixedTimeEquals(stored, computed);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace ARMenuPlatform.Helpers
{
    /// <summary>
    /// Provides password hashing using SHA256.
    /// All hashes are returned as uppercase hex strings (64 characters).
    ///
    /// IMPORTANT FOR FUTURE: SHA256 is a fast hashing algorithm — acceptable for
    /// an internal admin tool with controlled user creation, but for a public-facing
    /// system consider upgrading to BCrypt (BCrypt.Net-Next NuGet) which is
    /// intentionally slow and resistant to brute-force attacks.
    /// </summary>
    public static class PasswordHelper
    {
        /// <summary>
        /// Hashes a plaintext password using SHA256.
        /// Returns a 64-character uppercase hex string.
        ///
        /// Example:
        ///   PasswordHelper.HashPassword("Admin@123")
        ///   → "E86F78A8A3CAF0B60D8E74E5942AA6D86DC150CD3C03338AEF25B7D2D7E3ACC7"
        /// </summary>
        public static string HashPassword(string plainTextPassword)
        {
            if (string.IsNullOrEmpty(plainTextPassword))
                throw new ArgumentException("Password cannot be null or empty.", nameof(plainTextPassword));

            // Encode as UTF-8 bytes, compute SHA256, convert to uppercase hex string.
            byte[] inputBytes = Encoding.UTF8.GetBytes(plainTextPassword);
            byte[] hashBytes = SHA256.HashData(inputBytes);   // SHA256.HashData — no Dispose needed
            return Convert.ToHexString(hashBytes);              // uppercase hex, .NET 5+
        }

        /// <summary>
        /// Compares a plaintext password against a stored hash.
        /// Uses a constant-time string comparison to prevent timing attacks.
        ///
        /// Returns true if the passwords match.
        /// </summary>
        public static bool VerifyPassword(string plainTextPassword, string storedHash)
        {
            if (string.IsNullOrEmpty(plainTextPassword) || string.IsNullOrEmpty(storedHash))
                return false;

            string hashOfInput = HashPassword(plainTextPassword);

            // CryptographicOperations.FixedTimeEquals prevents timing side-channel attacks:
            // a plain == comparison returns early on first mismatch, leaking timing info.
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hashOfInput),
                Encoding.UTF8.GetBytes(storedHash)
            );
        }
    }
}
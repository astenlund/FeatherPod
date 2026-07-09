using System.Security.Cryptography;
using System.Text;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Single source of truth for API key minting and hashing. Keys use the
/// <c>fp_{userId}_{secret}</c> format and the stored verifier is
/// <c>SHA256(salt + secret)</c>. Shared by the server's user service and the CLI
/// dev-seed command so both produce byte-identical, mutually verifiable keys.
/// </summary>
public static class ApiKeyGenerator
{
    /// <summary>
    /// Mints a new API key for the given user id along with the salt used to hash it.
    /// The plaintext key is only ever available at this point; only the hash is persisted.
    /// </summary>
    public static (string ApiKey, string Salt) Generate(string userId)
    {
        // Generate 128-bit secret (16 bytes -> 22 chars base64url without padding)
        var secretBytes = RandomNumberGenerator.GetBytes(16);
        var secret = Convert.ToBase64String(secretBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // Generate 128-bit salt (16 bytes -> base64 for storage)
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToBase64String(saltBytes);

        var apiKey = $"fp_{userId}_{secret}";

        return (apiKey, salt);
    }

    /// <summary>
    /// Computes the stored verifier <c>SHA256(salt + secret)</c> for an API key.
    /// </summary>
    public static string Hash(string apiKey, string salt)
    {
        // Extract secret from fp_{userId}_{secret}
        // Note: userId contains only alphanumeric + hyphens (no underscores)
        // Secret is base64url which may contain underscores, so we find the second underscore
        var secondUnderscoreIndex = apiKey.IndexOf('_', 3);
        var secret = apiKey[(secondUnderscoreIndex + 1)..];

        var saltBytes = Convert.FromBase64String(salt);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        // Hash(salt + secret)
        var combined = new byte[saltBytes.Length + secretBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
        Buffer.BlockCopy(secretBytes, 0, combined, saltBytes.Length, secretBytes.Length);

        var combinedHashBytes = SHA256.HashData(combined);

        return Convert.ToHexString(combinedHashBytes).ToLowerInvariant();
    }
}

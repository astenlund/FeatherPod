using System.Security.Cryptography;
using System.Text;

namespace FeatherPod.Server.Validation;

internal static class SecurityHelpers
{
    public static bool ConstantTimeEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}

using System.Security.Cryptography;
using System.Text;

namespace WindowsPowerShellRc;

public sealed class BearerKey
{
    public string Value { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public bool IsValid(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Value), Encoding.UTF8.GetBytes(authorization[7..]));
    }
}

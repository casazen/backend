using System.Buffers.Text;
using System.Security.Cryptography;

namespace Casazen.Core.Entities;

public static class PropertyDoorTokens
{
    public const int TokenBytes = 24;

    public const int Length = 32;

    public const int MaxLength = 64;

    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    public static bool IsWellFormed(string? value)
    {
        if (value is null || value.Length != Length)
            return false;

        foreach (var c in value)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        }

        return true;
    }
}

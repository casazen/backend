using System.Buffers.Text;
using System.Security.Cryptography;

namespace Casazen.Core.Entities;

/// <summary>
/// The token of the door page of a property (<see cref="Property.DoorToken"/>, E22/F01/US02/T01, NF-24, NF-50): the QR code printed for the door points to
/// <c>{App:PublicSiteBaseUrl}/checkin/porta/{token}</c>. It is random (192 bits, base64url, so 32 characters) and carries nothing about the property: the id of
/// a property is enumerable, the token is not. It only says which property the page is about; the stay is proved by the guest with its booking code (or its
/// surname) and the arrival date.
/// </summary>
public static class PropertyDoorTokens
{
    /// <summary>Random bytes of a token: 192 bits.</summary>
    public const int TokenBytes = 24;

    /// <summary>Characters of a token: <see cref="TokenBytes"/> bytes in base64url, without padding.</summary>
    public const int Length = 32;

    /// <summary>Column length of <see cref="Property.DoorToken"/>: room beyond <see cref="Length"/>.</summary>
    public const int MaxLength = 64;

    /// <summary>A new random token.</summary>
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// True for a value that can be a token: exactly <see cref="Length"/> characters of the url-safe alphabet. A value of any other shape is not looked up
    /// at all, so a request with a made-up token never reaches the database.
    /// </summary>
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

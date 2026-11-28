using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.OrgTeam;

/// <summary>
/// Secret of an org invitation link (AM-02): 32 random bytes (256 bits) as 64 lowercase hex characters. Only its SHA-256
/// is stored (<c>OrgInvitations.TokenHash</c>), so the database never holds something that opens an invitation. The same
/// scheme as <see cref="Casazen.Core.Suppliers.SupplierInviteTokens"/>, kept in its own type: SHA-256 of a 256-bit random
/// value is enough (an HMAC adds nothing at that size and would force a key rotation), and the invitation id is an
/// identifier, never the secret.
/// </summary>
/// <remarks>
/// The token travels in the link of the email (<c>/invite/accept?token=…</c>) and then in the <b>body</b> of
/// <c>POST /api/org-invitations/lookup</c> and <c>accept</c>, never in a query string of the API (URLs end up in logs).
/// It is never logged. Sending the invitation again, copying its link and the reminder rotate it.
/// </remarks>
public static partial class OrgInvitationTokens
{
    public const int Length = 64;

    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static bool TryNormalize(string? value, out string token)
    {
        token = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return TokenPattern().IsMatch(token);
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// True when <paramref name="storedHash"/> is the hash of <paramref name="token"/>, compared in constant time
    /// (<see cref="CryptographicOperations.FixedTimeEquals"/>). The invitation is found by the indexed hash; this is the
    /// final check on the row that came back.
    /// </summary>
    public static bool Matches(string? storedHash, string token)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(Hash(token)));
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

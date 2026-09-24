namespace Casazen.Core.Suppliers;

/// <summary>
/// Secret that links an anonymous self-serve supplier registration to the Auth0 account created afterwards (SU-02,
/// A4-02). Returned only in the response of the anonymous <c>POST /api/suppliers/register</c>, kept by the web app
/// across the Auth0 signup and sent to <c>POST /api/suppliers/claim</c>. Same format as the invite token
/// (<see cref="SupplierInviteTokens"/>: 32 random bytes, 64 hex characters); only its SHA-256 is stored
/// (<c>SupplierProfiles.ClaimTokenHash</c>).
/// </summary>
public static class SupplierClaimTokens
{
    /// <summary>How long a claim token can be used: the same as an admin invite.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    /// <inheritdoc cref="SupplierInviteTokens.Generate"/>
    public static string Generate() => SupplierInviteTokens.Generate();

    /// <inheritdoc cref="SupplierInviteTokens.TryNormalize"/>
    public static bool TryNormalize(string? value, out string token) => SupplierInviteTokens.TryNormalize(value, out token);

    /// <inheritdoc cref="SupplierInviteTokens.Hash"/>
    public static string Hash(string token) => SupplierInviteTokens.Hash(token);
}

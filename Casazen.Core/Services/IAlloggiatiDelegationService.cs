namespace Casazen.Core.Services;

/// <summary>
/// The delegation of the Alloggiati transmission of an org (AW-R02, AW-D1, ADR-015-R10, E03/F01/US05/T02): the agency or the host stays
/// responsible for the communications, CasaZen is the tool it delegates the transmission to. The delegation is stored on the org with
/// who granted it, when, the version of the text accepted, and its revocation; each grant and revocation is a line of the activity log.
/// </summary>
/// <remarks>
/// <para>A revocation blocks new sends, not the retrieval of the receipts of the transmissions already acquired: the send path
/// (E03/F01/US06) enforces it by reading <see cref="AlloggiatiDelegationInfo.Valid"/>.</para>
/// <para>The legal text and its version belong to the sender wizard (E03/F01/US02/T02) and to the legal documents: here the version is
/// data, a code (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>, at most 50 characters). The service never checks roles: the web layer
/// authorizes the org and the person.</para>
/// </remarks>
public interface IAlloggiatiDelegationService
{
    /// <summary>The delegation of the org. Throws <c>NotFoundException</c> (<c>org_not_found</c>) for an org that does not exist.</summary>
    Task<AlloggiatiDelegationInfo> GetAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Grants the delegation with the version of the text <paramref name="userId"/> accepted, and records
    /// <c>AlloggiatiDelegationGranted</c>; a grant after a revocation clears the revocation. Granting again the version that is
    /// already valid changes and records nothing. A version that is not a code of at most 50 characters is refused with
    /// <c>DomainRuleException</c> <c>alloggiati_delegation_version_invalid</c>.
    /// </summary>
    Task<AlloggiatiDelegationInfo> GrantAsync(Guid orgId, string userId, string textVersion, CancellationToken ct = default);

    /// <summary>
    /// Revokes the delegation and records <c>AlloggiatiDelegationRevoked</c>; the grant (who, when, which version) stays as it was.
    /// Revoking a delegation that is not valid (never granted, already revoked) changes and records nothing.
    /// </summary>
    Task<AlloggiatiDelegationInfo> RevokeAsync(Guid orgId, string userId, CancellationToken ct = default);
}

/// <summary>The delegation of the Alloggiati transmission of an org. No personal data besides the opaque ids of the accounts.</summary>
/// <param name="Valid">Granted and not revoked: CasaZen may transmit for the org.</param>
/// <param name="GrantedAt">When the last grant was made (UTC); <c>null</c> when never granted.</param>
/// <param name="GrantedByUserId">Who made the last grant (the Auth0 subject).</param>
/// <param name="TextVersion">The version of the delegation text accepted with the last grant.</param>
/// <param name="RevokedAt">When the last grant was revoked (UTC); <c>null</c> while it stands.</param>
public sealed record AlloggiatiDelegationInfo(
    bool Valid,
    DateTime? GrantedAt,
    string? GrantedByUserId,
    string? TextVersion,
    DateTime? RevokedAt);

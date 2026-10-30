namespace Casazen.Core.Services;

/// <summary>
/// The privacy notice a landlord accepts for a lease before the guided contract form is opened (CF-BE). The acceptance is an event of the
/// lease (<c>ConcordatoFacilePrivacyAccepted</c>, payload <c>{ "policyVersion": ... }</c>, no personal data) and holds for the version of
/// the notice that was current when it was given: when the notice changes, the form asks for the acceptance again. The person who gave it
/// is not recorded, the lease is: it is the lease that goes to the form.
/// </summary>
public interface IConcordatoFacilePrivacyService
{
    /// <summary>
    /// What the lease of the org has on the privacy notice and on the form: whether the current version of the notice is accepted and
    /// when, and when the signed link was last made. A lease that does not exist or is another org's has nothing: no error is raised and
    /// nothing about it is revealed.
    /// </summary>
    Task<CfPrivacyState> GetStateAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the current version of the privacy notice was accepted for the lease. Idempotent: when that version is already
    /// accepted no second event is written. 404 <c>lease_not_found</c> when the lease does not exist or is another org's (the same
    /// answer for both).
    /// </summary>
    Task<CfPrivacyState> AcceptAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses with 409 <c>concordatofacile_privacy_not_accepted</c> when the lease has no acceptance of the current version of the
    /// notice. Nothing is written. Asked before the signed link is made, for everyone: the bypass of the platform admin is about the
    /// payment, not about the privacy.
    /// </summary>
    Task EnsureAcceptedAsync(Guid leaseId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a lease has on the privacy notice and on the form.
/// </summary>
/// <param name="Accepted">The current version of the notice was accepted for the lease.</param>
/// <param name="AcceptedAt">When the current version was accepted (UTC); null when it was not.</param>
/// <param name="LinkOpenedAt">When the signed link of the form was last made, that is when the form was last opened (UTC); null when never.</param>
public sealed record CfPrivacyState(bool Accepted, DateTime? AcceptedAt, DateTime? LinkOpenedAt)
{
    /// <summary>Nothing accepted, nothing opened.</summary>
    public static CfPrivacyState None { get; } = new(false, null, null);
}

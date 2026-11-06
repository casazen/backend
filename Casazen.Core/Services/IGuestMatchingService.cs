using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Guest matching service for CRM profiles (NF-23, NF-88, ADR-021, E07/F01/US02/T01): finds the profile of a guest across bookings and sources
/// by more than the e-mail address, creates it when there is none, proposes the merges it is not sure of and keeps the aggregates of the
/// profile (stays, spend, properties). The decisions are in <see cref="GuestMatchRules"/>; the keys are keyed hashes of
/// <see cref="ICrmContactIndex"/>, so the profile never holds the data they were made from.
/// </summary>
/// <remarks>
/// <b>Never executes a merge</b>: a guest that shares only a phone, or only a name and a property, gets a profile of its own and a
/// <see cref="GuestMergeCandidate"/> that waits for a person. <b>Never reads</b> <see cref="StayGuest"/>, a document, the place of birth or any
/// other Alloggiati field: the birth date of the booker is read once, to be hashed with the name (AC-NF88-6, Q1-05).
/// Every query names the org of the guest or of the profile, so the service works the same under the tenant of a request, of a job scope and
/// of the event bus.
/// </remarks>
public interface IGuestMatchingService
{
    /// <summary>
    /// Computes the keyed hash (HMAC-SHA256 of <see cref="ICrmContactIndex"/>, lowercase hex) of the normalized composite key per ADR-021-R2:
    /// email.trim().toLowerCase() + "|" + name.trim().toLowerCase() + "|" + surname.trim().toLowerCase() + "|" + dob.yyyyMMdd + "|" +
    /// fiscalCode.trim().toUpperInvariant(). It is the <c>CompositeKeyHash</c> of a profile the matching creates.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>Crm:ContactIndexKey</c> is not configured outside Development and Testing.</exception>
    string ComputeCompositeKeyHash(
        string email,
        string firstName,
        string lastName,
        DateTime? dateOfBirth,
        string? fiscalCode = null);

    /// <summary>
    /// Finds or creates the CRM profile of the given guest and links the guest to it (ADR-021-R3, NF-88). In order: the profile the guest
    /// already has (a merged profile is followed to the one that took its place); the profile of the e-mail contact with the same keyed hash (an
    /// OTA forward address is never a key); the profile with the same keyed name-and-birth hash. When the last two point to different profiles
    /// the guest goes to the e-mail one and the pair is proposed (score 90, <c>same_name_birth</c>). With none, a profile is created and a merge
    /// is proposed with the profiles that share its phone (80, <c>same_phone</c>) or a normalized name and a property stayed in (60,
    /// <c>same_name_same_property</c>), at most <see cref="GuestMatchRules.MaxProposalsPerProfile"/>; a pair already proposed or dismissed is
    /// never proposed again. The new profile, the link of the guest and these proposals are saved together (all or nothing, under an advisory
    /// lock of the guest, so two syncs of one guest make one profile). Fills the keys, the primary guest and the country of the profile when
    /// empty. Saves.
    /// </summary>
    Task<GuestCrmProfile> FindOrCreateProfileAsync(
        Guest guest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds or creates the profile of a person with no booking, who left an e-mail address or a phone number on the welcome page of a property
    /// (NF-88, NF-90). Finds it through the keyed e-mail contact, or through the phone key when exactly one profile has it; otherwise creates an
    /// empty profile (no stay, no primary guest, <paramref name="language"/> as its language). It never creates a <see cref="Guest"/>, and does
    /// not write the contact: the caller does, with the contact service of the CRM.
    /// </summary>
    Task<GuestCrmProfile> FindOrCreateLeadProfileAsync(
        Guid orgId,
        string? email,
        string? phone,
        string? displayName,
        string? language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes the aggregates of a CRM profile from the bookings of every guest linked to it: stays (a booking not cancelled and not
    /// pending, checked out or with a check-out before today in Rome), their spend and first and last arrival, and the properties of every
    /// booking that is not cancelled, upcoming ones included, so the scope of a host sees them. Changes nothing, not even the update time,
    /// when the values are the same.
    /// </summary>
    Task UpdateProfileAggregatesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}

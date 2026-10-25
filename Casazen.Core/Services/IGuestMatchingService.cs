using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Guest matching service for CRM profiles (NF-23, ADR-021): computes composite key hash
/// and finds or creates CRM profiles for guests across bookings.
/// </summary>
public interface IGuestMatchingService
{
    /// <summary>
    /// Computes SHA-256 hash of normalized composite key per ADR-021-R2:
    /// email.trim().toLowerCase() + "|" + name.trim().toLowerCase() + "|" +
    /// surname.trim().toLowerCase() + "|" + dob.yyyyMMdd + "|" + fiscalCode.trim().toUpperInvariant().
    /// </summary>
    string ComputeCompositeKeyHash(
        string email,
        string firstName,
        string lastName,
        DateTime? dateOfBirth,
        string? fiscalCode = null);

    /// <summary>
    /// Finds or creates a CRM profile for the given guest (ADR-021-R3).
    /// Links the guest to the profile and updates aggregates (stays, spend, properties).
    /// </summary>
    Task<GuestCrmProfile> FindOrCreateProfileAsync(
        Guest guest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates CRM profile aggregates after a booking change (stay count, spend, last stay date).
    /// </summary>
    Task UpdateProfileAggregatesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}

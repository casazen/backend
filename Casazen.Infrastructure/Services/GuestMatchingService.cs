using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Guest matching service for CRM profiles (NF-23, ADR-021): matches guests by composite key hash
/// and aggregates their booking history.
/// </summary>
public class GuestMatchingService(
    AppDbContext dbContext,
    ILogger<GuestMatchingService> logger) : IGuestMatchingService
{
    public string ComputeCompositeKeyHash(
        string email,
        string firstName,
        string lastName,
        DateTime? dateOfBirth,
        string? fiscalCode = null)
    {
        // ADR-021-R2: Normalize composite key components
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedFirstName = firstName.Trim().ToLowerInvariant();
        var normalizedLastName = lastName.Trim().ToLowerInvariant();
        var normalizedDob = dateOfBirth?.ToString("yyyyMMdd") ?? string.Empty;
        var normalizedFiscalCode = fiscalCode?.Trim().ToUpperInvariant() ?? string.Empty;

        // Build composite key
        var compositeKey = $"{normalizedEmail}|{normalizedFirstName}|{normalizedLastName}|{normalizedDob}|{normalizedFiscalCode}";

        // Compute SHA-256 hash
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(compositeKey));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public async Task<GuestCrmProfile> FindOrCreateProfileAsync(
        Guest guest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guest);

        if (guest.OrgId == Guid.Empty)
            throw new ArgumentException("Guest must have an OrgId.", nameof(guest));

        // ADR-021-R2: Compute composite key hash
        var hash = ComputeCompositeKeyHash(
            guest.Email,
            guest.FirstName,
            guest.LastName,
            guest.DateOfBirth,
            fiscalCode: null); // Fiscal code not available on Guest entity

        // ADR-021-R3: Find existing profile
        var profile = await dbContext.GuestCrmProfiles
            .FirstOrDefaultAsync(
                p => p.CompositeKeyHash == hash && p.OrgId == guest.OrgId,
                cancellationToken);

        if (profile is not null)
        {
            logger.LogInformation(
                "Matched guest {GuestId} to existing CRM profile {ProfileId}",
                guest.Id, profile.Id);

            // Link guest to profile
            guest.GuestCrmProfileId = profile.Id;
            profile.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            return profile;
        }

        // ADR-021-R3: Create new profile
        logger.LogInformation(
            "Creating new CRM profile for guest {GuestId} with hash {Hash}",
            guest.Id, hash[..8]);

        var now = DateTime.UtcNow;
        profile = new GuestCrmProfile
        {
            OrgId = guest.OrgId,
            CompositeKeyHash = hash,
            ConfirmedMarketingEmail = string.Empty, // Populated from MarketingConsentRecord
            MarketingConsentEmail = false,
            MarketingConsentWhatsApp = false,
            Language = string.Empty,
            Country = guest.Country,
            TotalStays = 0,
            TotalSpend = 0m,
            PropertyIdsJson = "[]",
            TagsJson = "[]",
            FirstStayAt = now,
            LastStayAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.GuestCrmProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Link guest to profile
        guest.GuestCrmProfileId = profile.Id;
        await dbContext.SaveChangesAsync(cancellationToken);

        return profile;
    }

    public async Task UpdateProfileAggregatesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.GuestCrmProfiles
            .Include(p => p.Guests)
            .ThenInclude(g => g.Bookings)
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);

        if (profile is null)
        {
            logger.LogWarning("CRM profile {ProfileId} not found for aggregate update", profileId);
            return;
        }

        var completedBookings = profile.Guests
            .SelectMany(g => g.Bookings)
            .Where(b => b.CheckInDate <= DateTime.UtcNow)
            .ToList();

        if (completedBookings.Count == 0)
        {
            logger.LogInformation("No completed bookings for profile {ProfileId}", profileId);
            return;
        }

        profile.TotalStays = completedBookings.Count;
        profile.TotalSpend = completedBookings.Sum(b => b.TotalPrice);
        profile.FirstStayAt = completedBookings.Min(b => b.CheckInDate);
        profile.LastStayAt = completedBookings.Max(b => b.CheckInDate);

        // Aggregate property IDs
        var propertyIds = completedBookings
            .Select(b => b.PropertyId)
            .Distinct()
            .ToList();
        profile.PropertyIdsJson = JsonSerializer.Serialize(propertyIds);

        profile.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Updated profile {ProfileId}: {Stays} stays, {Spend:C}, {Properties} properties",
            profileId, profile.TotalStays, profile.TotalSpend, propertyIds.Count);
    }
}

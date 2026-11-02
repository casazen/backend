using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPublicAvailabilityService"/>
/// <remarks>
/// The booking checks (<see cref="BookingService.IsPropertyAvailableAsync"/>: bookings through
/// <c>BookingRepository</c>, blocks through <see cref="PropertyICalSyncService.HasOverlappingBlockAsync"/>) and this read
/// share <see cref="PropertyOccupancy"/> and <see cref="CheckoutHolds.OccupiesDates"/> with the same hold TTL, so a stay
/// is refused exactly when one of its nights is shown as taken. The only difference is timing: an expired hold whose guest
/// has paid meanwhile is shown free until the expiry job (or a checkout of those dates) reads Stripe and keeps it (BK-21).
/// </remarks>
public sealed class PublicAvailabilityService(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IPublicAvailabilityService
{
    // The nights taken by stays, holds and blocks: the definition the calendar and the per-site reads share.
    private readonly PublicOccupancyReader _occupancy = new(db, configuration, timeProvider);

    public async Task<PublicAvailability> GetAsync(
        Guid propertyId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (to <= from || (to - from).TotalDays > PublicAvailability.MaxRangeDays)
        {
            throw new DomainRuleException(
                PublicAvailabilityErrorCodes.InvalidRange, "PublicAvailabilityInvalidRange", PublicAvailability.MaxRangeDays);
        }

        // Public data of a published property, filtered by its id: the answer must not depend on a token the caller may
        // send (a signed-in host of another org would otherwise see every night free).
        var published = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => p.Id == propertyId)
            .Where(PublicListing.IsPublished)
            .Select(p => new { p.MinNights })
            .FirstOrDefaultAsync(cancellationToken);
        if (published is null)
        {
            throw new NotFoundException("Property not published on the booking site")
            {
                Code = PublicAvailabilityErrorCodes.PropertyNotFound,
                MessageKey = "PublicPropertyNotFound",
            };
        }

        // Dates only, never the guest or the source of a stay (the reader ignores the tenant filter for the same reason as above).
        var taken = await _occupancy.TakenNightsAsync([propertyId], from, to, cancellationToken);

        return new PublicAvailability(propertyId, from, to, [.. taken[propertyId]], published.MinNights);
    }
}

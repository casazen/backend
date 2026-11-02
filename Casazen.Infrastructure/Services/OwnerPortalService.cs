using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Owner portal data access service (ADR-011).
/// Filters by PropertyOwnership.OwnerUserId and active ownership period (ADR-011-R2, ADR-011-R8).
/// All responses mask guest data per ADR-011-R3: names NULL, emails NULL, phones NULL.
/// </summary>
public sealed class OwnerPortalService(AppDbContext db, TimeProvider timeProvider) : IOwnerPortalService
{
    /// <summary>
    /// Gets properties owned by the owner with occupancy and revenue summaries (ADR-011-R2, ADR-011-R8).
    /// Filters by ownership period: OwnershipStart &lt;= now AND (OwnershipEnd == null OR OwnershipEnd &gt;= now).
    /// Guest data is masked in booking summaries (ADR-011-R3).
    /// </summary>
    public async Task<IReadOnlyList<OwnerPortalPropertyInfo>> GetOwnerPropertiesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Get active property ownerships for this owner (ADR-011-R2, ADR-011-R8)
        var propertyIds = await db.Set<PropertyOwnership>()
            .Where(po => po.OwnerUserId == ownerUserId
                      && po.OwnershipStart <= now
                      && (po.OwnershipEnd == null || po.OwnershipEnd >= now))
            .Select(po => po.PropertyId)
            .ToListAsync(cancellationToken);

        if (!propertyIds.Any())
            return [];

        // Get properties with their bookings
        var properties = await db.Set<Property>()
            .Where(p => propertyIds.Contains(p.Id))
            .Select(p => new OwnerPortalPropertyInfo
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                Address = p.Address,
                TotalBookings = db.Set<Booking>()
                    .Count(b => b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled),
                TotalRevenue = db.Set<Booking>()
                    .Where(b => b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled)
                    .Sum(b => (decimal?)b.TotalPrice) ?? 0m,
                OccupancyRate = 0.0, // Placeholder for occupancy calculation
                RecentBookings = db.Set<Booking>()
                    .Where(b => b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled)
                    .OrderByDescending(b => b.CheckInDate)
                    .Take(10)
                    .Select(b => new OwnerPortalBookingInfo
                    {
                        BookingId = b.Id,
                        CheckInDate = b.CheckInDate,
                        CheckOutDate = b.CheckOutDate,
                        Channel = b.Source.ToString(),
                        TotalAmount = b.TotalPrice,
                        Status = b.Status
                        // Guest data masked: no names, emails, phones per ADR-011-R3
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return properties;
    }

    /// <summary>
    /// Gets published monthly statements for the owner's properties (ADR-011-R2).
    /// Returns empty list for now (statements feature not yet implemented).
    /// </summary>
    public Task<IReadOnlyList<OwnerPortalStatementInfo>> GetOwnerStatementsAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        // Placeholder: statements feature not yet implemented
        return Task.FromResult<IReadOnlyList<OwnerPortalStatementInfo>>([]);
    }

    /// <summary>
    /// Gets invoices received by the owner (ADR-011-R2).
    /// Returns empty list for now (invoices feature not yet implemented).
    /// </summary>
    public Task<IReadOnlyList<OwnerPortalInvoiceInfo>> GetOwnerInvoicesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        // Placeholder: invoices feature not yet implemented
        return Task.FromResult<IReadOnlyList<OwnerPortalInvoiceInfo>>([]);
    }

    /// <summary>
    /// Gets withholding tax summary for the owner (ADR-011-R2).
    /// Returns empty summary for now (withholding feature not yet implemented).
    /// </summary>
    public Task<OwnerPortalWithholdingInfo> GetOwnerWithholdingAsync(
        Guid ownerUserId,
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        var currentYear = year ?? timeProvider.GetUtcNow().Year;

        // Placeholder: withholding feature not yet implemented
        return Task.FromResult(new OwnerPortalWithholdingInfo
        {
            Year = currentYear,
            TotalWithheld = 0m,
            Details = []
        });
    }

    /// <summary>
    /// Verifies that the owner has access to the specified property (ADR-011-R2).
    /// Returns true if PropertyOwnership exists with active period, false otherwise.
    /// </summary>
    public async Task<bool> OwnerHasAccessToPropertyAsync(
        Guid ownerUserId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return await db.Set<PropertyOwnership>()
            .AnyAsync(po => po.OwnerUserId == ownerUserId
                         && po.PropertyId == propertyId
                         && po.OwnershipStart <= now
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= now),
                      cancellationToken);
    }
}

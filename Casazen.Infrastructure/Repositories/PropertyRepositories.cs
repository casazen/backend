using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class PropertyRepository(AppDbContext context) : IPropertyRepository
{
    public async Task<Property?> GetByIdAsync(Guid id)
    {
        return await context.Properties
            .Include(p => p.Bookings)
            .Include(p => p.OtaIntegrations)
            // The checkout derives the free refund deadline and the deferred payment from it (BK-07, A3-16).
            .Include(p => p.CancellationPolicy)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Property?> GetRecordAsync(Guid id)
    {
        return await context.Properties.FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<IEnumerable<Property>> GetByOwnerAsync(string ownerId)
    {
        return await context.Properties
            .Where(p => p.OwnerId == ownerId && p.IsActive)
            .ToListAsync();
    }

    public async Task<IEnumerable<Property>> GetByScopeAsync(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var query = context.Properties.Where(p => p.OrgId == scope.OrgId && p.IsActive);
        if (scope.OwnerId is { } ownerId)
            query = query.Where(p => p.OwnerId == ownerId);

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<IEnumerable<Property>> GetAllAsync()
    {
        return await context.Properties
            .Where(p => p.IsActive)
            .ToListAsync();
    }

    public async Task<IEnumerable<Property>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice)
    {
        return await GetSearchQueryable(
                new PublicPropertySearchCriteria { City = city, MinBedrooms = bedrooms, MaxPrice = maxPrice })
            .ToListAsync();
    }

    public IQueryable<Property> GetSearchQueryable(PublicPropertySearchCriteria criteria, Guid? orgId = null)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // The site of an inactive org does not resolve (OrgService.GetPublicBySlugAsync): its properties are not offered
        // either, otherwise a search result would lead to a "site not found" page (BK-20).
        var query = context.Properties.AsQueryable()
            .Where(PublicListing.IsPublished)
            .Where(p => p.Org.IsActive);

        if (orgId.HasValue)
            query = query.Where(p => p.OrgId == orgId.Value);

        if (!string.IsNullOrWhiteSpace(criteria.City))
        {
            var city = criteria.City.Trim().ToLower();
            query = query.Where(p => p.City.ToLower().Contains(city));
        }

        if (criteria.MinBedrooms.HasValue)
            query = query.Where(p => p.Bedrooms >= criteria.MinBedrooms.Value);

        if (criteria.MinBathrooms.HasValue)
            query = query.Where(p => p.Bathrooms >= criteria.MinBathrooms.Value);

        if (criteria.Guests.HasValue)
            query = query.Where(p => p.MaxGuests >= criteria.Guests.Value);

        if (criteria.MinPrice.HasValue)
            query = query.Where(p => p.NightlyRate >= criteria.MinPrice.Value);

        if (criteria.MaxPrice.HasValue)
            query = query.Where(p => p.NightlyRate <= criteria.MaxPrice.Value);

        return query;
    }

    public async Task<Property> AddAsync(Property property)
    {
        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property;
    }

    public async Task<Property> UpdateAsync(Property property)
    {
        context.Properties.Update(property);
        await context.SaveChangesAsync();
        return property;
    }

    public async Task<PropertySoftDeleteOutcome> SoftDeleteAsync(
        Guid id,
        DateTime deletedAtUtc,
        DateTime todayInRome,
        CancellationToken cancellationToken = default)
    {
        // Soft delete (PC-05, A2-18): the row stays for fiscal history (tourist tax, CIN, cedolare secca), just
        // excluded from every normal read by the SoftDelete query filter. Never Remove() it.
        // The property's booking lock (BookingRepository.AddAsync/UpdateAsync, BK-04) makes the "no stay to come"
        // check and the delete atomic with respect to a booking being created on the same property.
        var isPostgres = string.Equals(context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);
        await using var transaction = isPostgres && context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await BookingRepository.LockPropertyDatesAsync(context, id, cancellationToken);

        // The tenant and SoftDelete filters apply: another org's or an already deleted property is not found.
        var property = await context.Properties.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (property is null)
            return PropertySoftDeleteOutcome.NotFound;

        // Same bound as StayKpiRules.UpcomingCheckOut: a value stored on today's Rome date counts as today.
        var todayStart = RomeCalendar.StartOfDayUtc(todayInRome);
        var hasUpcomingStay = await context.Bookings.AnyAsync(
            b => b.PropertyId == id
                && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
                && b.CheckOutDate >= todayStart,
            cancellationToken);
        if (hasUpcomingStay)
            return PropertySoftDeleteOutcome.HasUpcomingStays;

        var hasActiveLease = await context.LeaseContracts.AnyAsync(
            l => l.PropertyId == id
                && l.Status != LeaseStatus.Draft
                && l.Status != LeaseStatus.Rejected
                && l.EndDate >= todayStart,
            cancellationToken);
        if (hasActiveLease)
            return PropertySoftDeleteOutcome.HasActiveLeases;

        property.IsDeleted = true;
        property.DeletedAt = deletedAtUtc;
        property.UpdatedAt = deletedAtUtc;
        await context.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return PropertySoftDeleteOutcome.Deleted;
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        return await context.Properties.AnyAsync(p => p.Id == id);
    }

    public async Task<Guid?> GetOrgIdAsync(Guid id)
    {
        return await context.Properties
            .Where(p => p.Id == id)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync();
    }

    public async Task<Property?> GetPropertyDetailAsync(Guid id)
    {
        return await context.Properties
            .Include(p => p.PropertyDocuments)
            .Include(p => p.OtaIntegrations)
            .Include(p => p.Bookings)
            .Include(p => p.PricingAdapterConfig)
            .FirstOrDefaultAsync(p => p.Id == id && p.IsActive);
    }

    public async Task<IEnumerable<Property>> GetByScopeForComplianceAsync(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var query = context.Properties.Where(p => p.OrgId == scope.OrgId);
        if (scope.OwnerId is { } ownerId)
            query = query.Where(p => p.OwnerId == ownerId);

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<bool> CinCodeExistsOnOtherPropertyAsync(string cinCode, Guid excludePropertyId)
    {
        return await context.Properties.AnyAsync(p =>
            p.CinCode == cinCode && p.Id != excludePropertyId);
    }

    public async Task<bool> SlugExistsInOrgAsync(Guid orgId, string slug, Guid? excludePropertyId = null)
    {
        var query = context.Properties.Where(p => p.OrgId == orgId && p.Slug == slug);
        if (excludePropertyId.HasValue)
            query = query.Where(p => p.Id != excludePropertyId.Value);
        return await query.AnyAsync();
    }

    public async Task<IReadOnlyList<CancellationPolicy>> GetCancellationPoliciesAsync()
    {
        return await context.CancellationPolicies
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<bool> CancellationPolicyExistsAsync(Guid id)
    {
        return await context.CancellationPolicies.AnyAsync(p => p.Id == id);
    }
}

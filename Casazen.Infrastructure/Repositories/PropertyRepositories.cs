using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
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

        return await ActivePropertiesInScope(scope).OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<IEnumerable<Property>> GetByScopeAsync(HostScope scope, RentalMode mode)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return await ActivePropertiesInScope(scope)
            .Where(p => p.RentalMode == mode)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    private IQueryable<Property> ActivePropertiesInScope(HostScope scope)
    {
        var query = context.Properties.Where(p => p.OrgId == scope.OrgId && p.IsActive).InScope(scope);

        return query;
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
        // The photo gallery is written only by PropertyPhotoService, under the property's photo lock (PC-04): a save of
        // the other fields, from a copy of the row read earlier, must never put back an older photo list.
        context.Entry(property).Property(p => p.PhotoUrls).IsModified = false;
        // The rental mode is written when the property is created and by the scheduled mode change (PM-01, PM-02), never
        // by a generic save: a copy of the row read earlier must not put back an older mode, and the update of the other
        // fields cannot skip the checks the mode change makes on the stays and the leases.
        context.Entry(property).Property(p => p.RentalMode).IsModified = false;
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

        // The same rule as the change of mode to short stays (PM-02), which asks it for the day chosen: LeaseOccupancy.
        var hasActiveLease = await context.LeaseContracts.AnyAsync(
            LeaseOccupancy.RunsOnOrAfter(id, todayStart),
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

        // Short-rent properties only (PM-01): a long-term property has no CIN obligation (D.L. 145/2023 is about short stays).
        var query = context.Properties.Where(PropertyRentalModeRules.IsShortRent).Where(p => p.OrgId == scope.OrgId).InScope(scope);

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

using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The site profile of a property (DB-11a). One read and one write of the property row; only the columns of the profile are written
/// (EF sends the ones that changed), so a photo uploaded at the same moment or a price changed in another tab is not overwritten. The
/// row is read through the tenant query filter: another org's property does not exist here. The texts are never written to the log.
/// </summary>
/// <remarks>
/// Since NF-31 the document also holds the texts of the listing in the other languages and who wrote each of them
/// (<see cref="ListingTextOrigins"/>): a save by a person gives them their origin from the document stored before, and the call of a machine
/// translation (<see cref="SaveAutoTranslationsAsync"/>) never overwrites a text a person wrote.
/// </remarks>
public class PropertySiteProfileService(
    AppDbContext db,
    ILogger<PropertySiteProfileService> logger,
    TimeProvider? timeProvider = null) : IPropertySiteProfileService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SitePropertyProfileView?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        return property is null ? null : SitePropertyProfileRules.ToView(property);
    }

    public async Task<SitePropertyProfileView?> SaveAsync(
        Guid propertyId,
        SitePropertyProfileInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        if (property is null)
            return null;

        // A property let for the long term has no page on the booking site: 422 like the checkout (property_not_bookable_in_long_mode).
        PropertyRentalModeRules.EnsureShortRent(property);

        var now = _clock.GetUtcNow();
        var previous = SitePropertyProfileJson.Parse(property.SiteProfileJson);
        var values = SitePropertyProfileRules.Normalize(input, property.CheckInFrom, _clock.TodayInRome().Year, previous);
        SitePropertyProfileRules.Apply(property, values);
        property.UpdatedAt = now.UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Site profile of property {PropertyId} of org {OrgId} saved", property.Id, property.OrgId);
        return SitePropertyProfileRules.ToView(property);
    }

    public async Task<SitePropertyProfileView?> SaveAutoTranslationsAsync(
        Guid propertyId,
        IReadOnlyDictionary<string, ListingTextsInput> translated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translated);

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        if (property is null)
            return null;

        PropertyRentalModeRules.EnsureShortRent(property);

        // Only the document is written: the columns of the profile and the rest of the property stay as they are.
        var document = SitePropertyProfileJson.Parse(property.SiteProfileJson);
        var merged = SitePropertyProfileRules.MergeAutoTranslations(document, translated);
        property.SiteProfileJson = SitePropertyProfileJson.Serialize(merged);
        property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Machine translations of property {PropertyId} of org {OrgId} saved", property.Id, property.OrgId);
        return SitePropertyProfileRules.ToView(property);
    }
}

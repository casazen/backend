using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertyOverviewService"/>
/// <remarks>
/// Nothing here decides anything: the blockers are the ones of the activation wizard
/// (<see cref="IPropertyComplianceStatusService.GetBlockingStepsManyAsync"/>, the same rules as for one property), the nights are
/// the Home's (<see cref="IHostDashboardService.GetLast30DaysOccupancyAsync"/>), the state and the site visibility are
/// <see cref="PropertyLifecycle"/>, which reads <see cref="PublicListing.IsPublished"/> itself. The feeds are read as the wizard reads
/// them: a feed with an import URL. Four queries for the whole page whatever the number of properties (activation: three, calendar
/// feeds: one) plus the two of the nights, and none when the page has no property in short-stay mode.
/// </remarks>
public sealed class PropertyOverviewService(
    AppDbContext db,
    IPropertyComplianceStatusService complianceStatus,
    IHostDashboardService dashboard) : IPropertyOverviewService
{
    public async Task<IReadOnlyList<PropertyOverview>> DescribeAsync(
        IReadOnlyList<Property> properties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Count == 0)
            return [];

        // A property in long-term mode has no activation, no site and no occupancy of the booking calendar (PM-01).
        var shortStay = properties.Where(PropertyRentalModeRules.IsShort).ToList();
        var steps = await complianceStatus.GetBlockingStepsManyAsync(shortStay, cancellationToken);
        var withFeed = await PropertiesWithAFeedAsync(shortStay, cancellationToken);

        // The occupancy is told of an activated property only: a pending or paused one has nothing to be occupied by.
        var activeIds = shortStay
            .Where(p => PropertyLifecycle.StatusOf(p) == PropertyLifecycleStatus.Active)
            .Select(p => p.Id)
            .ToList();
        var occupancy = activeIds.Count == 0
            ? new Dictionary<Guid, HostDashboardOccupancy>()
            : (await dashboard.GetLast30DaysOccupancyAsync(activeIds, cancellationToken)).ToDictionary(o => o.Key, o => o.Value);

        return properties.Select(property => PropertyRentalModeRules.IsShort(property)
            ? DescribeShortStay(property, steps[property.Id], withFeed.Contains(property.Id), occupancy)
            : new PropertyOverview(
                property.Id, null, null, null, property.PhotoUrls.Count, CoverOf(property), null, null)).ToList();
    }

    private static PropertyOverview DescribeShortStay(
        Property property,
        IReadOnlyList<ComplianceActivationStep> steps,
        bool hasFeed,
        IReadOnlyDictionary<Guid, HostDashboardOccupancy> occupancy)
    {
        var lifecycle = PropertyLifecycle.StatusOf(property);
        return new PropertyOverview(
            property.Id,
            lifecycle,
            PropertyLifecycle.MissingOf(property, steps, hasFeed),
            occupancy.TryGetValue(property.Id, out var nights) ? PercentOf(nights) : null,
            property.PhotoUrls.Count,
            CoverOf(property),
            PropertyLifecycle.VisibilityOf(property),
            lifecycle == PropertyLifecycleStatus.Suspended ? property.ComplianceSuspensionReasons ?? [] : null);
    }

    // The properties that have a calendar feed, as the wizard counts it (ComplianceWizardService, the «ical» step): a feed with an
    // import URL. One query for the page.
    private async Task<HashSet<Guid>> PropertiesWithAFeedAsync(
        IReadOnlyList<Property> properties,
        CancellationToken cancellationToken)
    {
        if (properties.Count == 0)
            return [];

        var ids = properties.Select(p => p.Id).ToList();
        return (await db.PropertyICalFeeds
                .AsNoTracking()
                .Where(f => ids.Contains(f.PropertyId) && f.ImportUrl != null && f.ImportUrl != "")
                .Select(f => f.PropertyId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    // Whole percent of the nights available, half away from zero (62.5 is 63, as the Home's percentage reads); nothing when no
    // night was available.
    private static int? PercentOf(HostDashboardOccupancy nights) =>
        nights.Rate is { } rate ? (int)Math.Round(rate * 100m, MidpointRounding.AwayFromZero) : null;

    private static string? CoverOf(Property property) => property.PhotoUrls.Count > 0 ? property.PhotoUrls[0] : null;
}

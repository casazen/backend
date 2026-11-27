using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IPublicAvailabilityService
{
    Task<PublicAvailability> GetAsync(
        Guid propertyId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default);
}

public sealed record PublicAvailability(
    Guid PropertyId,
    DateTime FromDate,
    DateTime ToDate,
    IReadOnlyList<DateTime> BookedNights,
    int? MinNights = null)
{
    public const int MaxRangeDays = 366;
}

public static class PublicAvailabilityErrorCodes
{
    public const string PropertyNotFound = "public_property_not_found";

    public const string InvalidRange = "availability_range_invalid";
}

public static class PublicListing
{
    /// <summary>
    /// Published: in short-rent mode (<see cref="RentalMode.Short"/>, PM-01: a long-term property is never published, it
    /// has no booking site, no search result, no availability, no SEO page and no sitemap entry), active, not paused, with
    /// the compliance activated (<see cref="PropertyComplianceStatus.Active"/>) <b>and</b> shown on the site by its host
    /// (<see cref="Property.PublishedOnSite"/>, SR-07a, true unless the host switched it off).
    /// The public search, the property page, the availability, the SEO pages and the sitemaps, and the activation
    /// checklist use this rule; the checkout checks the same condition. A <see cref="PropertyComplianceStatus.Suspended"/>
    /// property (a requirement lost after the activation, CO-06) is not published, like a pending one. A paused property
    /// (PC-03, A2-05) is hidden here too, but stays visible and editable to its own host — pausing is reversible and never
    /// deletes or archives it. The host's switch only ever takes a property off the site: it is one more condition added to
    /// the others, so turning it on cannot publish a property that is pending, suspended or paused. The mode comparison is
    /// the one of <see cref="PropertyRentalModeRules.IsShortRent"/>.
    /// </summary>
    public static Expression<Func<Property, bool>> IsPublished { get; } =
        p => p.RentalMode == RentalMode.Short
            && p.IsActive && !p.IsPaused && p.ComplianceStatus == PropertyComplianceStatus.Active
            && p.PublishedOnSite;

    private static readonly Func<Property, bool> IsPublishedCompiled = IsPublished.Compile();

    public static bool Includes(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return IsPublishedCompiled(property);
    }
}

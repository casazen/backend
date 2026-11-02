using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Availability of a property on the public booking site (BK-05, A3-09, A2-13, A9-39): the nights already taken, read
/// with the same rule the booking checks use (<see cref="PropertyOccupancy"/>, <see cref="CheckoutHolds.OccupiesDates"/>):
/// confirmed bookings, pending bookings and checkout holds within their time, "pay at the property" requests waiting for
/// the host, imported (iCal) and manual calendar blocks. Expired holds are left out. Only dates: no guest, no source.
/// </summary>
public interface IPublicAvailabilityService
{
    /// <summary>
    /// The nights of <paramref name="propertyId"/> taken from <paramref name="fromDate"/> (included) to
    /// <paramref name="toDate"/> (excluded). Read only: nothing is cancelled or written.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <see cref="PublicAvailabilityErrorCodes.PropertyNotFound"/>: the property does not exist or is not published
    /// (<see cref="PublicListing.IsPublished"/>).
    /// </exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="PublicAvailabilityErrorCodes.InvalidRange"/>: <paramref name="toDate"/> is not after
    /// <paramref name="fromDate"/>, or the range is longer than <see cref="PublicAvailability.MaxRangeDays"/> days.
    /// </exception>
    Task<PublicAvailability> GetAsync(
        Guid propertyId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Taken nights of a property, as calendar dates (midnight UTC), in order and without duplicates. <paramref name="MinNights"/>
/// is the minimum stay of the property (DB-03), <c>null</c> for none: a free night can be booked as part of a stay of at least
/// that many nights (the quote and the checkout refuse a shorter one, 422 <c>direct_booking_min_nights_not_met</c>), so a
/// calendar built from this answer can grey out a selection that is too short without asking the quote.
/// </summary>
public sealed record PublicAvailability(
    Guid PropertyId,
    DateTime FromDate,
    DateTime ToDate,
    IReadOnlyList<DateTime> BookedNights,
    int? MinNights = null)
{
    /// <summary>Longest range one request may ask for: one year of the booking calendar.</summary>
    public const int MaxRangeDays = 366;
}

/// <summary>Stable <c>code</c> values of the public availability errors (FD-05): never rename one.</summary>
public static class PublicAvailabilityErrorCodes
{
    /// <summary>404: the property does not exist, is not active or is not published on the booking site.</summary>
    public const string PropertyNotFound = "public_property_not_found";

    /// <summary>422: the end of the range is not after its start, or the range is longer than one year.</summary>
    public const string InvalidRange = "availability_range_invalid";
}

/// <summary>Properties shown on the public booking site and bookable there.</summary>
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

    /// <summary>
    /// True when <paramref name="property"/> is published (<see cref="IsPublished"/>): the very same expression, for the code that
    /// already holds the entity (the checkout, the state of a property in the host's list), so none of them copies the rule.
    /// </summary>
    public static bool Includes(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return IsPublishedCompiled(property);
    }
}

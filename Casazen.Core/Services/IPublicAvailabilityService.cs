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

/// <summary>Taken nights of a property, as calendar dates (midnight UTC), in order and without duplicates.</summary>
public sealed record PublicAvailability(Guid PropertyId, DateTime FromDate, DateTime ToDate, IReadOnlyList<DateTime> BookedNights)
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
    /// Published: active and with the compliance activated (<see cref="PropertyComplianceStatus.Active"/>). The public
    /// search, the property page and the availability use this rule; the checkout checks the same condition. A
    /// <see cref="PropertyComplianceStatus.Suspended"/> property (a requirement lost after the activation, CO-06) is not
    /// published, like a pending one.
    /// </summary>
    public static Expression<Func<Property, bool>> IsPublished { get; } =
        p => p.IsActive && p.ComplianceStatus == PropertyComplianceStatus.Active;
}

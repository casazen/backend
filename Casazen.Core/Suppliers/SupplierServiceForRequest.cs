using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A service of a supplier's catalog as a service request needs it (SP-04): the name to keep on the request, the category it
/// must match, the duration and the rules the slot planner applies to it, and the price known in advance. Read by
/// <see cref="Services.ISupplierServiceCatalogService.FindForRequestAsync"/>; nothing of the public content of the service
/// (photos, supplements, descriptions) is here.
/// </summary>
/// <param name="Slug">
/// The public slug of the service (SP-11): the name the customer's page asks the free slots with
/// (<c>GET api/public/suppliers/{slug}/slots?service=</c>) when it moves its booking to another time.
/// </param>
/// <param name="UrgentEnabled">The service's own switch for urgent requests (SP-04c); the supplier's switch is the planner's.</param>
public sealed record SupplierServiceForRequest(
    Guid Id,
    string Name,
    string Category,
    SupplierServiceListingStatus Status,
    int? DurationMinutes,
    int? MinNoticeHours,
    int WeekdaysMask,
    int? PriceFromCents,
    SupplierServicePriceUnit PriceUnit,
    bool RequiresQuote,
    string Slug,
    bool UrgentEnabled = false)
{
    /// <summary>True for a published service: the only kind a request can be made for.</summary>
    public bool IsRequestable => Status == SupplierServiceListingStatus.Active;

    /// <summary>
    /// The price a request for this service can carry from the start: the "from" price of a service priced per job. A service on
    /// quote, or priced per hour, set or square meter, has no price until the supplier gives one (the quantity is unknown).
    /// </summary>
    public int? EstimatedAmountCents => EstimateOf(RequiresQuote, PriceUnit, PriceFromCents);

    /// <summary>
    /// The estimate rule for any view of a catalog service (the host's list of a supplier's services reads it too, SR-08a): the "from" price
    /// of a service priced per job and not on quote; none otherwise.
    /// </summary>
    public static int? EstimateOf(bool requiresQuote, SupplierServicePriceUnit priceUnit, int? priceFromCents) =>
        !requiresQuote && priceUnit == SupplierServicePriceUnit.PerJob && priceFromCents is > 0 ? priceFromCents : null;

    /// <summary>
    /// The slot query of the planner for this service (<c>null</c> when it has no duration, so no slot can be computed). With
    /// <paramref name="urgent"/> the query asks for the urgent slots (SP-04c): the supplier's preview asks only when
    /// <see cref="UrgentEnabled"/> is on.
    /// </summary>
    public SupplierSlotQuery? ToSlotQuery(bool urgent = false) =>
        DurationMinutes is > 0 ? new SupplierSlotQuery(DurationMinutes.Value, MinNoticeHours, WeekdaysMask, urgent) : null;
}

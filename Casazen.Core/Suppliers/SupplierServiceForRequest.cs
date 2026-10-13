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
    string Slug)
{
    /// <summary>True for a published service: the only kind a request can be made for.</summary>
    public bool IsRequestable => Status == SupplierServiceListingStatus.Active;

    /// <summary>
    /// The price a request for this service can carry from the start: the "from" price of a service priced per job. A service on
    /// quote, or priced per hour, set or square meter, has no price until the supplier gives one (the quantity is unknown).
    /// </summary>
    public int? EstimatedAmountCents =>
        !RequiresQuote && PriceUnit == SupplierServicePriceUnit.PerJob && PriceFromCents is > 0 ? PriceFromCents : null;

    /// <summary>The slot query of the planner for this service (<c>null</c> when it has no duration, so no slot can be computed).</summary>
    public SupplierSlotQuery? ToSlotQuery() =>
        DurationMinutes is > 0 ? new SupplierSlotQuery(DurationMinutes.Value, MinNoticeHours, WeekdaysMask) : null;
}

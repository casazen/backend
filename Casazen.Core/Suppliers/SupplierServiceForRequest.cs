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
    public bool IsRequestable => Status == SupplierServiceListingStatus.Active;

    public int? EstimatedAmountCents => EstimateOf(RequiresQuote, PriceUnit, PriceFromCents);

    /// <summary>
    /// The estimate rule for any view of a catalog service (the host's list of a supplier's services reads it too, SR-08a): the "from" price
    /// of a service priced per job and not on quote; none otherwise.
    /// </summary>
    public static int? EstimateOf(bool requiresQuote, SupplierServicePriceUnit priceUnit, int? priceFromCents) =>
        !requiresQuote && priceUnit == SupplierServicePriceUnit.PerJob && priceFromCents is > 0 ? priceFromCents : null;

    public SupplierSlotQuery? ToSlotQuery(bool urgent = false) =>
        DurationMinutes is > 0 ? new SupplierSlotQuery(DurationMinutes.Value, MinNoticeHours, WeekdaysMask, urgent) : null;
}

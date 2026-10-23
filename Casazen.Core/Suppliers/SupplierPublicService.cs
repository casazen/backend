using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A published service of an active supplier as the public may know it (SP-09): the content of the page and the terms the
/// estimate and the slots are computed from. <b>The only shape in which the catalog leaves the supplier's console</b>: it has
/// no id, no org, no status, no version, no position and no timestamp, so a public read cannot return what the type does not
/// carry. Built by <see cref="Services.ISupplierServiceCatalogService.ListPublicAsync"/> and
/// <see cref="Services.ISupplierServiceCatalogService.FindPublicAsync"/>, which return only the <c>Active</c> services, not
/// deleted, of a supplier that is itself <c>Active</c>.
/// </summary>
/// <param name="Slug">Last part of <c>/fornitori/{supplierSlug}/servizi/{slug}</c>; unique among the supplier's services.</param>
/// <param name="PriceFromCents">The "from" price in euro cents; <c>null</c> is "on quote".</param>
/// <param name="PriceUnit">What the price is for (job, hour, set, square meter).</param>
/// <param name="PricesIncludeVat">As the supplier declared it (decision D4): false means nothing is promised about VAT.</param>
/// <param name="RequiresQuote">The customer waits for the supplier's offer: no estimate is given.</param>
/// <param name="DurationMinutes">Indicative duration; also the length of a slot.</param>
/// <param name="Supplements">The structured supplements the estimate adds up.</param>
/// <param name="MinNoticeHours">Planning only (not shown): the service's own notice; <c>null</c> is the supplier's.</param>
/// <param name="WeekdaysMask">Planning only (not shown): the weekdays the service is offered on.</param>
/// <param name="UrgentEnabled">
/// The service's own switch for urgent requests (SP-04c), as the supplier saved it. Not shown: the public learns of it only through
/// <see cref="Urgent"/>, which also takes the supplier's switch into account.
/// </param>
/// <param name="UrgentSurchargeCents">The service's own urgent surcharge in cents; <c>null</c> is the supplier's default. Not shown.</param>
public sealed record SupplierPublicService(
    string Slug,
    string Name,
    string Category,
    string? Summary,
    string? Description,
    int? PriceFromCents,
    SupplierServicePriceUnit PriceUnit,
    bool PricesIncludeVat,
    bool RequiresQuote,
    int? DurationMinutes,
    IReadOnlyList<SupplierServiceSupplement> Supplements,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> PhotoUrls,
    int? MinNoticeHours,
    int WeekdaysMask,
    bool UrgentEnabled = false,
    int? UrgentSurchargeCents = null)
{
    /// <summary>
    /// What the public may know about urgent requests for this service (SP-04c): on only when the service <b>and</b> the supplier
    /// accept them, with the surcharge that would be added. The catalog does not know the supplier's settings, so it leaves
    /// <see cref="SupplierUrgentTerms.Off"/>; whoever does (the showcase service, the booking) sets it with
    /// <see cref="WithUrgent"/> before the service is shown, estimated or booked.
    /// </summary>
    public SupplierUrgentTerms Urgent { get; init; } = SupplierUrgentTerms.Off;

    /// <summary>This service with the urgent terms its own switch and surcharge give next to the supplier's settings (<see cref="SupplierUrgentRules.Effective"/>).</summary>
    public SupplierPublicService WithUrgent(SupplierUrgentSettings supplier) =>
        this with { Urgent = SupplierUrgentRules.Effective(UrgentEnabled, UrgentSurchargeCents, supplier) };

    /// <summary>
    /// The slot query of the planner for this service; <c>null</c> when it has no duration (a published service always has
    /// one, so this is a guard, not a case). With <paramref name="urgent"/> the query asks for the urgent slots (SP-04c): the caller
    /// asks only when <see cref="Urgent"/> is on.
    /// </summary>
    public SupplierSlotQuery? ToSlotQuery(bool urgent = false) =>
        DurationMinutes is > 0 ? new SupplierSlotQuery(DurationMinutes.Value, MinNoticeHours, WeekdaysMask, urgent) : null;
}

/// <summary>
/// A published service of an active supplier with the id of the catalog row, for the one use that needs it (SP-10): the booking
/// keeps the id on the request it creates (<c>ServiceRequest.ServiceListingId</c>). The public reads never carry the id
/// (<see cref="SupplierPublicService"/> has none); this is found by the same statement as the public service, with the same
/// rules (<c>Active</c> service of an <c>Active</c> supplier, not deleted), and is handed only to the booking service.
/// </summary>
public sealed record SupplierBookableService(Guid ListingId, SupplierPublicService Service);

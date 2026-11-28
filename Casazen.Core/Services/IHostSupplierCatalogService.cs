using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// One published service of a supplier as the host reads it to ask for an intervention (SR-08a): enough to choose it, show its price and
/// send the request. Nothing of the public content of the service (texts, photos, supplements, slug) and nothing private.
/// </summary>
/// <param name="Id">The catalog row: <c>serviceListingId</c> of the request.</param>
/// <param name="DurationMinutes">Indicative duration; <c>null</c> when the supplier gave none (then no slot can be computed).</param>
/// <param name="PriceFromCents">The "from" price in euro cents; <c>null</c> is "on quote".</param>
/// <param name="RequiresQuote">The host waits for the supplier's offer: the price is confirmed before the work.</param>
/// <param name="EstimatedAmountCents">
/// The price a request for this service starts with (<see cref="SupplierServiceForRequest.EstimatedAmountCents"/>): the "from" price of a
/// service priced per job; <c>null</c> for one on quote or priced per hour, set or square meter.
/// </param>
public sealed record HostSupplierService(
    Guid Id,
    string Name,
    string Category,
    int? DurationMinutes,
    int? PriceFromCents,
    SupplierServicePriceUnit PriceUnit,
    bool RequiresQuote,
    int? EstimatedAmountCents);

public sealed record HostSupplierSlots(
    Guid ServiceId,
    int? DurationMinutes,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<SupplierSlot> Slots);

public static class HostSupplierSlotLimits
{
    public const int Days = 14;
}

/// <summary>
/// What the host reads of a supplier to ask it for an intervention (SR-08a): its published services and the free slots of one of them.
/// The two reads change nothing. The slots are computed by <see cref="SupplierSlotPlanner"/>, with the very query the creation of a
/// request checks the chosen time with, so a slot offered here is one that <c>POST api/service-requests</c> accepts (unless someone else
/// takes it first: 409 <c>supplier_slot_unavailable</c>).
/// </summary>
/// <remarks>
/// Both reads are for a property of the host's org and a supplier that is <see cref="SupplierStatus.Active"/> and covers the comune of the
/// property, with the codes the creation of a request uses: 404 <c>property_not_found</c>, <c>supplier_not_found</c>,
/// <c>supplier_service_not_found</c>; 422 <c>service_request_supplier_inactive</c>, <c>service_request_supplier_outside_comune</c>,
/// <c>service_request_service_unavailable</c>, <c>invalid_service_category</c>. The authorization of the property (read, scope) is the
/// caller's.
/// </remarks>
public interface IHostSupplierCatalogService
{
    Task<IReadOnlyList<HostSupplierService>> ListServicesAsync(
        Guid hostOrgId,
        Guid propertyId,
        Guid supplierOrgId,
        string? category,
        CancellationToken cancellationToken = default);

    Task<HostSupplierSlots> GetSlotsAsync(
        Guid hostOrgId,
        Guid propertyId,
        Guid supplierOrgId,
        Guid serviceId,
        DateOnly? date,
        CancellationToken cancellationToken = default);
}

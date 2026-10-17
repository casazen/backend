using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>Whether a comune is one of the supplier's zones (decision D10: zones are comuni, the postal code does not decide).</summary>
/// <param name="Inside">True when the supplier covers the comune.</param>
/// <param name="Comune">The comune as the caller wrote it (an ISTAT code or a name), trimmed.</param>
public sealed record SupplierPreviewCoverage(bool Inside, string Comune);

/// <summary>
/// The free slots of one service as the supplier's own customers would see them (SP-03b, <c>GET api/supplier/preview/slots</c>).
/// </summary>
/// <param name="ServiceId">The catalog service the slots are for.</param>
/// <param name="ServiceSlug">Its public slug (the one the public page asks the slots with).</param>
/// <param name="ServiceStatus">Draft, published or paused: the supplier previews a service that is not public yet.</param>
/// <param name="DurationMinutes">How long the work lasts (a slot is that long); 0 for a draft that has no duration yet (then no day is computed).</param>
/// <param name="BookableUntil">The last Europe/Rome day that can be booked: today plus the supplier's horizon.</param>
/// <param name="Days">One entry for every day of the window, in date order; the days beyond the horizon are not listed.</param>
/// <param name="Coverage">Only when the caller asked about a comune.</param>
public sealed record SupplierSlotPreview(
    Guid ServiceId,
    string ServiceSlug,
    SupplierServiceListingStatus ServiceStatus,
    int DurationMinutes,
    DateOnly BookableUntil,
    IReadOnlyList<PublicSlotDay> Days,
    SupplierPreviewCoverage? Coverage);

/// <summary>
/// The supplier's preview "how customers see you" (SP-03b): the slots a customer would be offered for one of the supplier's
/// services, computed by the same planner as the public page (<see cref="ISupplierAgendaService.PlanAsync(Guid, DateOnly, DateOnly, Suppliers.SupplierSlotQuery, CancellationToken)"/>)
/// and cut by the same window (<see cref="Suppliers.PublicSlotWindow"/>), <b>but</b> for the supplier alone: no cache (a change of
/// hours shows at once), no condition on the service being published or the supplier being active (a draft and a paused service
/// can be tried), and behind the supplier's own authorization, not the public flag.
/// </summary>
public interface ISupplierSlotPreviewService
{
    /// <summary>
    /// The slots of the service <paramref name="serviceId"/> from <paramref name="from"/> (never before today) for
    /// <paramref name="days"/> days (1 to 62, 14 when left out), cut at the supplier's horizon, and, when <paramref name="comune"/> is
    /// given, whether it is one of the supplier's zones. Read-only.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <c>supplier_service_not_found</c>: not a service of this supplier (another's, or deleted: the answer is the same).
    /// </exception>
    Task<SupplierSlotPreview> GetAsync(
        Guid supplierOrgId,
        Guid serviceId,
        DateOnly? from,
        int? days,
        string? comune,
        CancellationToken cancellationToken = default);
}

using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// What the extended public page adds to the showcase of a supplier (SP-09): the published services and, only once it is
/// measured, the typical time to answer.
/// </summary>
/// <param name="Services">The supplier's <c>Active</c> services, by position then creation date.</param>
/// <param name="MedianResponseMinutes">
/// The median time from a request to its take, in minutes (<see cref="SupplierResponseTimes"/>); <c>null</c> until there are
/// <see cref="PublicShowcaseLimits.ResponseTimeMinSamples"/> answered requests in the last 90 days. Never a configured value.
/// </param>
public sealed record PublicSupplierExtension(IReadOnlyList<SupplierPublicService> Services, int? MedianResponseMinutes);

/// <summary>
/// What the published content of a supplier adds to its public page (SP-13a): the published content (never the draft), whether the supplier
/// paused the showcase, whether it takes bookings, and the few facts that are measured or checked, never written by hand.
/// </summary>
/// <param name="Content">The published content document.</param>
/// <param name="Paused">The supplier paused the showcase: the page says it takes no bookings.</param>
/// <param name="BookingsOpen">
/// The derived rule <c>published ∧ not paused ∧ OnlineBookingEnabled ∧ payments account ready ∧ payments flag on</c> (decision VT-D7).
/// </param>
/// <param name="Verified">"Verificato" (decision D11): an active profile, a VAT number and a Stripe account that can take charges and payouts.</param>
/// <param name="JobsDone">The jobs done with CasaZen, a true count, only from <see cref="ShowcaseContentLimits.JobsDoneMinToShow"/>; otherwise <c>null</c>.</param>
/// <param name="RespondWithinMinutes">How long the supplier has to answer a booking: its setting (180 by default), never written on the page by hand.</param>
/// <param name="AcceptsOnlinePayments">The payments are on and the supplier's account can take charges and payouts.</param>
/// <param name="PrivacyNoticeVersion">The version of the privacy notice a booking records (<c>Suppliers:Showcase:PrivacyNoticeVersion</c>), or <c>null</c> when not configured.</param>
public sealed record PublishedShowcasePage(
    ShowcaseContentDocument Content,
    bool Paused,
    bool BookingsOpen,
    bool Verified,
    int? JobsDone,
    int RespondWithinMinutes,
    bool AcceptsOnlinePayments,
    string? PrivacyNoticeVersion);

public sealed record PublicSlotDay(DateOnly Date, IReadOnlyList<SupplierSlot> Slots)
{
    public bool Available => Slots.Count > 0;
}

/// <summary>
/// The free slots of a service for a window of days (SP-09, <c>GET api/public/suppliers/{slug}/slots</c>), as the planner of
/// the supplier's agenda computes them (SP-03). A slot shown is not a promise: the booking recomputes under the calendar lock.
/// </summary>
/// <param name="ServiceSlug">The service the slots are for.</param>
/// <param name="DurationMinutes">How long the work lasts (a slot is that long).</param>
/// <param name="BookableUntil">The last Europe/Rome day that can be booked: today plus the supplier's horizon.</param>
/// <param name="Days">One entry for every day of the window, in date order; the days beyond the horizon are not listed.</param>
/// <param name="Urgent">
/// The slots are the urgent ones (SP-04c): today and tomorrow only, in the supplier's urgent window. Then <paramref name="BookableUntil"/>
/// is tomorrow, and a service or a supplier that does not offer urgent requests has no day at all.
/// </param>
public sealed record PublicSlots(
    string ServiceSlug,
    int DurationMinutes,
    DateOnly BookableUntil,
    IReadOnlyList<PublicSlotDay> Days,
    bool Urgent = false);

public enum PublicQuoteCoverage
{
    Unknown = 0,

    Covered = 1,

    Outside = 2,
}

public sealed record PublicQuote(
    SupplierPublicService Service,
    PublicQuoteCoverage Coverage,
    SupplierQuote Quote,
    string? PostalCode);

/// <summary>
/// The anonymous read side of a supplier's showcase (SP-09): the extended page, the services, the free slots and the price
/// estimate. Nothing here needs an account and nothing here carries a person: only what the supplier published.
/// </summary>
/// <remarks>
/// <para><b>Who is visible.</b> Only a supplier whose profile is <c>Active</c>, looked up by the lowercase showcase slug.
/// Unknown, pending and suspended answer the same (<c>null</c>): the page says "does not exist" for all three, and nothing
/// observable tells them apart. Every other method takes the supplier this one found.</para>
/// <para><b>Tenancy.</b> The tables of the catalog and of the agenda are keyed by the supplier org and not tenant-filtered:
/// every read goes through <see cref="ISupplierServiceCatalogService"/> and <see cref="ISupplierAgendaService"/>, whose
/// statements carry the explicit <c>OrgId</c> predicate (and, for the catalog, the supplier's <c>Active</c> status).</para>
/// <para><b>Slots.</b> Computed by <see cref="SupplierSlotPlanner"/> through <see cref="ISupplierAgendaService.PlanAsync(Guid, DateOnly, DateOnly, SupplierSlotQuery, CancellationToken)"/>,
/// so whatever takes the supplier's time (requests with hours, blocks, calendar engagements, and the holds of SP-10, added to the
/// planning input) is already in. Cached for 30 seconds per replica: a slot shown is not a promise.</para>
/// </remarks>
public interface IPublicSupplierShowcaseService
{
    Task<SupplierProfile?> FindActiveSupplierAsync(string? slug, CancellationToken cancellationToken = default);

    Task<PublishedShowcasePage?> GetPublishedPageAsync(SupplierProfile supplier, CancellationToken cancellationToken = default);

    Task<bool> IsPausedAsync(SupplierProfile supplier, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, DateTime>> GetNextSlotsAsync(
        SupplierProfile supplier,
        IReadOnlyList<SupplierPublicService> services,
        CancellationToken cancellationToken = default);

    Task<PublicSupplierExtension> GetExtensionAsync(SupplierProfile supplier, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierPublicService>> ListServicesAsync(SupplierProfile supplier, CancellationToken cancellationToken = default);

    Task<SupplierPublicService?> FindServiceAsync(SupplierProfile supplier, string? serviceSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// The free slots of a service from <paramref name="from"/> (default and never before today, Europe/Rome) for
    /// <paramref name="days"/> days (default <see cref="PublicShowcaseLimits.SlotsDefaultDays"/>, at most
    /// <see cref="PublicShowcaseLimits.SlotsMaxDays"/>), cut at the supplier's horizon. <c>null</c> when the service is not one
    /// of the supplier's published ones. Read-only. With <paramref name="urgent"/> (SP-04c) the urgent slots instead: today and
    /// tomorrow, in the supplier's urgent window, 30 minutes from now; no day at all when the service or the supplier does not accept
    /// urgent requests. Without it the slots every caller got before, from the same plan and the same cache.
    /// </summary>
    Task<PublicSlots?> GetSlotsAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        DateOnly? from,
        int? days,
        bool urgent = false,
        CancellationToken cancellationToken = default);

    Task<PublicQuote?> QuoteAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        SupplierQuoteRequest request,
        CancellationToken cancellationToken = default);
}

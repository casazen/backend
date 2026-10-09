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
/// The free slots of one Europe/Rome day. <b>No reason, no kind, no label</b>: a day with no slot is just "not available"
/// (closed, on leave, full, inside the notice and beyond the horizon look the same to the public), so a customer can read
/// neither why a supplier is not working nor what it is doing.
/// </summary>
public sealed record PublicSlotDay(DateOnly Date, IReadOnlyList<SupplierSlot> Slots)
{
    /// <summary>True when the day has at least one free slot.</summary>
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
public sealed record PublicSlots(string ServiceSlug, int DurationMinutes, DateOnly BookableUntil, IReadOnlyList<PublicSlotDay> Days);

/// <summary>Whether the place of an estimate is one of the supplier's zones.</summary>
public enum PublicQuoteCoverage
{
    /// <summary>No place was given: nothing is known, nothing is refused.</summary>
    Unknown = 0,

    /// <summary>The comune is one of the supplier's zones.</summary>
    Covered = 1,

    /// <summary>The comune is not one of the supplier's zones (or could not be recognized): the supplier decides.</summary>
    Outside = 2,
}

/// <summary>An estimate with the service it is for and where the work is.</summary>
/// <param name="Service">The published service the estimate is for.</param>
/// <param name="Coverage">Whether the place is in the supplier's zones.</param>
/// <param name="Quote">The estimate itself.</param>
/// <param name="PostalCode">The postal code the customer gave, echoed (it does not decide the coverage).</param>
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
/// so whatever takes the supplier's time (requests with hours, blocks, calendar engagements, and the holds SP-10 adds to the
/// planning input) is already in. Cached for 30 seconds per replica: a slot shown is not a promise.</para>
/// </remarks>
public interface IPublicSupplierShowcaseService
{
    /// <summary>
    /// The <c>Active</c> supplier whose showcase slug is <paramref name="slug"/> (trimmed, lowercase); <c>null</c> for an unknown
    /// slug and for a supplier that is pending or suspended, with no way to tell which. Read-only.
    /// </summary>
    Task<SupplierProfile?> FindActiveSupplierAsync(string? slug, CancellationToken cancellationToken = default);

    /// <summary>The services and the measured response time to add to the showcase of <paramref name="supplier"/>. Read-only.</summary>
    Task<PublicSupplierExtension> GetExtensionAsync(SupplierProfile supplier, CancellationToken cancellationToken = default);

    /// <summary>The <c>Active</c> services of <paramref name="supplier"/>, by position then creation date. Read-only.</summary>
    Task<IReadOnlyList<SupplierPublicService>> ListServicesAsync(SupplierProfile supplier, CancellationToken cancellationToken = default);

    /// <summary>
    /// One <c>Active</c> service of <paramref name="supplier"/> by its slug; <c>null</c> for an unknown slug, a draft, a paused
    /// or a deleted service and a service of another supplier, with no way to tell which. Read-only.
    /// </summary>
    Task<SupplierPublicService?> FindServiceAsync(SupplierProfile supplier, string? serviceSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// The free slots of a service from <paramref name="from"/> (default and never before today, Europe/Rome) for
    /// <paramref name="days"/> days (default <see cref="PublicShowcaseLimits.SlotsDefaultDays"/>, at most
    /// <see cref="PublicShowcaseLimits.SlotsMaxDays"/>), cut at the supplier's horizon. <c>null</c> when the service is not one
    /// of the supplier's published ones. Read-only.
    /// </summary>
    Task<PublicSlots?> GetSlotsAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        DateOnly? from,
        int? days,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The price estimate of a service (<see cref="SupplierQuoteCalculator"/>) for what the customer chose, with the check of
    /// the place against the supplier's zones. <c>null</c> when the service is not one of the supplier's published ones. An
    /// estimate that cannot be given (on quote, outside the zones) is a result, not an error. Read-only.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="SupplierQuoteErrors.Invalid"/> (<see cref="SupplierQuoteRuleException"/>, with the fields): a value that does not fit the service.
    /// </exception>
    Task<PublicQuote?> QuoteAsync(
        SupplierProfile supplier,
        string? serviceSlug,
        SupplierQuoteRequest request,
        CancellationToken cancellationToken = default);
}

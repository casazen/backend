using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierConflictService"/>
/// <remarks>
/// <para><b>What it reads.</b> The requests of the range with their hours and catalog service
/// (<see cref="ISupplierServiceRequestReader.ListForAgendaAsync"/>), the planning input of the agenda for the range and for the days
/// after it where a time can be suggested
/// (<see cref="ISupplierAgendaService.BuildPlanningInputAsync(Guid, DateOnly, DateOnly, CancellationToken)"/>: the same rules, hours,
/// time off, blocks, engagements of the calendar feed, requests and bookings waiting as the slot planner reads) and, only when a
/// request still waiting for an answer is for a catalog service, that service's notice and weekdays (a suggested time must be one
/// <c>propose-time</c> accepts). It never names a table of the agenda or of the catalog: the guards of
/// <c>SupplierAgendaTenancyTests</c> and <c>SupplierServiceListingTenancyTests</c> need no new file.</para>
/// <para><b>Order of the reads.</b> The jobs to check are read <i>before</i> the planning input, and nothing is in a transaction:
/// a booking that is confirmed in between is then a request of the input that was not looked at yet (it is checked by the next call),
/// never a request looked at beside the hold it just replaced. The requests of the range are read twice (the input reads them again
/// with the days around the range); the second read is the price of leaving the interface of the agenda as it is.</para>
/// <para><b>Cost.</b> One read of the jobs, the planning input (seven statements), at most one read of the catalog, and one pure
/// pass over at most <see cref="SupplierAgendaLimits.MaxConflictRequests"/> jobs of at most 62 days of one supplier (a suggestion
/// plans at most <see cref="SupplierAgendaLimits.ConflictSuggestionDays"/> + 1 days in memory).</para>
/// </remarks>
public sealed class SupplierConflictService(
    ISupplierAgendaService agenda,
    ISupplierServiceRequestReader requests,
    ISupplierServiceCatalogService catalog) : ISupplierConflictService
{
    public async Task<SupplierConflictReport> GetConflictsAsync(
        Guid supplierOrgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        SupplierAgendaRange.Ensure(from, to, SupplierAgendaLimits.MaxCalendarDays);

        var listed = await requests.ListForAgendaAsync(supplierOrgId, from, to, cancellationToken);

        // The planner's input reaches past the range: a suggestion looks for a free slot in the days after the job.
        var input = await agenda.BuildPlanningInputAsync(
            supplierOrgId,
            from,
            to.AddDays(SupplierAgendaLimits.ConflictSuggestionDays),
            cancellationToken);

        // Which of them the detector looks at is its own rule (a job with hours, still to come); the bound is applied to those, the
        // nearest first, so what is furthest away is cut.
        var nowUtc = UtcDateTime.Normalize(input.NowUtc);
        var upcoming = listed
            .Where(request => request is { HasHours: true })
            .Select(request => (Request: request, Job: new SupplierConflictRequest(request.Id, request.Status, request.StartUtc!.Value, request.EndUtc!.Value)))
            .Where(pair => SupplierConflictDetector.IsChecked(pair.Job, nowUtc))
            .OrderBy(pair => pair.Job.StartUtc)
            .ThenBy(pair => pair.Job.Id)
            .ToList();
        var truncated = upcoming.Count > SupplierAgendaLimits.MaxConflictRequests;
        if (truncated)
            upcoming = upcoming.Take(SupplierAgendaLimits.MaxConflictRequests).ToList();

        var services = await ServiceConstraintsAsync(supplierOrgId, upcoming.Select(pair => pair.Request).ToList(), cancellationToken);
        var jobs = upcoming
            .Select(pair =>
            {
                var constraints = pair.Request.ServiceListingId is { } id && services.TryGetValue(id, out var found) ? found : default;
                return pair.Job with { ServiceMinNoticeHours = constraints.MinNoticeHours, ServiceWeekdaysMask = constraints.WeekdaysMask };
            })
            .ToList();

        return new SupplierConflictReport(from, to, SupplierConflictDetector.Detect(input, jobs), truncated);
    }

    /// <summary>
    /// The notice and weekdays of the catalog services behind the requests a time can be proposed on (<c>Richiesto</c>): the only
    /// ones that get a suggestion. One read of the supplier's catalog, and none when no such request is for a service.
    /// </summary>
    private async Task<Dictionary<Guid, (int? MinNoticeHours, int? WeekdaysMask)>> ServiceConstraintsAsync(
        Guid supplierOrgId,
        IReadOnlyList<SupplierAgendaRequest> upcoming,
        CancellationToken cancellationToken)
    {
        var wanted = upcoming
            .Where(request => request.Status == ServiceRequestStatus.Richiesto && request.ServiceListingId is not null)
            .Select(request => request.ServiceListingId!.Value)
            .ToHashSet();
        if (wanted.Count == 0)
            return [];

        var listings = await catalog.ListAsync(supplierOrgId, cancellationToken);
        return listings
            .Where(listing => wanted.Contains(listing.Id))
            .ToDictionary(listing => listing.Id, listing => ((int?)listing.MinNoticeHours, (int?)listing.WeekdaysMask));
    }
}

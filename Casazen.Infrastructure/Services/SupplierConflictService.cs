using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierConflictService"/>
/// <remarks>
/// <para><b>What it reads.</b> The planning input of the agenda for the range and for the days after it where a time can be
/// suggested (<see cref="ISupplierAgendaService.BuildPlanningInputAsync(Guid, DateOnly, DateOnly, CancellationToken)"/>: the same
/// rules, hours, time off, blocks, engagements of the calendar feed, requests and bookings waiting as the slot planner reads), the
/// requests of the range with their hours and catalog service (<see cref="ISupplierServiceRequestReader.ListForAgendaAsync"/>) and,
/// only when a request still waiting for an answer is for a catalog service, that service's notice and weekdays (a suggested
/// time must be one <c>propose-time</c> accepts). It never names a table of the agenda or of the catalog: the guards of
/// <c>SupplierAgendaTenancyTests</c> and <c>SupplierServiceListingTenancyTests</c> need no new file.</para>
/// <para><b>Cost.</b> Three reads and one pure pass over at most <see cref="SupplierAgendaLimits.MaxConflictRequests"/> jobs of
/// at most 62 days of one supplier (the planning input is built once; a suggestion plans at most
/// <see cref="SupplierAgendaLimits.ConflictSuggestionDays"/> + 1 days in memory).</para>
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
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "The last day cannot be before the first.");
        if (to.DayNumber - from.DayNumber + 1 > SupplierAgendaLimits.MaxCalendarDays)
            throw new ArgumentOutOfRangeException(nameof(to), $"The range covers at most {SupplierAgendaLimits.MaxCalendarDays} days.");

        // The planner's input reaches past the range: a suggestion looks for a free slot in the days after the job.
        var input = await agenda.BuildPlanningInputAsync(
            supplierOrgId,
            from,
            to.AddDays(SupplierAgendaLimits.ConflictSuggestionDays),
            cancellationToken);

        var listed = await requests.ListForAgendaAsync(supplierOrgId, from, to, cancellationToken);
        var candidates = listed
            .Where(request => request is { HasHours: true } && IsJob(request.Status))
            .OrderBy(request => request.StartUtc)
            .ThenBy(request => request.Id)
            .ToList();

        // The nearest first: the bound cuts what is furthest away. Jobs already over are in the list and are left out by the
        // detector, so the bound is applied to the ones that count.
        var upcoming = candidates.Where(request => request.StartUtc >= input.NowUtc).ToList();
        var truncated = upcoming.Count > SupplierAgendaLimits.MaxConflictRequests;
        if (truncated)
            upcoming = upcoming.Take(SupplierAgendaLimits.MaxConflictRequests).ToList();

        var services = await ServiceConstraintsAsync(supplierOrgId, upcoming, cancellationToken);
        var jobs = upcoming
            .Select(request =>
            {
                var constraints = request.ServiceListingId is { } id && services.TryGetValue(id, out var found) ? found : default;
                return new SupplierConflictRequest(
                    request.Id,
                    request.Status,
                    request.StartUtc!.Value,
                    request.EndUtc!.Value,
                    constraints.MinNoticeHours,
                    constraints.WeekdaysMask);
            })
            .ToList();

        return new SupplierConflictReport(from, to, SupplierConflictDetector.Detect(input, jobs), truncated);
    }

    private static bool IsJob(ServiceRequestStatus status) =>
        status is ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso;

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

using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostTodayService"/>
/// <remarks>
/// Reads in bounded steps, none per row: the stays of the day (3 lists), the check-in state of the arrivals listed (one read of
/// the bookings, the guests of the stays and the links), the requests, the cockpit and what it lacks (one read of the stays of
/// the incomplete check-ins, a few reads for each of the first properties to activate), the stays of the cockpit (one read), and,
/// for a caller who may read payments, the confirmed stays whose payment failed (one page, and a count only when the page is full).
/// What SR-03b adds (the interventions of the day, the Alloggiati and the cleaning of the stays listed, the things to do about an
/// intervention) is read the same way, a few reads for all the rows. The cockpit's own check of the guests' data still asks once
/// per candidate stay (SR-03-FU4).
/// </remarks>
public sealed class HostTodayService(
    AppDbContext db,
    IHostDashboardService dashboard,
    IComplianceWizardService compliance,
    IComplianceMissingService complianceMissing,
    IOnSiteBookingRequestService onSiteRequests,
    IStayGuestService stayGuests,
    TimeProvider? timeProvider = null) : IHostTodayService
{
    /// <summary>Requests listed (the count covers all of them).</summary>
    public const int ApprovalListSize = 10;

    /// <summary>Things to do listed (the count covers all of them).</summary>
    public const int TodoListSize = 20;

    /// <summary>Interventions of the day listed (the count covers all of them; SR-03b).</summary>
    public const int ServiceListSize = 20;

    /// <summary>
    /// Things to do about an intervention listed (the count covers all of them; SR-03b): a backlog of finished jobs that nobody
    /// marked paid must not push every other thing to do out of the list, so they never take more than half of it.
    /// </summary>
    public const int ServiceTodoListSize = 10;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostToday> GetTodayAsync(
        HostScope scope,
        HostTodayOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var stays = await dashboard.GetTodayStaysAsync(scope, cancellationToken);
        var arriving = await GetArrivingAsync(stays.Arrivals.Items.Concat(stays.Upcoming.Items), stays.TodayInRome, nowUtc, cancellationToken);
        var cleaning = await GetCleaningAsync(scope, stays.Departures.Items, cancellationToken);

        var approvals = await onSiteRequests.GetAwaitingHostApprovalAsync(scope, cancellationToken);
        var cockpit = await complianceMissing.DescribeAsync(
            await compliance.GetSummaryAsync(scope, cancellationToken), cancellationToken);
        var failedPayments = options.IncludePayments
            ? await GetFailedPaymentsAsync(scope, cancellationToken)
            : new FailedPayments(0, []);
        var serviceRequests = await GetServiceRequestsToAnswerAsync(scope, cancellationToken);

        var todo = await BuildTodoAsync(cockpit, approvals, failedPayments, serviceRequests, cancellationToken);

        return new HostToday(
            stays.TodayInRome,
            MapList(stays.Arrivals, stay => ArrivingStay(stay, arriving)),
            MapList(stays.Departures, stay => new HostTodayStay(stay, null, null, cleaning.GetValueOrDefault(stay.BookingId))),
            MapList(stays.Upcoming, stay => ArrivingStay(stay, arriving)),
            new HostTodayList<HostTodayApproval>(
                approvals.Count,
                approvals.Take(ApprovalListSize).Select(ToApproval).ToList()),
            todo)
        {
            Services = await GetServicesOfTheDayAsync(scope, stays.TodayInRome, cancellationToken),
        };
    }

    private static HostTodayStay ArrivingStay(HostDashboardStay stay, IReadOnlyDictionary<Guid, ArrivingStayDetails> arriving)
    {
        var details = arriving.GetValueOrDefault(stay.BookingId);
        return new HostTodayStay(stay, details?.CheckIn, details?.Alloggiati);
    }

    private static HostTodayList<HostTodayStay> MapList(HostDashboardStayList list, Func<HostDashboardStay, HostTodayStay> map) =>
        new(list.Count, list.Items.Select(map).ToList());

    private static HostTodayApproval ToApproval(Booking booking) => new(
        booking.Id,
        booking.BookingCode,
        booking.PropertyId,
        booking.Property?.Name ?? string.Empty,
        GuestNameOf(booking),
        booking.NumberOfGuests,
        StayKpiRules.RomeDateOf(booking.CheckInDate),
        StayKpiRules.RomeDateOf(booking.CheckOutDate),
        booking.RequestExpiresAt ?? default);

    private static string GuestNameOf(Booking booking) =>
        booking.Guest is null ? string.Empty : $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim();

    // What the arriving stays say besides the booking: how far the online check-in of each is (its link, the best one it has, and
    // whether the data of the guests are complete) and, from SR-03b, where its Alloggiati communication stands. The bookings, the
    // guests of the stays, the links and the reports are read once for all of them.
    internal async Task<IReadOnlyDictionary<Guid, ArrivingStayDetails>> GetArrivingAsync(
        IEnumerable<HostDashboardStay> stays,
        DateTime todayInRome,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var ids = stays.Select(stay => stay.BookingId).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, ArrivingStayDetails>();

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => ids.Contains(b.Id))
            .ToListAsync(cancellationToken);
        var guests = await stayGuests.GetForBookingsAsync(bookings, cancellationToken);
        var sessions = (await db.GuestCheckInSessions
                .AsNoTracking()
                .Where(s => ids.Contains(s.BookingId))
                .ToListAsync(cancellationToken))
            .ToLookup(s => s.BookingId);
        var reports = (await db.AlloggiatiWebReports
                .AsNoTracking()
                .Where(r => ids.Contains(r.BookingId))
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.BookingId);

        return bookings.ToDictionary(
            booking => booking.Id,
            booking => new ArrivingStayDetails(
                new HostTodayCheckIn(
                    StateOf(sessions[booking.Id], nowUtc),
                    AlloggiatiRecordRules.IsDataComplete(guests[booking.Id])),
                AlloggiatiOf(booking, reports[booking.Id].ToList(), todayInRome)));
    }

    // The status the host reads (AlloggiatiStatusRules, the one of the Alloggiati page) and, until it is sent, the legal term.
    private static HostTodayAlloggiati AlloggiatiOf(Booking booking, IReadOnlyCollection<AlloggiatiWebReport> reports, DateTime todayInRome)
    {
        var status = AlloggiatiStatusRules.Effective(
            AlloggiatiStatusRules.SlotReport(reports, booking.GuestId)?.Status, booking.CheckInDate, todayInRome);
        return new HostTodayAlloggiati(status, AlloggiatiStatusRules.IsSent(status) ? null : AlloggiatiTerms.DeadlineUtc(booking));
    }

    // The latest cleaning request tied to each leaving stay, whatever made it (the check-out wizard, the interventions page): one
    // read for the departures of the day.
    internal async Task<IReadOnlyDictionary<Guid, HostTodayCleaning>> GetCleaningAsync(
        HostScope scope,
        IEnumerable<HostDashboardStay> departures,
        CancellationToken cancellationToken)
    {
        var ids = departures.Select(stay => stay.BookingId).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, HostTodayCleaning>();

        var requests = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId
                && r.RentalContext == ServiceRequestRentalContext.ShortRent
                && r.Category == ServiceCategories.Cleaning
                && r.BookingId != null
                && ids.Contains(r.BookingId.Value))
            .Select(r => new { BookingId = r.BookingId!.Value, r.Id, r.Status, r.CreatedAt })
            .ToListAsync(cancellationToken);

        return requests
            .GroupBy(r => r.BookingId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var latest = group.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First();
                    return new HostTodayCleaning(latest.Id, latest.Status);
                });
    }

    // The interventions of today (Europe/Rome): the requests whose time falls on the day and, for the ones that have none, the
    // requests of a stay that leaves today (the day of such a request has always been the check-out day of its stay). The ones
    // nobody will do (refused, cancelled) are not part of the day. A count and the first ones, by time.
    private async Task<HostTodayList<HostTodayServiceRequest>> GetServicesOfTheDayAsync(
        HostScope scope,
        DateTime todayInRome,
        CancellationToken cancellationToken)
    {
        var dayStart = RomeCalendar.StartOfDayUtc(todayInRome);
        var dayEnd = RomeCalendar.StartOfDayUtc(todayInRome.AddDays(1));

        var ofTheDay = HostServiceRequestsInScope(scope)
            .Where(r => r.Status != ServiceRequestStatus.Rifiutato && r.Status != ServiceRequestStatus.Annullato)
            .Where(r => (r.ScheduledStartUtc != null && r.ScheduledStartUtc >= dayStart && r.ScheduledStartUtc < dayEnd)
                || (r.ScheduledStartUtc == null
                    && r.Booking != null
                    && r.Booking.CheckOutDate >= dayStart
                    && r.Booking.CheckOutDate < dayEnd));

        var items = await ofTheDay
            .OrderBy(r => r.ScheduledStartUtc == null)
            .ThenBy(r => r.ScheduledStartUtc)
            .ThenBy(r => r.Id)
            .Take(ServiceListSize)
            .Select(r => new HostTodayServiceRequest(
                r.Id,
                r.Category,
                r.ServiceNameSnapshot,
                r.ScheduledStartUtc,
                r.ScheduledEndUtc,
                r.Status,
                r.SupplierOrgId,
                r.SupplierOrg.DisplayName != "" ? r.SupplierOrg.DisplayName : r.SupplierOrg.Name,
                r.Property!.Id,
                r.Property.Name,
                r.BookingId))
            .ToListAsync(cancellationToken);

        // Fewer than the page: that is all of them, no second query for the count.
        var count = items.Count < ServiceListSize ? items.Count : await ofTheDay.CountAsync(cancellationToken);
        return new HostTodayList<HostTodayServiceRequest>(count, items);
    }

    // The short-rent service requests of the properties of the scope (the org, and for a restricted scope the properties it reaches).
    // A request with no property is nobody's here.
    private IQueryable<ServiceRequest> HostServiceRequestsInScope(HostScope scope) => db.ServiceRequests
        .AsNoTracking()
        .Where(r => r.OrgId == scope.OrgId && r.RentalContext == ServiceRequestRentalContext.ShortRent && r.Property != null)
        .InScope(scope);

    // The requests that wait for the host (HostServiceRequestTodoRules): a proposed time to answer, an amount to confirm, a job to
    // pay. The first ones (ServiceTodoListSize), in the order of the things to do among the requests, and the count of them all.
    private async Task<ServiceRequestTodos> GetServiceRequestsToAnswerAsync(HostScope scope, CancellationToken cancellationToken)
    {
        var waiting = HostServiceRequestsInScope(scope).Where(HostServiceRequestTodoRules.NeedTheHost());

        var items = await waiting
            .OrderBy(HostServiceRequestTodoRules.Rank())
            .ThenBy(r => r.ScheduledStartUtc == null)
            .ThenBy(r => r.ScheduledStartUtc)
            .ThenBy(r => r.Id)
            .Take(ServiceTodoListSize)
            .Select(r => new ServiceRequestToAnswer(
                r.Id,
                r.Status,
                r.ProposedStartUtc != null,
                r.FinalAmountNeedsConfirmation,
                r.ScheduledStartUtc,
                r.BookingId,
                r.Property!.Id,
                r.Property.Name,
                r.SupplierOrg.DisplayName != "" ? r.SupplierOrg.DisplayName : r.SupplierOrg.Name))
            .ToListAsync(cancellationToken);

        var count = items.Count < ServiceTodoListSize ? items.Count : await waiting.CountAsync(cancellationToken);
        return new ServiceRequestTodos(count, items);
    }

    /// <summary>The state of the check-in link of a stay: a submitted one wins, otherwise the latest link tells.</summary>
    internal static HostTodayCheckInState StateOf(IEnumerable<GuestCheckInSession> sessions, DateTime nowUtc)
    {
        var latestFirst = sessions.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).ToList();
        if (latestFirst.Any(s => s.IsCompleted))
            return HostTodayCheckInState.Completed;

        var latest = latestFirst.FirstOrDefault();
        if (latest is null)
            return HostTodayCheckInState.NotSent;

        return latest.EffectiveStatus(nowUtc) switch
        {
            GuestCheckInSessionStatus.InCompilazione => HostTodayCheckInState.InProgress,
            // The link exists, but an email that failed left the guest with nothing to open (the host can send it again).
            GuestCheckInSessionStatus.Inviato => latest.LinkEmailStatus == GuestCheckInLinkEmailStatus.Failed
                ? HostTodayCheckInState.NotSent
                : HostTodayCheckInState.Sent,
            _ => HostTodayCheckInState.Expired,
        };
    }

    // The confirmed stays whose payment failed and nothing else paid: the guest has to be followed up (a deferred charge
    // that was declined, a card that needs another authentication). One item per stay.
    private async Task<FailedPayments> GetFailedPaymentsAsync(HostScope scope, CancellationToken cancellationToken)
    {
        var failed = db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId)
            .InScope(scope)
            .Where(b => b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
            .Where(b => b.Payments.Any(p => p.Status == PaymentStatus.Failed))
            .Where(b => !b.Payments.Any(p =>
                p.Status == PaymentStatus.Completed
                || p.Status == PaymentStatus.PartiallyRefunded
                || p.Status == PaymentStatus.Processing));

        var items = await failed
            .OrderBy(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .Take(TodoListSize)
            .Select(b => new FailedPayment(
                b.Id,
                b.PropertyId,
                b.Property.Name,
                (b.Guest.FirstName + " " + b.Guest.LastName).Trim(),
                b.Payments
                    .Where(p => p.Status == PaymentStatus.Failed)
                    .OrderByDescending(p => p.UpdatedAt)
                    .Select(p => p.Id)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        // Fewer than the page: that is all of them, no second query for the count.
        var count = items.Count < TodoListSize ? items.Count : await failed.CountAsync(cancellationToken);
        return new FailedPayments(count, items);
    }

    private async Task<HostTodayList<HostTodoItem>> BuildTodoAsync(
        ComplianceSummaryResult cockpit,
        IReadOnlyList<Booking> approvals,
        FailedPayments failedPayments,
        ServiceRequestTodos serviceRequests,
        CancellationToken cancellationToken)
    {
        var items = new List<HostTodoItem>();

        // Requests first: the host's answer is due by RespondBy, then the request is cancelled.
        foreach (var booking in approvals)
        {
            items.Add(new HostTodoItem(
                HostTodoAction.RespondToRequest,
                HostTodoPriority.Of(HostTodoAction.RespondToRequest),
                booking.RequestExpiresAt,
                GuestNameOf(booking),
                booking.PropertyId,
                booking.Property?.Name,
                booking.Id,
                null,
                [new ComplianceMissing(ComplianceMissingCodes.ApprovalNotAnswered, "response")]));
        }

        var sections = new[]
        {
            cockpit.PropertiesPending,
            cockpit.GuestCheckInsIncomplete,
            cockpit.CheckoutsDue,
            cockpit.AlloggiatiFailures,
            cockpit.AlloggiatiManualRequired,
            cockpit.TurnoversPending,
        };
        var cockpitItems = sections.SelectMany(section => section.Items).ToList();
        var facts = await GetBookingFactsAsync(cockpitItems, cancellationToken);

        foreach (var item in cockpitItems)
        {
            var action = HostTodoPriority.From(item.Action);
            var stay = item.BookingId is { } bookingId ? facts.GetValueOrDefault(bookingId) : null;
            items.Add(new HostTodoItem(
                action,
                HostTodoPriority.Of(action),
                DueAt(action, stay),
                item.Label,
                item.PropertyId ?? stay?.PropertyId,
                // The label of a property to activate is its name.
                item.PropertyId is not null ? item.Label : stay?.PropertyName,
                item.BookingId,
                null,
                item.Missing));
        }

        foreach (var payment in failedPayments.Items)
        {
            items.Add(new HostTodoItem(
                HostTodoAction.ReviewFailedPayment,
                HostTodoPriority.Of(HostTodoAction.ReviewFailedPayment),
                null,
                payment.GuestName,
                payment.PropertyId,
                payment.PropertyName,
                payment.BookingId,
                payment.PaymentId == Guid.Empty ? null : payment.PaymentId,
                [new ComplianceMissing(ComplianceMissingCodes.PaymentFailed, "payment")]));
        }

        // The interventions that wait for the host (SR-03b): the label is the supplier, the target the request.
        foreach (var request in serviceRequests.Items)
        {
            var action = HostServiceRequestTodoRules.ActionOf(request.Status, request.HasProposal, request.FinalAmountNeedsConfirmation);
            if (action is not { } todoAction)
                continue;

            items.Add(new HostTodoItem(
                todoAction,
                HostTodoPriority.Of(todoAction),
                HostServiceRequestTodoRules.DueAt(todoAction, request.ScheduledStart),
                request.SupplierName,
                request.PropertyId,
                request.PropertyName,
                request.BookingId,
                null,
                [HostServiceRequestTodoRules.MissingFor(todoAction)],
                request.Id));
        }

        // The count covers every thing to do, the cockpit's sections that list only their latest items included.
        var total = approvals.Count + sections.Sum(section => section.Count) + failedPayments.Count + serviceRequests.Count;

        var ordered = items
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.DueAt ?? DateTime.MaxValue)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.BookingId ?? item.PropertyId ?? Guid.Empty)
            .Take(TodoListSize)
            .ToList();

        return new HostTodayList<HostTodoItem>(Math.Max(total, ordered.Count), ordered);
    }

    // The property and the dates of the stays of the cockpit, in one read: what a stay's term is worked out from.
    private async Task<Dictionary<Guid, StayFacts>> GetBookingFactsAsync(
        IReadOnlyList<ComplianceSummaryItem> cockpitItems,
        CancellationToken cancellationToken)
    {
        var ids = cockpitItems.Where(item => item.BookingId is not null).Select(item => item.BookingId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, StayFacts>();

        var facts = await db.Bookings
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new StayFacts(b.Id, b.PropertyId, b.Property.Name, b.CheckInDate, b.CheckOutDate, b.ArrivedAt))
            .ToListAsync(cancellationToken);
        return facts.ToDictionary(stay => stay.BookingId);
    }

    // When the thing is due, if the stay fixes it: the legal term of the Alloggiati communication (which runs from the arrival),
    // the day the guest arrives (the data are needed by then), the day the guest leaves.
    private static DateTime? DueAt(HostTodoAction action, StayFacts? stay)
    {
        if (stay is null)
            return null;

        return action switch
        {
            HostTodoAction.ResolveAlloggiatiFailure or HostTodoAction.SendAlloggiati =>
                AlloggiatiTerms.DeadlineUtc(stay.ArrivedAt, stay.CheckInDate, stay.CheckOutDate),
            HostTodoAction.CompleteGuestCheckIn => AlloggiatiTerms.ArrivalDayStartUtc(stay.CheckInDate),
            HostTodoAction.CheckOut => RomeCalendar.StartOfDayUtc(stay.CheckOutDate),
            _ => null,
        };
    }

    private sealed record StayFacts(
        Guid BookingId,
        Guid PropertyId,
        string PropertyName,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        DateTime? ArrivedAt);

    private sealed record FailedPayment(Guid BookingId, Guid PropertyId, string PropertyName, string GuestName, Guid PaymentId);

    private sealed record FailedPayments(int Count, IReadOnlyList<FailedPayment> Items);

    internal sealed record ArrivingStayDetails(HostTodayCheckIn CheckIn, HostTodayAlloggiati Alloggiati);

    private sealed record ServiceRequestToAnswer(
        Guid Id,
        ServiceRequestStatus Status,
        bool HasProposal,
        bool FinalAmountNeedsConfirmation,
        DateTime? ScheduledStart,
        Guid? BookingId,
        Guid PropertyId,
        string PropertyName,
        string SupplierName);

    private sealed record ServiceRequestTodos(int Count, IReadOnlyList<ServiceRequestToAnswer> Items);
}

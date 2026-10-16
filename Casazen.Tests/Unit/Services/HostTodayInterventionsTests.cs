using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03b on the host's day (<c>GET /api/dashboard/today</c>): the interventions of the day, where the Alloggiati communication of
/// each arrival stands, the cleaning of each departure, and the things to do about an intervention (a time to answer, an amount to
/// confirm, a job to pay). Inside the caller's scope. Today is 9 October 2026 in Rome (summer time: UTC+2).
/// </summary>
public class HostTodayInterventionsTests
{
    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static HostTodayService Service(AppDbContext db) => HostTodayServiceTests.NewService(db);

    private sealed record Fixture(AppDbContext Db, Guid OrgId, Property Trullo, Property CasaBianca, OrgEntity Supplier, Guest Guest);

    private static async Task<Fixture> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        // Both properties are active, so the day has no property to activate in it.
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Trullo");
        trullo.ComplianceStatus = PropertyComplianceStatus.Active;
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Casa Bianca");
        casaBianca.ComplianceStatus = PropertyComplianceStatus.Active;
        var supplier = HostScopeScenario.NewOrg("Splendore Pulizie", $"splendore-{Guid.NewGuid():N}"[..20], OrgType.Supplier);
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Orgs.Add(supplier);
        db.Properties.AddRange(trullo, casaBianca);
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return new Fixture(db, orgId, trullo, casaBianca, supplier, guest);
    }

    private static Booking Stay(Fixture world, Property property, BookingStatus status, DateTime checkIn, DateTime checkOut) => new()
    {
        OrgId = world.OrgId,
        PropertyId = property.Id,
        GuestId = world.Guest.Id,
        Status = status,
        Source = BookingSource.Direct,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
    };

    private static ServiceRequest Request(
        Fixture world,
        Property property,
        ServiceRequestStatus status = ServiceRequestStatus.Richiesto,
        DateTime? start = null,
        DateTime? end = null,
        Guid? bookingId = null,
        string category = ServiceCategories.Cleaning)
    {
        return new ServiceRequest
        {
            OrgId = world.OrgId,
            PropertyId = property.Id,
            BookingId = bookingId,
            SupplierOrgId = world.Supplier.Id,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = category,
            Status = status,
            ScheduledStartUtc = start,
            ScheduledEndUtc = start is null ? null : end ?? start.Value.AddHours(2),
            ServiceNameSnapshot = "Pulizia di fine soggiorno",
        };
    }

    // --- The interventions of the day -------------------------------------------------------------------------

    [Fact]
    public async Task Services_TheInterventionsOfToday_InOrderOfTime_WithTheirSupplierAndProperty()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        var leavesToday = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 6), Day(10, 9));
        var leavesTomorrow = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 7), Day(10, 10));
        world.Db.Bookings.AddRange(leavesToday, leavesTomorrow);
        await world.Db.SaveChangesAsync();

        // 10:00 Rome; 08:00 Rome (so the first of them); half past midnight (the day starts at 22:00Z of the 8th); a request with no
        // time of a stay that leaves today (the day of such a request has always been the check-out day of its stay).
        var morning = Request(world, world.Trullo, ServiceRequestStatus.PresoInCarico, At(10, 9, 8));
        var early = Request(world, world.CasaBianca, ServiceRequestStatus.InCorso, At(10, 9, 6), category: ServiceCategories.Maintenance);
        var justAfterMidnight = Request(world, world.Trullo, ServiceRequestStatus.Richiesto, At(10, 8, 22, 30));
        var withNoTime = Request(world, world.Trullo, ServiceRequestStatus.Richiesto, bookingId: leavesToday.Id);
        // Not today: the last minute of the day before, the first of the day after, tomorrow's stay with no time.
        var yesterday = Request(world, world.Trullo, ServiceRequestStatus.Completato, At(10, 8, 21, 59));
        var tomorrow = Request(world, world.Trullo, ServiceRequestStatus.Richiesto, At(10, 9, 22));
        var noTimeTomorrow = Request(world, world.Trullo, ServiceRequestStatus.Richiesto, bookingId: leavesTomorrow.Id);
        // Today, but nobody will do them.
        var refused = Request(world, world.Trullo, ServiceRequestStatus.Rifiutato, At(10, 9, 9));
        var cancelled = Request(world, world.Trullo, ServiceRequestStatus.Annullato, At(10, 9, 9));
        world.Db.ServiceRequests.AddRange(
            morning, early, justAfterMidnight, withNoTime, yesterday, tomorrow, noTimeTomorrow, refused, cancelled);
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(4, today.Services.Count);
        Assert.Equal(
            [justAfterMidnight.Id, early.Id, morning.Id, withNoTime.Id],
            today.Services.Items.Select(s => s.Id));
        var first = today.Services.Items[1];
        Assert.Equal(
            (ServiceCategories.Maintenance, "Pulizia di fine soggiorno", At(10, 9, 6), At(10, 9, 8), ServiceRequestStatus.InCorso),
            (first.Category, first.ServiceName, first.ScheduledStart, first.ScheduledEnd, first.Status));
        Assert.Equal((world.Supplier.Id, "Splendore Pulizie"), (first.SupplierOrgId, first.SupplierName));
        Assert.Equal((world.CasaBianca.Id, "Casa Bianca"), (first.PropertyId, first.PropertyName));
        // The one with no time says so: the client puts it after the others.
        var legacy = today.Services.Items[3];
        Assert.Null(legacy.ScheduledStart);
        Assert.Null(legacy.ScheduledEnd);
        Assert.Equal(leavesToday.Id, legacy.BookingId);
    }

    [Fact]
    public async Task Services_ACollaboratorLimitedToSomePropertiesSeesThoseOfItsPropertyOnly()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        foreach (var property in new[] { world.Granted, world.Hidden, world.OtherOrgProperty })
        {
            db.ServiceRequests.Add(new ServiceRequest
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                SupplierOrgId = world.SupplierOrgId,
                RentalContext = ServiceRequestRentalContext.ShortRent,
                Category = ServiceCategories.Cleaning,
                Status = ServiceRequestStatus.PresoInCarico,
                ScheduledStartUtc = At(10, 9, 8),
                ScheduledEndUtc = At(10, 9, 10),
            });
        }

        await db.SaveChangesAsync();

        var restricted = await Service(db).GetTodayAsync(world.Restricted, new HostTodayOptions());
        var orgWide = await Service(db).GetTodayAsync(world.OrgWide, new HostTodayOptions());

        Assert.Equal([world.Granted.Id], restricted.Services.Items.Select(s => s.PropertyId));
        Assert.Equal(1, restricted.Services.Count);
        Assert.Equal(new[] { world.Granted.Id, world.Hidden.Id }.Order(), orgWide.Services.Items.Select(s => s.PropertyId).Order());
        Assert.DoesNotContain(orgWide.Services.Items, s => s.PropertyId == world.OtherOrgProperty.Id);
    }

    [Fact]
    public async Task Services_OnlyTheFirstTwentyAreListed_TheCountCoversAll_SoonestFirst()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        world.Db.ServiceRequests.AddRange(
            Enumerable.Range(0, 25).Select(i => Request(world, world.Trullo, ServiceRequestStatus.PresoInCarico, At(10, 9, 5).AddMinutes(i * 10))));
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(25, today.Services.Count);
        Assert.Equal(HostTodayService.ServiceListSize, today.Services.Items.Count);
        Assert.Equal(today.Services.Items.Select(s => s.ScheduledStart).Order(), today.Services.Items.Select(s => s.ScheduledStart));
        Assert.Equal(At(10, 9, 5), today.Services.Items[0].ScheduledStart);
    }

    [Fact]
    public async Task Services_NothingToday_IsAnEmptyList_NotAMissingOne()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(0, today.Services.Count);
        Assert.Empty(today.Services.Items);
    }

    // --- Where the Alloggiati communication of each arrival stands ----------------------------------------------

    private static AlloggiatiWebReport Report(Booking booking, AlloggiatiWebStatus status) => new()
    {
        BookingId = booking.Id,
        OrgId = booking.OrgId,
        GuestId = booking.GuestId,
        Status = status,
        ConfirmationNumber = status == AlloggiatiWebStatus.Inviato ? "RICEVUTA-1" : null,
        ReportedAt = status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente ? At(10, 9, 7) : null,
    };

    [Fact]
    public async Task Arrivals_EachOneSaysWhereItsAlloggiatiCommunicationStands_AndItsTermUntilItIsSent()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        var untouched = Stay(world, world.Trullo, BookingStatus.Confirmed, Day(10, 9), Day(10, 12));
        var oneNight = Stay(world, world.Trullo, BookingStatus.Confirmed, Day(10, 9), Day(10, 10));
        var sent = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 9), Day(10, 12));
        var declared = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 9), Day(10, 12));
        var failed = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 9), Day(10, 12));
        failed.ArrivedAt = At(10, 9, 8);
        var tomorrow = Stay(world, world.Trullo, BookingStatus.Confirmed, Day(10, 10), Day(10, 13));
        world.Db.Bookings.AddRange(untouched, oneNight, sent, declared, failed, tomorrow);
        world.Db.AlloggiatiWebReports.AddRange(
            Report(sent, AlloggiatiWebStatus.Inviato),
            Report(declared, AlloggiatiWebStatus.InviatoManualmente),
            Report(failed, AlloggiatiWebStatus.Errore));
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        var byBooking = today.Arrivals.Items.Concat(today.Upcoming.Items).ToDictionary(s => s.Stay.BookingId, s => s.Alloggiati!);
        // From the arrival day, nothing sent is "to send manually"; 24 hours from the start of the arrival day (midnight in Rome is
        // 22:00Z of the day before), 6 hours for a stay of one night.
        Assert.Equal(new HostTodayAlloggiati(AlloggiatiWebStatus.DaInviareManualmente, At(10, 9, 22)), byBooking[untouched.Id]);
        Assert.Equal(new HostTodayAlloggiati(AlloggiatiWebStatus.DaInviareManualmente, At(10, 9, 4)), byBooking[oneNight.Id]);
        // Sent, or declared sent: done, no term any more.
        Assert.Equal(new HostTodayAlloggiati(AlloggiatiWebStatus.Inviato, null), byBooking[sent.Id]);
        Assert.Equal(new HostTodayAlloggiati(AlloggiatiWebStatus.InviatoManualmente, null), byBooking[declared.Id]);
        // A failure keeps its term, which runs from the arrival that was registered (08:00Z + 24 hours).
        Assert.Equal(new HostTodayAlloggiati(AlloggiatiWebStatus.Errore, At(10, 10, 8)), byBooking[failed.Id]);
        // Tomorrow's arrival: not yet to send on the portal (it accepts today or yesterday only).
        Assert.Equal(new HostTodayAlloggiati(AlloggiatiWebStatus.DaInviare, At(10, 10, 22)), byBooking[tomorrow.Id]);
        Assert.All(today.Arrivals.Items.Concat(today.Upcoming.Items), stay => Assert.NotNull(stay.Alloggiati));
    }

    [Fact]
    public async Task Departures_HaveNoAlloggiati_AndNoCheckIn()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        world.Db.Bookings.Add(Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 6), Day(10, 9)));
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        var departure = Assert.Single(today.Departures.Items);
        Assert.Null(departure.Alloggiati);
        Assert.Null(departure.CheckIn);
    }

    // --- The cleaning of each departure ------------------------------------------------------------------------

    [Fact]
    public async Task Departures_EachOneCarriesItsLatestCleaningRequest_AndNullWhenItHasNone()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        var withCleaning = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 6), Day(10, 9));
        var withTwo = Stay(world, world.Trullo, BookingStatus.CheckedIn, Day(10, 5), Day(10, 9));
        var withoutAny = Stay(world, world.CasaBianca, BookingStatus.CheckedIn, Day(10, 7), Day(10, 9));
        var withMaintenanceOnly = Stay(world, world.CasaBianca, BookingStatus.CheckedIn, Day(10, 7), Day(10, 9));
        world.Db.Bookings.AddRange(withCleaning, withTwo, withoutAny, withMaintenanceOnly);

        var done = Request(world, world.Trullo, ServiceRequestStatus.Completato, bookingId: withCleaning.Id);
        var cancelledFirst = Request(world, world.Trullo, ServiceRequestStatus.Annullato, bookingId: withTwo.Id);
        cancelledFirst.CreatedAt = At(10, 6, 9);
        var askedAgain = Request(world, world.Trullo, ServiceRequestStatus.Richiesto, bookingId: withTwo.Id);
        askedAgain.CreatedAt = At(10, 7, 9);
        var maintenance = Request(world, world.CasaBianca, ServiceRequestStatus.InCorso, bookingId: withMaintenanceOnly.Id, category: ServiceCategories.Maintenance);
        world.Db.ServiceRequests.AddRange(done, cancelledFirst, askedAgain, maintenance);
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        var cleaning = today.Departures.Items.ToDictionary(s => s.Stay.BookingId, s => s.Cleaning);
        Assert.Equal(new HostTodayCleaning(done.Id, ServiceRequestStatus.Completato), cleaning[withCleaning.Id]);
        // The request that replaced the one that was cancelled is the cleaning of the stay.
        Assert.Equal(new HostTodayCleaning(askedAgain.Id, ServiceRequestStatus.Richiesto), cleaning[withTwo.Id]);
        Assert.Null(cleaning[withoutAny.Id]);
        Assert.Null(cleaning[withMaintenanceOnly.Id]);
        // An arrival has no cleaning to follow.
        Assert.All(today.Arrivals.Items, s => Assert.Null(s.Cleaning));
    }

    // --- The things to do about an intervention ----------------------------------------------------------------

    private static async Task<(Fixture World, ServiceRequest Proposal, ServiceRequest Amount, ServiceRequest ToPay)> SeedInterventionsAsync()
    {
        var world = await SeedAsync();
        var proposal = Request(world, world.Trullo, ServiceRequestStatus.Richiesto, At(10, 12, 8));
        proposal.ProposedStartUtc = At(10, 12, 13);
        proposal.ProposedEndUtc = At(10, 12, 15);
        proposal.ProposedAt = At(10, 8, 9);
        var amount = Request(world, world.CasaBianca, ServiceRequestStatus.Completato, At(10, 7, 8));
        amount.FinalAmountCents = 15_000;
        amount.QuotedAmountCents = 10_000;
        amount.FinalAmountNeedsConfirmation = true;
        var toPay = Request(world, world.Trullo, ServiceRequestStatus.Completato, At(10, 6, 8));
        toPay.FinalAmountCents = 8_000;

        // Nothing for the host: a new request nobody proposed anything on (a reminder, at most), one the supplier took, one under
        // way, one paid, one cancelled, a long-term request, a showcase one.
        world.Db.ServiceRequests.AddRange(
            proposal,
            amount,
            toPay,
            Request(world, world.Trullo, ServiceRequestStatus.Richiesto, At(10, 12, 8)),
            Request(world, world.Trullo, ServiceRequestStatus.PresoInCarico, At(10, 12, 8)),
            Request(world, world.Trullo, ServiceRequestStatus.InCorso, At(10, 12, 8)),
            Request(world, world.Trullo, ServiceRequestStatus.Pagato, At(10, 3, 8)),
            Request(world, world.Trullo, ServiceRequestStatus.Annullato, At(10, 12, 8)),
            Request(world, world.Trullo, ServiceRequestStatus.Rifiutato, At(10, 12, 8)));
        var longRent = Request(world, world.Trullo, ServiceRequestStatus.Completato, At(10, 1, 8));
        longRent.RentalContext = ServiceRequestRentalContext.LongRent;
        world.Db.ServiceRequests.Add(longRent);
        await world.Db.SaveChangesAsync();
        return (world, proposal, amount, toPay);
    }

    [Fact]
    public async Task Todo_ATimeToAnswer_AnAmountToConfirm_AJobToPay_EachWithItsRequestAndWhatItLacks()
    {
        var (world, proposal, amount, toPay) = await SeedInterventionsAsync();
        await using var _ = world.Db;

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(3, today.Todo.Count);
        Assert.Equal(
            [HostTodoAction.RespondToServiceRequest, HostTodoAction.ConfirmFinalAmount, HostTodoAction.PayServiceRequest],
            today.Todo.Items.Select(i => i.Action));
        Assert.Equal([1, 5, 6], today.Todo.Items.Select(i => i.Priority));
        Assert.Equal([proposal.Id, amount.Id, toPay.Id], today.Todo.Items.Select(i => i.ServiceRequestId));
        // Whom it is about (the supplier), where (the property), never a path.
        Assert.All(today.Todo.Items, item => Assert.Equal("Splendore Pulizie", item.Label));
        Assert.Equal(["Trullo", "Casa Bianca", "Trullo"], today.Todo.Items.Select(i => i.PropertyName));
        Assert.Equal(
            [
                new ComplianceMissing(ComplianceMissingCodes.ProposalNotAnswered, "response"),
                new ComplianceMissing(ComplianceMissingCodes.FinalAmountNotConfirmed, "amount"),
                new ComplianceMissing(ComplianceMissingCodes.ServiceNotPaid, "payment"),
            ],
            today.Todo.Items.Select(i => Assert.Single(i.Missing)));
        // The answer is due before the work was to be done; the amount and the payment have no term of their own.
        Assert.Equal([At(10, 12, 8), null, null], today.Todo.Items.Select(i => i.DueAt));
        Assert.All(today.Todo.Items, item => Assert.Null(item.PaymentId));
    }

    [Fact]
    public async Task Todo_TheInterventionsStandInTheListWithTheThingsOfTheStays_ByPriorityThenByTerm()
    {
        var (world, proposal, _, _) = await SeedInterventionsAsync();
        await using var _ = world.Db;
        // A request waiting for the host that expires after the proposal's term is due (the 12th at 08:00Z): same priority, later.
        var request = Stay(world, world.Trullo, BookingStatus.Pending, Day(11, 20), Day(11, 23));
        request.PaymentOption = PaymentOption.OnSite;
        request.GuestEmailVerifiedAt = At(10, 8, 12);
        request.RequestExpiresAt = At(10, 13, 8);
        var soon = Stay(world, world.Trullo, BookingStatus.Pending, Day(11, 25), Day(11, 28));
        soon.PaymentOption = PaymentOption.OnSite;
        soon.GuestEmailVerifiedAt = At(10, 8, 12);
        soon.RequestExpiresAt = At(10, 10, 8);
        world.Db.Bookings.AddRange(request, soon);
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(
            [HostTodoAction.RespondToRequest, HostTodoAction.RespondToServiceRequest, HostTodoAction.RespondToRequest, HostTodoAction.ConfirmFinalAmount, HostTodoAction.PayServiceRequest],
            today.Todo.Items.Select(i => i.Action));
        Assert.Equal(5, today.Todo.Count);
        Assert.Equal(proposal.Id, today.Todo.Items[1].ServiceRequestId);
        Assert.Null(today.Todo.Items[0].ServiceRequestId);
    }

    [Fact]
    public async Task Todo_TheInterventionsWithoutMoneyReadAreThereToo_OnlyTheFailedPaymentsOfTheStaysAreHidden()
    {
        var (world, _, _, _) = await SeedInterventionsAsync();
        await using var _ = world.Db;

        var withoutMoney = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions(IncludePayments: false));
        var withMoney = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions(IncludePayments: true));

        // Paying a supplier is a collaborator's job (servicerequest.write), and the item carries no amount.
        Assert.Equal(3, withoutMoney.Todo.Count);
        Assert.Equal(3, withMoney.Todo.Count);
    }

    [Fact]
    public async Task Todo_ACollaboratorLimitedToSomePropertiesIsToldAboutItsPropertysInterventionsOnly()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        foreach (var property in new[] { world.Granted, world.Hidden, world.OtherOrgProperty })
        {
            db.ServiceRequests.Add(new ServiceRequest
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                SupplierOrgId = world.SupplierOrgId,
                RentalContext = ServiceRequestRentalContext.ShortRent,
                Category = ServiceCategories.Cleaning,
                Status = ServiceRequestStatus.Completato,
                FinalAmountCents = 8_000,
            });
        }

        await db.SaveChangesAsync();

        var restricted = await Service(db).GetTodayAsync(world.Restricted, new HostTodayOptions());
        var orgWide = await Service(db).GetTodayAsync(world.OrgWide, new HostTodayOptions());

        Assert.Equal(
            [world.Granted.Id],
            restricted.Todo.Items.Where(i => i.Action == HostTodoAction.PayServiceRequest).Select(i => i.PropertyId));
        Assert.Equal(
            new[] { world.Granted.Id, world.Hidden.Id }.Order(),
            orgWide.Todo.Items.Where(i => i.Action == HostTodoAction.PayServiceRequest).Select(i => i.PropertyId!.Value).Order());
    }

    [Fact]
    public async Task Todo_OnlyTheFirstTenInterventionsAreListed_TheCountCoversAllOfThem()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        world.Db.ServiceRequests.AddRange(
            Enumerable.Range(0, 25).Select(i =>
            {
                var request = Request(world, world.Trullo, ServiceRequestStatus.Completato);
                request.CompletedAt = At(10, 8, 10).AddMinutes(i);
                return request;
            }));
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(25, today.Todo.Count);
        Assert.Equal(HostTodayService.ServiceTodoListSize, today.Todo.Items.Count);
        Assert.All(today.Todo.Items, item => Assert.Equal(HostTodoAction.PayServiceRequest, item.Action));
    }

    [Fact]
    public async Task Todo_ABacklogOfFinishedJobsNobodyMarkedPaid_NeverPushesTheOtherThingsToDoOutOfTheList()
    {
        var world = await SeedAsync();
        await using var _ = world.Db;
        // A property still to activate (the lowest priority), and a backlog of jobs to pay (the sixth) longer than the list.
        var toActivate = HostScopeScenario.NewProperty(world.OrgId, "auth0|titolare-sr03b", "Masseria");
        world.Db.Properties.Add(toActivate);
        world.Db.ServiceRequests.AddRange(
            Enumerable.Range(0, 25).Select(i => Request(world, world.Trullo, ServiceRequestStatus.Completato)));
        await world.Db.SaveChangesAsync();

        var today = await Service(world.Db).GetTodayAsync(new HostScope(world.OrgId), new HostTodayOptions());

        Assert.Equal(26, today.Todo.Count);
        Assert.Equal(HostTodayService.ServiceTodoListSize, today.Todo.Items.Count(i => i.Action == HostTodoAction.PayServiceRequest));
        Assert.Contains(today.Todo.Items, i => i.Action == HostTodoAction.ActivateProperty && i.PropertyId == toActivate.Id);
    }

    // --- The rule that says which request asks something of the host ---------------------------------------------

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true, false, HostTodoAction.RespondToServiceRequest)]
    [InlineData(ServiceRequestStatus.Richiesto, false, false, null)]
    [InlineData(ServiceRequestStatus.Richiesto, false, true, null)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true, false, null)]
    [InlineData(ServiceRequestStatus.InCorso, false, false, null)]
    [InlineData(ServiceRequestStatus.Completato, false, true, HostTodoAction.ConfirmFinalAmount)]
    [InlineData(ServiceRequestStatus.Completato, false, false, HostTodoAction.PayServiceRequest)]
    [InlineData(ServiceRequestStatus.Completato, true, false, HostTodoAction.PayServiceRequest)]
    [InlineData(ServiceRequestStatus.Pagato, false, false, null)]
    [InlineData(ServiceRequestStatus.Rifiutato, true, false, null)]
    [InlineData(ServiceRequestStatus.Annullato, true, true, null)]
    public void ActionOf_ARequestAndWhatItIsWaitingFor(
        ServiceRequestStatus status, bool hasProposal, bool needsConfirmation, HostTodoAction? expected)
    {
        Assert.Equal(expected, HostServiceRequestTodoRules.ActionOf(status, hasProposal, needsConfirmation));
        // The query that finds the requests agrees with the rule.
        var request = new ServiceRequest
        {
            Status = status,
            ProposedStartUtc = hasProposal ? At(10, 12, 13) : null,
            FinalAmountNeedsConfirmation = needsConfirmation,
        };
        Assert.Equal(expected is not null, HostServiceRequestTodoRules.NeedTheHost().Compile()(request));
    }

    [Fact]
    public void MissingFor_OnlyTheActionsAboutInterventionsHaveOne()
    {
        Assert.Equal(ComplianceMissingCodes.ProposalNotAnswered, HostServiceRequestTodoRules.MissingFor(HostTodoAction.RespondToServiceRequest).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => HostServiceRequestTodoRules.MissingFor(HostTodoAction.CheckOut));
        Assert.Null(HostServiceRequestTodoRules.DueAt(HostTodoAction.PayServiceRequest, At(10, 12, 8)));
        Assert.Equal(At(10, 12, 8), HostServiceRequestTodoRules.DueAt(HostTodoAction.RespondToServiceRequest, At(10, 12, 8)));
        Assert.Null(HostServiceRequestTodoRules.DueAt(HostTodoAction.RespondToServiceRequest, null));
    }
}

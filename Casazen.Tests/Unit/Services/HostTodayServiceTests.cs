using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03: the host's day in one read. Who arrives and leaves today and how far their online check-in is, the requests waiting
/// with the time left to answer, and the things to do in order of priority, each with what it lacks and where it leads; inside
/// the caller's scope, and with the failed payments only for a caller who may read payments. Today is 9 October 2026 in Rome.
/// </summary>
public class HostTodayServiceTests
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    internal static HostTodayService NewService(AppDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Compliance:RequiredDocuments:default:0"] = "CinCertificate" })
            .Build();
        var stayGuests = new StayGuestService(db, new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance));
        var status = new PropertyComplianceStatusService(
            db,
            configuration,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Options.Create(new ComplianceOptions()),
            NullLogger<PropertyComplianceStatusService>.Instance,
            Clock);

        return new HostTodayService(
            db,
            new HostDashboardService(db, new ConfigurationBuilder().Build(), Clock),
            ComplianceWizardServiceTests.CreateService(db, Clock),
            new ComplianceMissingService(db, status, stayGuests),
            new OnSiteBookingRequestService(
                db, null!, null!, null!, new ConfigurationBuilder().Build(), NullLogger<OnSiteBookingRequestService>.Instance, Clock),
            stayGuests,
            Clock);
    }

    // --- The world of the scope tests: one of everything on each of two properties --------------------

    [Fact]
    public async Task Todo_EveryThingToDo_InOrderOfPriority_WithWhatItLacksAndWhereItLeads()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);

        var today = await NewService(db).GetTodayAsync(world.OrgWide, new HostTodayOptions());

        // A request, a communication to send, the data of an arrival, a turnover and a property to activate, on each property.
        HostTodoAction[] expected =
        [
            HostTodoAction.RespondToRequest, HostTodoAction.RespondToRequest,
            HostTodoAction.SendAlloggiati, HostTodoAction.SendAlloggiati,
            HostTodoAction.CompleteGuestCheckIn, HostTodoAction.CompleteGuestCheckIn,
            HostTodoAction.ConfirmPropertyReady, HostTodoAction.ConfirmPropertyReady,
            HostTodoAction.ActivateProperty, HostTodoAction.ActivateProperty,
        ];
        Assert.Equal(expected, today.Todo.Items.Select(i => i.Action));
        Assert.Equal(10, today.Todo.Count);
        Assert.Equal(expected.Select(HostTodoPriority.Of), today.Todo.Items.Select(i => i.Priority));
        Assert.All(today.Todo.Items, item => Assert.NotEmpty(item.Missing));
        Assert.All(today.Todo.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Label)));
        // Never a path: a key and the ids of the target.
        Assert.All(today.Todo.Items.Where(i => i.Action == HostTodoAction.ActivateProperty), i => Assert.NotNull(i.PropertyId));
        Assert.All(today.Todo.Items.Where(i => i.Action != HostTodoAction.ActivateProperty), i => Assert.NotNull(i.BookingId));
    }

    [Fact]
    public async Task Todo_WhenItIsDue_ComesFromTheStayOrTheRequest()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);

        var todo = (await NewService(db).GetTodayAsync(world.OrgWide, new HostTodayOptions())).Todo.Items;

        // The request is cancelled after its deadline: the answer is due by then.
        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.RespondToRequest),
            item => Assert.Equal(HostScopeScenario.Now.UtcDateTime.AddDays(1), item.DueAt));
        // The communication runs 24 hours from the start of the arrival day (1 October, midnight in Rome = 22:00Z of the 30th).
        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.SendAlloggiati),
            item => Assert.Equal(At(10, 1, 22), item.DueAt));
        // The data are needed by the day of the arrival (10 October, midnight in Rome).
        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.CompleteGuestCheckIn),
            item => Assert.Equal(At(10, 9, 22), item.DueAt));
        Assert.All(todo.Where(i => i.Action is HostTodoAction.ConfirmPropertyReady or HostTodoAction.ActivateProperty), item => Assert.Null(item.DueAt));
    }

    [Fact]
    public async Task Todo_WhatEachItemLacks_IsTheCockpitsAnswer()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);

        var todo = (await NewService(db).GetTodayAsync(world.OrgWide, new HostTodayOptions())).Todo.Items;

        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.RespondToRequest),
            item => Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.ApprovalNotAnswered, "response")], item.Missing));
        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.SendAlloggiati),
            item => Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.AlloggiatiNotSent, "alloggiati")], item.Missing));
        // The booker is the only guest the stay has, and has none of the fields of the record.
        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.CompleteGuestCheckIn),
            item => Assert.Contains(item.Missing, m => m.Code == ComplianceMissingCodes.GuestFieldMissing && m.Field == "documentNumber"));
        Assert.All(
            todo.Where(i => i.Action == HostTodoAction.ActivateProperty),
            item => Assert.Contains(item.Missing, m => m.Code == "activation_cin_missing" && m.Field == "cin"));
    }

    [Fact]
    public async Task Scope_ACollaboratorLimitedToSomePropertiesSeesTheDayOfItsPropertyOnly()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = NewService(db);

        var restricted = await service.GetTodayAsync(world.Restricted, new HostTodayOptions());
        var orgWide = await service.GetTodayAsync(world.OrgWide, new HostTodayOptions());

        Assert.Equal(5, restricted.Todo.Count);
        Assert.Equal(10, orgWide.Todo.Count);
        Assert.Equal(1, restricted.Approvals.Count);
        Assert.Equal([world.Granted.Id], restricted.Approvals.Items.Select(a => a.PropertyId));
        Assert.All(
            restricted.Todo.Items.Where(i => i.PropertyId is not null),
            item => Assert.Equal(world.Granted.Id, item.PropertyId));
        // The stays of the hidden property are in none of the lists, nor are those of another org in the whole org's.
        var hiddenBookings = await db.Bookings.Where(b => b.PropertyId == world.Hidden.Id).Select(b => b.Id).ToListAsync();
        Assert.Empty(restricted.Todo.Items.Select(i => i.BookingId ?? Guid.Empty).Intersect(hiddenBookings));
        Assert.Empty(restricted.Upcoming.Items.Select(s => s.Stay.BookingId).Intersect(hiddenBookings));
        var otherOrgBookings = await db.Bookings.Where(b => b.PropertyId == world.OtherOrgProperty.Id).Select(b => b.Id).ToListAsync();
        Assert.Empty(orgWide.Todo.Items.Select(i => i.BookingId ?? Guid.Empty).Intersect(otherOrgBookings));
    }

    [Fact]
    public async Task Approvals_TheRequestsWaitingForTheHost_TheOnesToExpireFirst_WithTheTimeLeft()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        var late = Request(property, guest, expiresAt: At(10, 10, 18));
        var soon = Request(property, guest, expiresAt: At(10, 9, 14));
        // Not for the host: the guest has not confirmed the email, or the time to answer ran out.
        var unconfirmed = Request(property, guest, expiresAt: At(10, 9, 14));
        unconfirmed.GuestEmailVerifiedAt = null;
        var expired = Request(property, guest, expiresAt: At(10, 9, 9));
        db.Bookings.AddRange(late, soon, unconfirmed, expired);
        await db.SaveChangesAsync();

        var today = await NewService(db).GetTodayAsync(new HostScope(orgId), new HostTodayOptions());

        Assert.Equal([soon.Id, late.Id], today.Approvals.Items.Select(a => a.BookingId));
        Assert.Equal(2, today.Approvals.Count);
        Assert.Equal(At(10, 9, 14), today.Approvals.Items[0].RespondBy);
        Assert.Equal("Anna Verdi", today.Approvals.Items[0].GuestName);
        Assert.Equal("Trullo", today.Approvals.Items[0].PropertyName);
        // The one to expire first is the first thing to do, with its deadline.
        var first = today.Todo.Items[0];
        Assert.Equal((HostTodoAction.RespondToRequest, soon.Id, At(10, 9, 14)), (first.Action, first.BookingId, first.DueAt));
    }

    [Fact]
    public async Task Todo_OnlyTheFirstTwentyAreListed_TheCountCoversAll()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.AddRange(Enumerable.Range(0, 25).Select(i => Request(property, guest, expiresAt: At(10, 9, 12).AddMinutes(i))));
        await db.SaveChangesAsync();

        var today = await NewService(db).GetTodayAsync(new HostScope(orgId), new HostTodayOptions());

        Assert.Equal(25, today.Approvals.Count);
        Assert.Equal(HostTodayService.ApprovalListSize, today.Approvals.Items.Count);
        Assert.Equal(25, today.Todo.Count);
        Assert.Equal(HostTodayService.TodoListSize, today.Todo.Items.Count);
        // The nearest deadlines are the ones listed.
        Assert.Equal(today.Todo.Items.Select(i => i.DueAt).Order(), today.Todo.Items.Select(i => i.DueAt));
    }

    private static Booking Request(Property property, Guest guest, DateTime expiresAt) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        GuestId = guest.Id,
        Status = BookingStatus.Pending,
        Source = BookingSource.Direct,
        PaymentOption = PaymentOption.OnSite,
        GuestEmailVerifiedAt = At(10, 8, 12),
        RequestExpiresAt = expiresAt,
        CheckInDate = Day(11, 20),
        CheckOutDate = Day(11, 23),
        NumberOfGuests = 2,
    };

    // --- Failed payments: only for who may read payments ----------------------------------------------

    private static async Task<(AppDbContext Db, Guid OrgId, Booking Failed, Booking Paid, Booking Cancelled, Property Property)> SeedPaymentsAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        db.Properties.Add(property);
        db.Guests.Add(guest);

        Booking Confirmed(BookingStatus status, int dayOfOctober) => new()
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            Source = BookingSource.Direct,
            CheckInDate = Day(10, dayOfOctober),
            CheckOutDate = Day(10, dayOfOctober + 2),
            NumberOfGuests = 2,
        };

        Payment Pay(Booking booking, PaymentStatus status, int minute) => new()
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Status = status,
            Amount = 100m,
            CreatedAt = At(10, 5, 10, minute),
            UpdatedAt = At(10, 5, 10, minute),
        };

        var failed = Confirmed(BookingStatus.Confirmed, 20);
        var paid = Confirmed(BookingStatus.Confirmed, 22);
        var cancelled = Confirmed(BookingStatus.Cancelled, 24);
        db.Bookings.AddRange(failed, paid, cancelled);
        // A card declined twice: one thing to do for the stay, pointing at the latest attempt. Failed, then paid: nothing to do. A
        // cancelled stay is not chased.
        db.Payments.AddRange(
            Pay(failed, PaymentStatus.Failed, 1),
            Pay(failed, PaymentStatus.Failed, 2),
            Pay(paid, PaymentStatus.Failed, 1),
            Pay(paid, PaymentStatus.Completed, 2),
            Pay(cancelled, PaymentStatus.Failed, 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, orgId, failed, paid, cancelled, property);
    }

    [Fact]
    public async Task FailedPayments_AConfirmedStayWhosePaymentFailedAndNothingPaid_IsOneThingToDo()
    {
        var (db, orgId, failed, _, _, property) = await SeedPaymentsAsync();
        await using var disposable = db;

        var today = await NewService(db).GetTodayAsync(new HostScope(orgId), new HostTodayOptions(IncludePayments: true));

        var item = Assert.Single(today.Todo.Items);
        Assert.Equal(HostTodoAction.ReviewFailedPayment, item.Action);
        Assert.Equal(5, item.Priority);
        Assert.Equal(failed.Id, item.BookingId);
        Assert.Equal(property.Id, item.PropertyId);
        Assert.Equal("Trullo", item.PropertyName);
        Assert.Equal("Anna Verdi", item.Label);
        Assert.Equal(
            await db.Payments.Where(p => p.BookingId == failed.Id).OrderByDescending(p => p.UpdatedAt).Select(p => p.Id).FirstAsync(),
            item.PaymentId);
        Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.PaymentFailed, "payment")], item.Missing);
        Assert.Equal(1, today.Todo.Count);
    }

    [Fact]
    public async Task FailedPayments_AreLeftOutForWhoMayNotReadPayments()
    {
        var (db, orgId, _, _, _, _) = await SeedPaymentsAsync();
        await using var disposable = db;

        var today = await NewService(db).GetTodayAsync(new HostScope(orgId), new HostTodayOptions(IncludePayments: false));

        Assert.Empty(today.Todo.Items);
        Assert.Equal(0, today.Todo.Count);
    }

    // --- The stays of the day and their online check-in ------------------------------------------------

    private static GuestCheckInSession Session(Booking booking, GuestCheckInSessionStatus status, DateTime expiresAt, GuestCheckInLinkEmailStatus? email = GuestCheckInLinkEmailStatus.Sent, DateTime? createdAt = null)
    {
        return new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = status,
            ExpiresAt = expiresAt,
            LinkEmailStatus = email,
            CreatedAt = createdAt ?? At(10, 6, 8),
            UpdatedAt = createdAt ?? At(10, 6, 8),
        };
    }

    [Fact]
    public async Task Arrivals_EachStayCarriesHowFarItsOnlineCheckInIs()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        db.Properties.Add(property);

        Booking Arrival(string last, BookingStatus status = BookingStatus.Confirmed)
        {
            var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = last, Email = $"{Guid.NewGuid():N}@example.com" };
            db.Guests.Add(guest);
            var booking = new Booking
            {
                OrgId = orgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                Status = status,
                Source = BookingSource.Direct,
                CheckInDate = Day(10, 9),
                CheckOutDate = Day(10, 12),
                NumberOfGuests = 1,
            };
            db.Bookings.Add(booking);
            return booking;
        }

        var none = Arrival("NessunLink");
        var sent = Arrival("Inviato");
        var inProgress = Arrival("InCompilazione");
        var completed = Arrival("Completo");
        var expired = Arrival("Scaduto");
        var failedEmail = Arrival("EmailFallita");
        var enteredByHost = Arrival("DatiDelTitolare");
        // The link of "expired" is past its validity: an open link is expired even before the job marks it so.
        var future = At(10, 15, 8);
        db.GuestCheckInSessions.AddRange(
            Session(sent, GuestCheckInSessionStatus.Inviato, future),
            Session(inProgress, GuestCheckInSessionStatus.InCompilazione, future),
            Session(completed, GuestCheckInSessionStatus.Completo, future),
            Session(expired, GuestCheckInSessionStatus.Inviato, At(10, 8, 8)),
            Session(failedEmail, GuestCheckInSessionStatus.Inviato, future, GuestCheckInLinkEmailStatus.Failed));
        // The host entered every field of the record for this stay: no link is needed.
        db.StayGuests.Add(new StayGuest
        {
            BookingId = enteredByHost.Id,
            OrgId = orgId,
            GuestId = enteredByHost.GuestId,
            Position = 0,
            Type = StayGuestType.SingleGuest,
            FirstName = "Anna",
            LastName = "DatiDelTitolare",
            Gender = Gender.Female,
            DateOfBirth = Day(1, 2),
            BornInItaly = true,
            BirthComuneName = "Milano",
            BirthProvince = "MI",
            CitizenshipName = "Italia",
            DocumentType = GuestDocumentType.IdentityCard,
            DocumentNumber = "CA12345AB",
            DocumentIssuePlaceName = "Milano",
        });
        await db.SaveChangesAsync();

        var today = await NewService(db).GetTodayAsync(new HostScope(orgId), new HostTodayOptions());

        var states = today.Arrivals.Items.ToDictionary(s => s.Stay.BookingId, s => s.CheckIn!);
        Assert.Equal(HostTodayCheckInState.NotSent, states[none.Id].State);
        Assert.Equal(HostTodayCheckInState.Sent, states[sent.Id].State);
        Assert.Equal(HostTodayCheckInState.InProgress, states[inProgress.Id].State);
        Assert.Equal(HostTodayCheckInState.Completed, states[completed.Id].State);
        Assert.Equal(HostTodayCheckInState.Expired, states[expired.Id].State);
        Assert.Equal(HostTodayCheckInState.NotSent, states[failedEmail.Id].State);
        Assert.Equal(7, today.Arrivals.Count);
        // The data of the guests: only the stay whose record the host filled in is complete.
        Assert.Equal([enteredByHost.Id], states.Where(s => s.Value.DataComplete).Select(s => s.Key));
        Assert.Equal(HostTodayCheckInState.NotSent, states[enteredByHost.Id].State);
    }

    [Fact]
    public async Task Departures_HaveNoCheckInToFollow()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var leaving = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = BookingStatus.CheckedIn,
            Source = BookingSource.Manual,
            CheckInDate = Day(10, 6),
            CheckOutDate = Day(10, 9),
            NumberOfGuests = 2,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(leaving);
        await db.SaveChangesAsync();

        var today = await NewService(db).GetTodayAsync(new HostScope(orgId), new HostTodayOptions());

        var stay = Assert.Single(today.Departures.Items);
        Assert.Equal(leaving.Id, stay.Stay.BookingId);
        Assert.Null(stay.CheckIn);
        Assert.Equal(Day(10, 9), today.TodayInRome);
        // The departure is also a thing to do: close the stay.
        Assert.Contains(today.Todo.Items, i => i.Action == HostTodoAction.CheckOut && i.BookingId == leaving.Id);
    }

    // --- The state of a link, as a rule ----------------------------------------------------------------

    [Theory]
    [InlineData(GuestCheckInSessionStatus.Inviato, false, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.Sent)]
    [InlineData(GuestCheckInSessionStatus.Inviato, false, GuestCheckInLinkEmailStatus.NotRequested, HostTodayCheckInState.Sent)]
    [InlineData(GuestCheckInSessionStatus.Inviato, false, GuestCheckInLinkEmailStatus.Queued, HostTodayCheckInState.Sent)]
    [InlineData(GuestCheckInSessionStatus.Inviato, false, GuestCheckInLinkEmailStatus.Failed, HostTodayCheckInState.NotSent)]
    [InlineData(GuestCheckInSessionStatus.Inviato, true, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.Expired)]
    [InlineData(GuestCheckInSessionStatus.InCompilazione, false, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.InProgress)]
    [InlineData(GuestCheckInSessionStatus.InCompilazione, true, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.Expired)]
    [InlineData(GuestCheckInSessionStatus.Completo, false, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.Completed)]
    [InlineData(GuestCheckInSessionStatus.AlloggiatiInviato, true, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.Completed)]
    [InlineData(GuestCheckInSessionStatus.Scaduto, false, GuestCheckInLinkEmailStatus.Sent, HostTodayCheckInState.Expired)]
    public void StateOf_ALinkAndWhatHappenedToIt(
        GuestCheckInSessionStatus status, bool pastItsValidity, GuestCheckInLinkEmailStatus email, HostTodayCheckInState expected)
    {
        var now = At(10, 9, 10);
        var booking = new Booking();
        var session = Session(booking, status, pastItsValidity ? now.AddHours(-1) : now.AddDays(3), email);

        Assert.Equal(expected, HostTodayService.StateOf([session], now));
    }

    [Fact]
    public void StateOf_NoLink_IsNotSent_ASubmittedOneWins_ElseTheLatestOneTells()
    {
        var now = At(10, 9, 10);
        var booking = new Booking();
        var oldCompleted = Session(booking, GuestCheckInSessionStatus.Completo, now.AddDays(3), createdAt: now.AddDays(-5));
        var newerOpen = Session(booking, GuestCheckInSessionStatus.Inviato, now.AddDays(3), createdAt: now.AddDays(-1));
        var olderExpired = Session(booking, GuestCheckInSessionStatus.Scaduto, now.AddDays(-3), createdAt: now.AddDays(-6));

        Assert.Equal(HostTodayCheckInState.NotSent, HostTodayService.StateOf([], now));
        Assert.Equal(HostTodayCheckInState.Completed, HostTodayService.StateOf([newerOpen, oldCompleted], now));
        // A new link replaced the one that expired: the new one is what the guest has.
        Assert.Equal(HostTodayCheckInState.Sent, HostTodayService.StateOf([olderExpired, newerOpen], now));
    }
}

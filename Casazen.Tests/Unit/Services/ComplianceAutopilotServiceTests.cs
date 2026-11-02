using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// «Gestisci con AI»: what the assistant plans for the items of the cockpit and what it does when it runs. The cockpit itself is
/// <see cref="ComplianceWizardServiceTests"/>'s: here it is given, and the rules of the assistant are the subject. The collaborators that
/// send things (the check-in link, its e-mail, the check-out, the AI) are doubles that record what they were asked.
/// </summary>
public class ComplianceAutopilotServiceTests
{
    // 11:00 in Rome on 2 November 2026.
    private static readonly DateTimeOffset Instant = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Today = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly FixedTimeProvider Clock = new(Instant);

    private sealed class Rig
    {
        private readonly string _store = $"autopilot-{Guid.NewGuid():N}";

        public Rig()
        {
            Db = NewDb();
            Org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}" };
            Db.Orgs.Add(Org);
            Property = AddProperty("Casa Rossi");
            Locked = AddProperty("Casa Bianchi");
            Db.SaveChanges();
            Writable.Add(Property.Id);

            Wizard.Setup(w => w.GetSummaryAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Cockpit);
            Alloggiati
                .Setup(a => a.GetGuestSummaryAsync(It.IsAny<Guid>()))
                .ReturnsAsync((Guid id) => AlloggiatiSummaries.GetValueOrDefault(id) ?? Summary(id, dataComplete: true, exportReady: true));
            CheckIn
                .Setup(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
                .ReturnsAsync((Guid bookingId, Guid orgId) =>
                {
                    if (IssueFailures.TryGetValue(bookingId, out var failure))
                        throw failure;

                    Issued.Add(bookingId);
                    using var db = NewDb();
                    var session = new GuestCheckInSession
                    {
                        BookingId = bookingId,
                        OrgId = orgId,
                        TokenHash = $"hash-{Guid.NewGuid():N}",
                        ExpiresAt = Instant.UtcDateTime.AddDays(7),
                        CreatedAt = Instant.UtcDateTime,
                    };
                    db.GuestCheckInSessions.Add(session);
                    db.SaveChanges();
                    return new IssuedCheckInLink(session.Id, $"tok-{bookingId:N}", session.ExpiresAt);
                });
            Emails
                .Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => EmailOutcome);
            Lifecycle
                .Setup(l => l.CheckOutAsync(It.IsAny<Guid>(), It.IsAny<StayCheckOut>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid bookingId, StayCheckOut _, CancellationToken _) =>
                {
                    if (CloseFailures.TryGetValue(bookingId, out var failure))
                        throw failure;

                    Closed.Add(bookingId);
                    return new Booking { Id = bookingId };
                });
            Writer
                .Setup(w => w.WriteAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<AutopilotChatVariant>>(), It.IsAny<Func<bool>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, IReadOnlyCollection<AutopilotChatVariant> variants, Func<bool>? _, CancellationToken _) => Wording(variants));

            Service = NewService(Db);
        }

        public AppDbContext Db { get; }
        public OrgEntity Org { get; }

        /// <summary>The property the caller may write on.</summary>
        public Property Property { get; }

        /// <summary>The property the caller may not write on.</summary>
        public Property Locked { get; }

        public ComplianceAutopilotService Service { get; }
        public Mock<IComplianceWizardService> Wizard { get; } = new();
        public Mock<IAlloggiatiWebService> Alloggiati { get; } = new();
        public Mock<IGuestCheckInService> CheckIn { get; } = new();
        public Mock<IGuestCheckInLinkEmailQueue> Emails { get; } = new();
        public Mock<IStayLifecycleService> Lifecycle { get; } = new();
        public Mock<IAutopilotMessageWriter> Writer { get; } = new();

        public HashSet<Guid> Writable { get; } = [];
        public ComplianceSummaryResult Cockpit { get; set; } = CockpitOf(null, null, null, null);
        public Dictionary<Guid, AlloggiatiGuestSummaryInfo> AlloggiatiSummaries { get; } = [];
        public Dictionary<Guid, Exception> IssueFailures { get; } = [];
        public Dictionary<Guid, Exception> CloseFailures { get; } = [];
        public List<Guid> Issued { get; } = [];
        public List<Guid> Closed { get; } = [];
        public GuestCheckInLinkEmailOutcome EmailOutcome { get; set; } = new(GuestCheckInLinkEmailStatus.Queued);

        public AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_store).Options);

        public ComplianceAutopilotService NewService(AppDbContext db, bool emailConfigured = true) => new(
            db,
            Wizard.Object,
            Alloggiati.Object,
            CheckIn.Object,
            Emails.Object,
            Lifecycle.Object,
            Writer.Object,
            AiOff(),
            EmailTestHelpers.Links(),
            emailConfigured ? EmailTestHelpers.ConfiguredEmail() : Microsoft.Extensions.Options.Options.Create(new EmailOptions()),
            NullLogger<ComplianceAutopilotService>.Instance,
            Clock);

        public AutopilotCaller Caller(Func<bool>? permit = null) =>
            new((propertyId, _) => Task.FromResult(Writable.Contains(propertyId)), permit);

        public HostScope Scope => new(Org.Id);

        private Property AddProperty(string name)
        {
            var property = new Property
            {
                OrgId = Org.Id,
                OwnerId = "auth0|owner",
                Name = name,
                Address = "Via Roma 1",
                City = "Rome",
                PostalCode = "00100",
                Bedrooms = 2,
                Bathrooms = 1,
                MaxGuests = 4,
                NightlyRate = 100,
                IsActive = true,
            };
            Db.Properties.Add(property);
            return property;
        }

        public Booking AddStay(
            string firstName,
            BookingStatus status,
            int checkInOffset,
            int checkOutOffset,
            string email = "guest@example.com",
            string phone = "",
            string country = "",
            Property? property = null)
        {
            property ??= Property;
            var guest = new Guest
            {
                OrgId = Org.Id,
                FirstName = firstName,
                LastName = "Verdi",
                Email = email,
                PhoneNumber = phone,
                Country = country,
            };
            var booking = new Booking
            {
                PropertyId = property.Id,
                OrgId = Org.Id,
                GuestId = guest.Id,
                CheckInDate = Today.AddDays(checkInOffset),
                CheckOutDate = Today.AddDays(checkOutOffset),
                NumberOfGuests = 2,
                Status = status,
                BasePrice = 100,
                TotalPrice = 100,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.SaveChanges();
            return booking;
        }

        public void AddSession(Booking booking, DateTime createdAt, GuestCheckInSessionStatus status = GuestCheckInSessionStatus.Scaduto)
        {
            Db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = Org.Id,
                TokenHash = $"hash-{Guid.NewGuid():N}",
                ExpiresAt = createdAt.AddDays(7),
                CreatedAt = createdAt,
                Status = status,
            });
            Db.SaveChanges();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static IAiRuntimeSettings AiOff()
    {
        var settings = new Mock<IAiRuntimeSettings>();
        settings.Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Casazen.Core.Options.AiOptions());
        return settings.Object;
    }

    private static AutopilotWording Wording(IReadOnlyCollection<AutopilotChatVariant> variants, bool ai = false) => new(
        variants.DistinctBy(v => v.Key).ToDictionary(
            v => v.Key,
            v => new AutopilotWrittenText(ai ? $"Ciao {{nome}}, scrivo io il testo per {{struttura}}. {{link}}" : AutopilotChatText.Fallback(v.Language, v.Reminder), ai)),
        ai,
        ai ? null : AiSuggestionFallbackReasons.AiNotConfigured);

    private static AlloggiatiGuestSummaryInfo Summary(Guid bookingId, bool dataComplete, bool exportReady, int stayDays = 3) => new(
        bookingId,
        AlloggiatiWebStatus.DaInviareManualmente,
        Today.AddDays(-stayDays),
        stayDays,
        stayDays > 30,
        2,
        [],
        dataComplete,
        exportReady,
        []);

    private static ComplianceSummarySection Section(IReadOnlyList<ComplianceSummaryItem>? items) =>
        new(items?.Count ?? 0, items ?? []);

    private static ComplianceSummaryResult CockpitOf(
        IReadOnlyList<ComplianceSummaryItem>? incomplete,
        IReadOnlyList<ComplianceSummaryItem>? departures,
        IReadOnlyList<ComplianceSummaryItem>? alloggiatiManual,
        IReadOnlyList<ComplianceSummaryItem>? alloggiatiFailed) => new(
        Section(null),
        Section(incomplete),
        Section(departures),
        Section(alloggiatiFailed),
        Section(alloggiatiManual),
        Section(null));

    private static ComplianceSummaryItem Item(ComplianceCockpitAction action, Booking booking, DateTime? dueAt = null) =>
        ComplianceSummaryItem.ForBooking(action, booking.Id, "Anna Verdi") with { DueAt = dueAt };

    private static ComplianceSummaryItem[] Items(ComplianceCockpitAction action, params Booking[] bookings) =>
        bookings.Select(b => Item(action, b)).ToArray();

    private static AutopilotItem Only(AutopilotPlan plan, AutopilotTopic topic, Booking booking) =>
        Assert.Single(plan.Items, i => i.Topic == topic && i.BookingId == booking.Id);

    private const string Phone = "+39 333 123 4567";

    // ─── The plan ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_NothingInTheCockpit_IsEmpty()
    {
        var rig = new Rig();

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.Empty(plan.Items);
        Assert.False(plan.AiAvailable);
        Assert.Equal(0, plan.BeyondLimit);
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_AStayWithAnEmailAndANumber_IsReadyByBothWays()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, phone: Phone);
        var due = Today.AddDays(-1);
        rig.Cockpit = CockpitOf([Item(ComplianceCockpitAction.CompleteGuestCheckIn, stay, due)], null, null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.GuestData, stay);

        Assert.Equal(AutopilotItemStatus.Ready, item.Status);
        Assert.Equal(AutopilotReasons.AskGuests, item.Reason);
        Assert.Equal(new[] { AutopilotChannels.Email, AutopilotChannels.Chat }, item.Channels);
        Assert.Equal($"GuestData:{stay.Id:N}", item.Key);
        Assert.Equal("Anna Verdi", item.GuestName);
        Assert.Equal("Casa Rossi", item.PropertyName);
        Assert.Equal(rig.Property.Id, item.PropertyId);
        Assert.Equal(due, item.DueAt);
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_WhenCasazenCannotSendEmail_TheChatIsTheOnlyWay()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, phone: Phone);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var plan = await rig.NewService(rig.Db, emailConfigured: false).PlanAsync(rig.Scope, rig.Caller());

        Assert.Equal(new[] { AutopilotChannels.Chat }, Only(plan, AutopilotTopic.GuestData, stay).Channels);
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_AskedAnHourAgo_Waits()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        rig.AddSession(stay, Instant.UtcDateTime.AddHours(-1), GuestCheckInSessionStatus.Inviato);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.GuestData, stay);

        Assert.Equal((AutopilotItemStatus.Waiting, AutopilotReasons.AskedRecently), (item.Status, item.Reason));
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_AskedYesterday_IsAskedAgain()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        rig.AddSession(stay, Instant.UtcDateTime.AddHours(-25));
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.GuestData, stay);

        Assert.Equal((AutopilotItemStatus.Ready, AutopilotReasons.AskGuests), (item.Status, item.Reason));
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_AskedThreeTimes_IsTheHostsToChase()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        for (var days = 10; days >= 1; days -= 4)
            rig.AddSession(stay, Instant.UtcDateTime.AddDays(-days));
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.GuestData, stay);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.AskedEnough), (item.Status, item.Reason));
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_TheGuestHasSubmitted_IsTheHostsToCorrect()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        rig.AddSession(stay, Instant.UtcDateTime.AddDays(-3), GuestCheckInSessionStatus.Completo);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.GuestData, stay);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.AlreadySubmitted), (item.Status, item.Reason));
    }

    [Fact]
    public async Task Plan_GuestDataToAsk_WithoutAnyWayToReachTheGuest_IsTheHosts()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, email: "not-an-email");
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.GuestData, stay);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.NoContact), (item.Status, item.Reason));
    }

    [Fact]
    public async Task Plan_AStayOfAPropertyTheCallerCannotWriteOn_IsTheHosts_NeverReady()
    {
        var rig = new Rig();
        var asking = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, property: rig.Locked);
        var departing = rig.AddStay("Bruno", BookingStatus.CheckedIn, -9, -3, property: rig.Locked);
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, asking),
            Items(ComplianceCockpitAction.CheckOut, departing),
            null,
            null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.All(plan.Items, i => Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.NoPermission), (i.Status, i.Reason)));
        Assert.Equal(2, plan.Items.Count);
    }

    [Fact]
    public async Task Plan_ADeparturePastWithTheDataOfTheGuestsComplete_IsReadyToClose()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        rig.Cockpit = CockpitOf(null, Items(ComplianceCockpitAction.CheckOut, stay), null, null);

        var item = Only(await rig.Service.PlanAsync(rig.Scope, rig.Caller()), AutopilotTopic.Departure, stay);

        Assert.Equal((AutopilotItemStatus.Ready, AutopilotReasons.CloseDeparture), (item.Status, item.Reason));
    }

    [Fact]
    public async Task Plan_ADepartureWhoseGuestsAreBeingAsked_WaitsForTheirAnswer()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay),
            Items(ComplianceCockpitAction.CheckOut, stay),
            null,
            null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.Equal(AutopilotItemStatus.Ready, Only(plan, AutopilotTopic.GuestData, stay).Status);
        var departure = Only(plan, AutopilotTopic.Departure, stay);
        Assert.Equal((AutopilotItemStatus.Waiting, AutopilotReasons.AwaitingGuestData), (departure.Status, departure.Reason));
    }

    [Fact]
    public async Task Plan_ADepartureWhoseGuestsCannotBeAskedAnyMore_IsClosedAnyway()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3, email: "nope");
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay),
            Items(ComplianceCockpitAction.CheckOut, stay),
            null,
            null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.Equal(AutopilotItemStatus.Host, Only(plan, AutopilotTopic.GuestData, stay).Status);
        Assert.Equal(AutopilotItemStatus.Ready, Only(plan, AutopilotTopic.Departure, stay).Status);
    }

    [Fact]
    public async Task Plan_TodaysDeparture_AndABookingWhoseArrivalWasNeverRegistered_AreTheHosts()
    {
        var rig = new Rig();
        var today = rig.AddStay("Anna", BookingStatus.CheckedIn, -2, 0);
        var confirmed = rig.AddStay("Bruno", BookingStatus.Confirmed, -2, 0);
        rig.Cockpit = CockpitOf(null, Items(ComplianceCockpitAction.CheckOut, today, confirmed), null, null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        var one = Only(plan, AutopilotTopic.Departure, today);
        var two = Only(plan, AutopilotTopic.Departure, confirmed);
        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.DepartureToday), (one.Status, one.Reason));
        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.ArrivalNotRegistered), (two.Status, two.Reason));
    }

    [Fact]
    public async Task Plan_Alloggiati_IsAlwaysTheHosts_AndSaysWhetherTheDataAreReadyForThePortal()
    {
        var rig = new Rig();
        var ready = rig.AddStay("Anna", BookingStatus.CheckedOut, -9, -6);
        var incomplete = rig.AddStay("Bruno", BookingStatus.CheckedOut, -9, -6);
        var codes = rig.AddStay("Carla", BookingStatus.CheckedOut, -9, -6);
        var rejected = rig.AddStay("Dario", BookingStatus.CheckedOut, -9, -6);
        rig.AlloggiatiSummaries[incomplete.Id] = Summary(incomplete.Id, dataComplete: false, exportReady: false);
        rig.AlloggiatiSummaries[codes.Id] = Summary(codes.Id, dataComplete: true, exportReady: false);
        rig.Cockpit = CockpitOf(
            null,
            null,
            Items(ComplianceCockpitAction.SendAlloggiati, ready, incomplete, codes),
            Items(ComplianceCockpitAction.ResolveAlloggiatiFailure, rejected));

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.All(plan.Items, i => Assert.Equal(AutopilotItemStatus.Host, i.Status));
        Assert.Equal(AutopilotReasons.ReadyToUpload, Only(plan, AutopilotTopic.Alloggiati, ready).Reason);
        Assert.Equal(AutopilotReasons.DataMissing, Only(plan, AutopilotTopic.Alloggiati, incomplete).Reason);
        Assert.Equal(AutopilotReasons.CodesToComplete, Only(plan, AutopilotTopic.Alloggiati, codes).Reason);
        Assert.Equal(AutopilotReasons.Failed, Only(plan, AutopilotTopic.Alloggiati, rejected).Reason);
    }

    [Fact]
    public async Task Plan_AStayWhoseGuestsAreAskedIsOneItem_NotAlsoAnAlloggiatiOne()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay),
            null,
            Items(ComplianceCockpitAction.SendAlloggiati, stay),
            null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.Equal(AutopilotTopic.GuestData, Assert.Single(plan.Items).Topic);
    }

    [Fact]
    public async Task Plan_ItemsAreMostOverdueFirst()
    {
        var rig = new Rig();
        var late = rig.AddStay("Late", BookingStatus.CheckedIn, -9, -3);
        var later = rig.AddStay("Later", BookingStatus.CheckedIn, -9, -3);
        var undated = rig.AddStay("Undated", BookingStatus.CheckedIn, -9, -3);
        rig.Cockpit = CockpitOf(
            [Item(ComplianceCockpitAction.CompleteGuestCheckIn, later, Today.AddDays(-2)), Item(ComplianceCockpitAction.CompleteGuestCheckIn, undated), Item(ComplianceCockpitAction.CompleteGuestCheckIn, late, Today.AddDays(-30))],
            null,
            null,
            null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.Equal(new[] { late.Id, later.Id, undated.Id }, plan.Items.Select(i => i.BookingId));
    }

    [Fact]
    public async Task Plan_MoreReadyItemsThanOneRunReaches_AreCounted()
    {
        var rig = new Rig();
        var asking = Enumerable.Range(0, AutopilotLimits.MaxGuestRequestsPerRun + 3)
            .Select(i => rig.AddStay($"G{i}", BookingStatus.CheckedIn, -1, 2))
            .ToArray();
        var departing = Enumerable.Range(0, AutopilotLimits.MaxDeparturesPerRun + 2)
            .Select(i => rig.AddStay($"D{i}", BookingStatus.CheckedIn, -9, -3))
            .ToArray();
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, asking),
            Items(ComplianceCockpitAction.CheckOut, departing),
            null,
            null);

        var plan = await rig.Service.PlanAsync(rig.Scope, rig.Caller());

        Assert.Equal(5, plan.BeyondLimit);
    }

    // ─── The run ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_AsksTheGuestByEmail_AndPreparesTheMessageTheHostSends()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, phone: Phone);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal((AutopilotOutcomeStatus.Done, AutopilotOutcomeCodes.EmailQueued), (outcome.Status, outcome.Code));
        Assert.Equal("Queued", outcome.EmailStatus);
        Assert.Equal(new[] { stay.Id }, rig.Issued);
        rig.Emails.Verify(e => e.QueueAsync(It.IsAny<Guid>(), $"tok-{stay.Id:N}", It.IsAny<CancellationToken>()), Times.Once);

        var chat = outcome.Chat;
        Assert.NotNull(chat);
        Assert.Equal("it", chat.Language);
        Assert.False(chat.AiWritten);
        Assert.StartsWith("Ciao Anna, per completare la registrazione del tuo soggiorno a Casa Rossi", chat.Text, StringComparison.Ordinal);
        Assert.EndsWith($"{EmailTestHelpers.PublicSiteBaseUrl}/checkin/tok-{stay.Id:N}", chat.Text, StringComparison.Ordinal);
        Assert.StartsWith("https://wa.me/393331234567?text=", chat.WhatsAppUrl, StringComparison.Ordinal);
        Assert.StartsWith("sms:+393331234567?&body=", chat.SmsUrl, StringComparison.Ordinal);
        rig.Lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_TheGuestIsAskedInHisOwnLanguage_ByReminderNumber()
    {
        var rig = new Rig();
        var german = rig.AddStay("Hans", BookingStatus.CheckedIn, -1, 2, phone: "+49 151 2345 6789", country: "Germany");
        var again = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, phone: Phone);
        rig.AddSession(again, Instant.UtcDateTime.AddDays(-5));
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, german, again), null, null, null);

        var permit = () => true;
        await rig.Service.RunAsync(rig.Scope, rig.Caller(permit), null);

        rig.Writer.Verify(
            w => w.WriteAsync(
                rig.Org.Id,
                It.Is<IReadOnlyCollection<AutopilotChatVariant>>(v => v.Select(x => x.Key).Order().SequenceEqual(new[] { "de-1", "it-2" })),
                permit,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_TheWordingOfTheAi_IsUsedAndSaid()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, phone: Phone);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);
        rig.Writer
            .Setup(w => w.WriteAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<AutopilotChatVariant>>(), It.IsAny<Func<bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<AutopilotChatVariant> variants, Func<bool>? _, CancellationToken _) => Wording(variants, ai: true));

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        Assert.True(result.AiUsed);
        Assert.Null(result.AiReason);
        var chat = Assert.Single(result.Outcomes).Chat;
        Assert.NotNull(chat);
        Assert.True(chat.AiWritten);
        Assert.StartsWith("Ciao Anna, scrivo io il testo per Casa Rossi.", chat.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WithoutANumber_ThereIsNoMessageToSend()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        Assert.Null(Assert.Single(result.Outcomes).Chat);
        rig.Writer.Verify(
            w => w.WriteAsync(It.IsAny<Guid>(), It.Is<IReadOnlyCollection<AutopilotChatVariant>>(v => v.Count == 0), It.IsAny<Func<bool>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_TheEmailThatCannotLeave_WithANumber_LeavesTheMessageForTheHost()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, phone: Phone);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);
        rig.EmailOutcome = new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.QueueFailed);

        var outcome = Assert.Single((await rig.Service.RunAsync(rig.Scope, rig.Caller(), null)).Outcomes);

        Assert.Equal((AutopilotOutcomeStatus.Done, AutopilotOutcomeCodes.ChatReady), (outcome.Status, outcome.Code));
        Assert.Equal(("Failed", GuestCheckInLinkEmailErrors.QueueFailed), (outcome.EmailStatus, outcome.EmailError));
        Assert.NotNull(outcome.Chat);
    }

    [Fact]
    public async Task Run_TheEmailThatCannotLeave_WithoutANumber_IsAFailure()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);
        rig.EmailOutcome = new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.Rejected);

        var outcome = Assert.Single((await rig.Service.RunAsync(rig.Scope, rig.Caller(), null)).Outcomes);

        Assert.Equal((AutopilotOutcomeStatus.Failed, AutopilotOutcomeCodes.EmailFailed), (outcome.Status, outcome.Code));
        Assert.Equal(GuestCheckInLinkEmailErrors.Rejected, outcome.EmailError);
        Assert.Null(outcome.Chat);
    }

    [Fact]
    public async Task Run_ClosesTheDeparturesThatAreOver_WithTheCheckOutOfTheHostsOwnButton()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        rig.Cockpit = CockpitOf(null, Items(ComplianceCockpitAction.CheckOut, stay), null, null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal((AutopilotOutcomeStatus.Done, AutopilotOutcomeCodes.DepartureClosed), (outcome.Status, outcome.Code));
        Assert.Equal(new[] { stay.Id }, rig.Closed);
        rig.Lifecycle.Verify(
            l => l.CheckOutAsync(stay.Id, It.Is<StayCheckOut>(c => !c.RegisterArrival && c.Turnover == null && c.Wizard == null), It.IsAny<CancellationToken>()),
            Times.Once);
        rig.CheckIn.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_NeverClosesAStayWhoseGuestsAreBeingAskedForTheirData()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay),
            Items(ComplianceCockpitAction.CheckOut, stay),
            null,
            null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        Assert.Equal(AutopilotTopic.GuestData, Assert.Single(result.Outcomes).Topic);
        Assert.Empty(rig.Closed);
    }

    [Fact]
    public async Task Run_NeverTouchesAStayOfAPropertyTheCallerCannotWriteOn()
    {
        var rig = new Rig();
        var asking = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2, property: rig.Locked);
        var departing = rig.AddStay("Bruno", BookingStatus.CheckedIn, -9, -3, property: rig.Locked);
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, asking),
            Items(ComplianceCockpitAction.CheckOut, departing),
            null,
            null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        Assert.Empty(result.Outcomes);
        Assert.Empty(rig.Issued);
        Assert.Empty(rig.Closed);
    }

    [Fact]
    public async Task Run_OnlyWhatTheHostWasShown_AndWhatIsNoLongerToDoIsSaid()
    {
        var rig = new Rig();
        var shown = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        var added = rig.AddStay("Bruno", BookingStatus.CheckedIn, -9, -3);
        var today = rig.AddStay("Carla", BookingStatus.CheckedIn, -2, 0);
        rig.Cockpit = CockpitOf(null, Items(ComplianceCockpitAction.CheckOut, shown, added, today), null, null);

        var result = await rig.Service.RunAsync(
            rig.Scope,
            rig.Caller(),
            [$"Departure:{shown.Id:N}", $"Departure:{today.Id:N}", "Departure:not-a-booking"]);

        Assert.Equal(new[] { shown.Id }, rig.Closed);
        Assert.Equal(2, result.Outcomes.Count);
        var skipped = Assert.Single(result.Outcomes, o => o.Status == AutopilotOutcomeStatus.Skipped);
        Assert.Equal((today.Id, AutopilotOutcomeCodes.NoLongerNeeded), (skipped.BookingId, skipped.Code));
    }

    [Fact]
    public async Task Run_AnItemThatFails_DoesNotStopTheOthers()
    {
        var rig = new Rig();
        var done = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        var submitted = rig.AddStay("Bruno", BookingStatus.CheckedIn, -1, 2);
        var departing = rig.AddStay("Carla", BookingStatus.CheckedIn, -9, -3);
        var broken = rig.AddStay("Dario", BookingStatus.CheckedIn, -9, -3);
        var other = rig.AddStay("Elena", BookingStatus.CheckedIn, -9, -3);
        rig.IssueFailures[submitted.Id] = new DomainConflictException("checkin_already_completed", "CheckInAlreadyCompleted");
        rig.CloseFailures[broken.Id] = new InvalidOperationException("boom");
        rig.Cockpit = CockpitOf(
            Items(ComplianceCockpitAction.CompleteGuestCheckIn, done, submitted),
            Items(ComplianceCockpitAction.CheckOut, departing, broken, other),
            null,
            null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        Assert.Equal(5, result.Outcomes.Count);
        var refused = result.Outcomes.Single(o => o.BookingId == submitted.Id);
        Assert.Equal((AutopilotOutcomeStatus.Failed, "checkin_already_completed"), (refused.Status, refused.ErrorCode));
        var unexpected = result.Outcomes.Single(o => o.BookingId == broken.Id);
        Assert.Equal((AutopilotOutcomeStatus.Failed, "unexpected"), (unexpected.Status, unexpected.ErrorCode));
        Assert.Equal(AutopilotOutcomeStatus.Done, result.Outcomes.Single(o => o.BookingId == done.Id).Status);
        Assert.Equal(new[] { departing.Id, other.Id }.Order(), rig.Closed.Order());
    }

    [Fact]
    public async Task Run_ADepartureAlreadyClosedByTheHost_IsSkipped_NotAFailure()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -9, -3);
        rig.CloseFailures[stay.Id] = new DomainConflictException(BookingErrorCodes.AlreadyCheckedOut, "BookingAlreadyCheckedOut");
        rig.Cockpit = CockpitOf(null, Items(ComplianceCockpitAction.CheckOut, stay), null, null);

        var outcome = Assert.Single((await rig.Service.RunAsync(rig.Scope, rig.Caller(), null)).Outcomes);

        Assert.Equal((AutopilotOutcomeStatus.Skipped, AutopilotOutcomeCodes.NoLongerNeeded), (outcome.Status, outcome.Code));
    }

    [Fact]
    public async Task Run_NoMoreGuestsThanTheLimit_AndTheRestIsCounted()
    {
        var rig = new Rig();
        var stays = Enumerable.Range(0, AutopilotLimits.MaxGuestRequestsPerRun + 3)
            .Select(i => rig.AddStay($"G{i}", BookingStatus.CheckedIn, -1, 2))
            .ToArray();
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stays), null, null, null);

        var result = await rig.Service.RunAsync(rig.Scope, rig.Caller(), null);

        Assert.Equal(AutopilotLimits.MaxGuestRequestsPerRun, result.Outcomes.Count);
        Assert.Equal(3, result.BeyondLimit);
        Assert.Equal(AutopilotLimits.MaxGuestRequestsPerRun, rig.Issued.Count);
    }

    [Fact]
    public async Task Run_TwoRunsAtTheSameTime_AskTheGuestOnce()
    {
        var rig = new Rig();
        var stay = rig.AddStay("Anna", BookingStatus.CheckedIn, -1, 2);
        rig.Cockpit = CockpitOf(Items(ComplianceCockpitAction.CompleteGuestCheckIn, stay), null, null, null);
        await using var otherDb = rig.NewDb();
        var other = rig.NewService(otherDb);

        await Task.WhenAll(
            rig.Service.RunAsync(rig.Scope, rig.Caller(), null),
            other.RunAsync(rig.Scope, rig.Caller(), null));

        Assert.Equal(new[] { stay.Id }, rig.Issued);
    }
}

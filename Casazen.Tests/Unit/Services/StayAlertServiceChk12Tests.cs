using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-52, NF-48, CHK-12, AC-NF52-6, AC-NF52-15): the new stay stages of the hourly job and what every stage leaves behind. Each stage is sent
/// once, the receipt that is missing is the engine's word, the short stay and the stay over 30 nights are alerted only for a property the channel manager sends
/// for, the "To do" is opened and closed with the condition, and with the module's flag off nothing of it happens. EF InMemory; the rest are mocks.
/// </summary>
public class StayAlertServiceChk12Tests : IDisposable
{
    // 6 November 2026, 10:00 in Rome (winter time, UTC+1): the hour of the receipt alert. The stay arrives on the 5th.
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime CheckIn = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly Mock<IAlloggiatiWebService> _alloggiati = new();
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IComplianceStatusService> _status = new();
    private readonly Mock<IPropertySenderStore> _senders = new();
    private readonly Mock<IComplianceAlertFollowUp> _followUp = new();
    private readonly Mock<IFeatureFlags> _flags = new();
    private readonly StayAlertOptions _options = new();

    private ComplianceSenderType _sender = ComplianceSenderType.ChannelManager;

    public StayAlertServiceChk12Tests()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(true);
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        _status
            .Setup(s => s.EvaluateAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceStatusWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComplianceStatusResult.Empty);
        _senders
            .Setup(s => s.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, id => new PropertySenderInfo(id, Guid.Empty, _sender, _sender, SenderSource.Stored, true, null, null)));
    }

    public void Dispose() => _db.Dispose();

    private StayAlertService Service(bool withModule = true) =>
        withModule
            ? new StayAlertService(
                _db,
                _alloggiati.Object,
                _notifications.Object,
                Options.Create(_options),
                Options.Create(new ComplianceOptions()),
                NullLogger<StayAlertService>.Instance,
                _clock,
                _flags.Object,
                _status.Object,
                _senders.Object,
                _followUp.Object)
            : new StayAlertService(
                _db,
                _alloggiati.Object,
                _notifications.Object,
                Options.Create(_options),
                Options.Create(new ComplianceOptions()),
                NullLogger<StayAlertService>.Instance,
                _clock);

    private async Task<Booking> SeedStayAsync(DateTime? checkOut = null, DateTime? checkIn = null)
    {
        var org = OrgTeamTestData.AddOrg(_db);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Villa Rosa",
            Address = "Via Test 1",
            City = "Roma",
            RentalMode = RentalMode.Short,
            IsActive = true,
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = checkIn ?? CheckIn,
            CheckOutDate = checkOut ?? CheckIn.AddDays(2),
            Status = BookingStatus.Confirmed,
        };
        _db.AddRange(property, guest, booking);
        await _db.SaveChangesAsync();
        return booking;
    }

    private void Status(Booking booking, AlloggiatiWebStatus status, bool dataComplete = true, DateTime? reportedAt = null) =>
        _alloggiati
            .Setup(a => a.GetStatusAsync(booking.Id))
            .ReturnsAsync(new AlloggiatiStatusInfo(
                booking.Id,
                status,
                null,
                null,
                reportedAt,
                AlloggiatiTermsDeadline(booking),
                false,
                0,
                false,
                dataComplete));

    private static DateTime AlloggiatiTermsDeadline(Booking booking) =>
        Casazen.Core.Regulatory.AlloggiatiTerms.DeadlineUtc(booking);

    /// <summary>The Alloggiati item of the stay as the engine builds it, with the verdict of the one rule class.</summary>
    private void EngineSays(Booking booking, AlloggiatiWebStatus status, DateTime communicatedAt, ComplianceProof? proof = null)
    {
        var deadline = Casazen.Core.Regulatory.AlloggiatiTerms.DeadlineUtc(booking);
        var verdict = ComplianceLightRules.Alloggiati(new AlloggiatiFacts
        {
            Sender = ComplianceSenderType.Manual,
            Status = status,
            StayNights = 2,
            DeadlineUtc = deadline,
            NowUtc = _clock.GetUtcNow().UtcDateTime,
            TodayRome = DateOnly.FromDateTime(Casazen.Core.Utilities.RomeCalendar.TodayAt(_clock.GetUtcNow())),
            CommunicatedAt = communicatedAt,
            Proof = proof,
        });
        var item = new ComplianceStatusItem(
            ComplianceItemKind.Alloggiati, verdict.Action, booking.PropertyId, booking.Id, null, "Villa Rosa", verdict.Verdict, deadline);
        _status
            .Setup(s => s.EvaluateAsync(It.Is<HostScope>(h => h.OrgId == booking.OrgId), It.IsAny<ComplianceStatusWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceStatusResult(
                [item],
                new ComplianceEvaluatedUniverse(new HashSet<Guid> { booking.Id }, new HashSet<Guid>(), new HashSet<Guid> { booking.PropertyId }, new HashSet<string>())));
    }

    private void VerifySent(Booking booking, StayAlertKind kind, Times times) =>
        _notifications.Verify(n => n.SendStayAlertAsync(It.Is<StayAlert>(a => a.BookingId == booking.Id && a.Kind == kind), It.IsAny<CancellationToken>()), times);

    private Task<List<StayAlertState>> StatesAsync(StayAlertType type) =>
        _db.StayAlertStates.AsNoTracking().Where(s => s.Type == type).ToListAsync();

    // ─── The receipt that is missing ───

    [Fact]
    public async Task RunAsync_TheCommunicationWasDeclaredAndNoReceiptIsArchived_TheHostIsToldTheDayAfterAt10_OnceOnly()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);
        EngineSays(booking, AlloggiatiWebStatus.InviatoManualmente, communicatedAt: CheckIn);

        var first = await Service().RunAsync();
        var second = await Service().RunAsync();

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        VerifySent(booking, StayAlertKind.ReceiptMissing, Times.Once());
        var state = Assert.Single(await StatesAsync(StayAlertType.ReceiptMissing));
        Assert.Equal((StayAlertSchedule.SingleStage, 1, CheckIn), (state.Stage, state.AlertCount, state.ReferenceDate));
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.ReceiptToRetrieve
                    && r.Action == ActivityAction.UploadAlloggiatiProof
                    && r.EntityType == InternalActivity.BookingEntityType
                    && r.EntityId == booking.Id
                    && r.PropertyId == booking.PropertyId
                    && r.Urgency == ActivityUrgency.High
                    && r.Reason == ComplianceReasonCodes.ReceiptMissing),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // A receipt is not a deadline: no popup.
        _followUp.Verify(f => f.PopupAsync(It.IsAny<ComplianceAlertPopup>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_BeforeTheHourOfTheDayAfter_NoReceiptAlertIsSent()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);
        EngineSays(booking, AlloggiatiWebStatus.InviatoManualmente, communicatedAt: CheckIn);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 8, 59, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        VerifySent(booking, StayAlertKind.ReceiptMissing, Times.Never());
    }

    [Fact]
    public async Task RunAsync_TheEngineSaysTheReceiptIsNotMissing_NoAlertIsSent_NeverACopyOfItsRule()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);
        // The proof is archived: the engine says green, whatever the report says.
        EngineSays(booking, AlloggiatiWebStatus.InviatoManualmente, CheckIn, new ComplianceProof("manual-proof:1", ComplianceEvidenceSources.ManualUpload));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        VerifySent(booking, StayAlertKind.ReceiptMissing, Times.Never());
        // And the "To do" of the receipt and of the communication of a stay whose proof is archived is closed.
        _followUp.Verify(
            f => f.CloseTodosAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Contains(ActivityType.ReceiptToRetrieve) && t.Contains(ActivityType.AlloggiatiDeadline)),
                InternalActivity.BookingEntityType,
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == booking.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ADayAfterTheHour_TheReceiptAlertIsDroppedNotSentAsABacklog()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        EngineSays(booking, AlloggiatiWebStatus.InviatoManualmente, communicatedAt: CheckIn);

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        VerifySent(booking, StayAlertKind.ReceiptMissing, Times.Never());
    }

    // ─── The short stay and the stay over 30 nights ───

    [Fact]
    public async Task RunAsync_AShortStayNotEntrustedToTheChannelManager_IsAlertedSixHoursBeforeTheArrival_Once()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(1));
        Status(booking, AlloggiatiWebStatus.DaInviare);
        // The arrival day starts at 23:00 UTC of the 4th (Rome, winter time): six hours before it is 17:00.
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero));

        var first = await Service().RunAsync();
        var second = await Service().RunAsync();

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        _notifications.Verify(
            n => n.SendStayAlertAsync(It.Is<StayAlert>(a => a.BookingId == booking.Id && a.Kind == StayAlertKind.ShortStayAtRisk && a.ShortStay), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Single(await StatesAsync(StayAlertType.ShortStayAtRisk));
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.AlloggiatiDeadline
                    && r.Action == ActivityAction.SendAlloggiatiManually
                    && r.Urgency == ActivityUrgency.High
                    && r.Reason == ComplianceReasonCodes.ShortStayAtRisk),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // It has a legal deadline: the follow-up decides, with the clock, whether it is within 24 hours, and raises the popup for the booking.
        _followUp.Verify(
            f => f.PopupAsync(
                It.Is<ComplianceAlertPopup>(p => p.Type == AlertType.AlloggiatiDeadline && p.EntityType == "Booking" && p.EntityId == booking.Id && p.PropertyId == booking.PropertyId),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_AShortStayWhoseGuestsAreEntrusted_IsNotAlerted()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(1));
        Status(booking, AlloggiatiWebStatus.DaInviare);
        _db.StayGuests.Add(new StayGuest { OrgId = booking.OrgId, BookingId = booking.Id, Position = 0, FirstName = "Ospite", LastName = "Uno" });
        _db.StayGuestOctorateLinks.Add(new StayGuestOctorateLink
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            Position = 0,
            OctorateGuestId = "TEST-0",
            PayloadHash = new string('0', StayGuestOctorateLink.PayloadHashLength),
            SentAsDraft = false,
            Status = OctorateSyncStatus.Sent,
            SentAt = Start.UtcDateTime,
        });
        await _db.SaveChangesAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        Assert.Empty(await StatesAsync(StayAlertType.ShortStayAtRisk));
    }

    [Fact]
    public async Task RunAsync_AShortStayEntrustedOnlyInPart_IsStillAtRisk()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(1));
        Status(booking, AlloggiatiWebStatus.DaInviare);
        foreach (var position in new[] { 0, 1 })
            _db.StayGuests.Add(new StayGuest { OrgId = booking.OrgId, BookingId = booking.Id, Position = position, FirstName = "Ospite", LastName = $"{position}" });

        // Only the first guest is on the channel manager for good; the second was sent as a draft.
        foreach (var (position, draft) in new[] { (0, false), (1, true) })
        {
            _db.StayGuestOctorateLinks.Add(new StayGuestOctorateLink
            {
                OrgId = booking.OrgId,
                BookingId = booking.Id,
                Position = position,
                OctorateGuestId = $"TEST-{position}",
                PayloadHash = new string('0', StayGuestOctorateLink.PayloadHashLength),
                SentAsDraft = draft,
                Status = OctorateSyncStatus.Sent,
                SentAt = Start.UtcDateTime,
            });
        }

        await _db.SaveChangesAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, (await Service().RunAsync()).Sent);
    }

    [Fact]
    public async Task RunAsync_AShortStayOfAPropertyTheHostSendsFor_OrWithTheConnectorOff_IsNotAlertedByThisStage()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(1));
        Status(booking, AlloggiatiWebStatus.DaInviare);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero));

        _sender = ComplianceSenderType.Manual;
        Assert.Equal(0, (await Service().RunAsync()).Sent);

        _sender = ComplianceSenderType.ChannelManager;
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(false);
        Assert.Equal(0, (await Service().RunAsync()).Sent);
        Assert.Empty(await StatesAsync(StayAlertType.ShortStayAtRisk));
    }

    [Fact]
    public async Task RunAsync_AStayOver30Nights_IsAlertedTheDayBeforeTheArrivalAt9_Once()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(31));
        Status(booking, AlloggiatiWebStatus.DaInviare);
        // The 4th at 09:00 in Rome (UTC+1) is 08:00 UTC.
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero));

        var first = await Service().RunAsync();
        var second = await Service().RunAsync();

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        VerifySent(booking, StayAlertKind.OverThirtyNights, Times.Once());
        Assert.Single(await StatesAsync(StayAlertType.OverThirtyNights));
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.AlloggiatiDeadline
                    && r.Action == ActivityAction.SendAlloggiatiManually
                    && r.Urgency == ActivityUrgency.Medium
                    && r.Reason == ComplianceReasonCodes.OverThirtyNightsNotSent),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_AStayOver30NightsAlreadySent_IsNotAlerted()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(31));
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, (await Service().RunAsync()).Sent);
    }

    // ─── The module's flag ───

    [Fact]
    public async Task RunAsync_WithTheModuleOff_NoNewStageAndNothingLeftBehind_AndTheOldStagesSendWhatTheySent()
    {
        var booking = await SeedStayAsync(checkOut: CheckIn.AddDays(1));
        Status(booking, AlloggiatiWebStatus.DaInviare, dataComplete: false);
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(false);
        // The day before the arrival at 10:00 in Rome: the old stage «guest data missing» is due, and so is nothing new.
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(1, result.Sent);
        VerifySent(booking, StayAlertKind.GuestDataMissing, Times.Once());
        _status.VerifyNoOtherCalls();
        _senders.VerifyNoOtherCalls();
        _followUp.VerifyNoOtherCalls();
        Assert.Empty(await StatesAsync(StayAlertType.ShortStayAtRisk));
    }

    [Fact]
    public async Task RunAsync_WithoutTheNewServices_BehavesAsItAlwaysDid()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.DaInviare, dataComplete: false);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

        var result = await Service(withModule: false).RunAsync();

        Assert.Equal(1, result.Sent);
        VerifySent(booking, StayAlertKind.GuestDataMissing, Times.Once());
    }

    // ─── What the old stages leave behind ───

    [Fact]
    public async Task RunAsync_TheDataOfTheGuestsAreMissingTheDayBefore_OpensTheCheckInTodo_WithTheArrivalAsItsDeadline()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.DaInviare, dataComplete: false);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(1, result.Sent);
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.CheckInPending
                    && r.Action == ActivityAction.StartCheckIn
                    && r.EntityType == InternalActivity.BookingEntityType
                    && r.EntityId == booking.Id
                    && r.Urgency == ActivityUrgency.Medium
                    && r.Reason == ComplianceReasonCodes.GuestDataMissing
                    && r.Deadline == Casazen.Core.Regulatory.AlloggiatiTerms.ArrivalDayStartUtc(CheckIn)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_TheDeadlineApproaching_OpensTheAlloggiatiTodoAndRaisesThePopup()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.DaInviareManualmente);
        // Noon of the arrival day in Rome (the unregistered arrival's deadline is the end of the day).
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(1, result.Sent);
        VerifySent(booking, StayAlertKind.AlloggiatiDeadlineApproaching, Times.Once());
        var deadline = Casazen.Core.Regulatory.AlloggiatiTerms.ArrivalDayStartUtc(CheckIn.AddDays(1));
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.AlloggiatiDeadline
                    && r.Action == ActivityAction.UploadAlloggiatiProof
                    && r.Urgency == ActivityUrgency.High
                    && r.Reason == ComplianceReasonCodes.AlloggiatiNotSent
                    && r.Deadline == deadline),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _followUp.Verify(
            f => f.PopupAsync(
                It.Is<ComplianceAlertPopup>(p => p.Type == AlertType.AlloggiatiDeadline && p.EntityId == booking.Id && p.DeadlineUtc == deadline),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_TheCheckoutReminder_LeavesNothingBehind()
    {
        var booking = await SeedStayAsync(checkIn: new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), checkOut: CheckIn.Date);
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, reportedAt: CheckIn);
        // The check-out day of the stay (the 5th) at 20:00 in Rome (UTC+1).
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 5, 19, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.True(result.Sent >= 1);
        VerifySent(booking, StayAlertKind.CheckoutReminder, Times.Once());
        _followUp.Verify(f => f.OpenTodoAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _followUp.Verify(f => f.PopupAsync(It.IsAny<ComplianceAlertPopup>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── What clears the condition ───

    [Fact]
    public async Task RunAsync_TheCommunicationWasSent_AndTheDataAreComplete_TheirTodoAreClosed_AndTheStaysThatOweNothingAreSwept()
    {
        var booking = await SeedStayAsync();
        Status(booking, AlloggiatiWebStatus.InviatoManualmente, dataComplete: true, reportedAt: CheckIn);

        await Service().RunAsync();

        _followUp.Verify(
            f => f.CloseTodosAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Single() == ActivityType.AlloggiatiDeadline),
                InternalActivity.BookingEntityType,
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == booking.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _followUp.Verify(
            f => f.CloseTodosAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Single() == ActivityType.CheckInPending),
                InternalActivity.BookingEntityType,
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == booking.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _followUp.Verify(
            f => f.CloseTodosOfStaysThatOweNothingAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Contains(ActivityType.CheckInPending)
                    && t.Contains(ActivityType.AlloggiatiDeadline)
                    && t.Contains(ActivityType.ReceiptToRetrieve)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ACancelledStayAndANoShow_GetNoAlertOfTheNewStages()
    {
        var cancelled = await SeedStayAsync(checkOut: CheckIn.AddDays(1));
        cancelled.Status = BookingStatus.Cancelled;
        var noShow = await SeedStayAsync(checkOut: CheckIn.AddDays(31));
        noShow.NoShowAt = Start.UtcDateTime;
        await _db.SaveChangesAsync();
        Status(cancelled, AlloggiatiWebStatus.DaInviare);
        Status(noShow, AlloggiatiWebStatus.DaInviare);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        _notifications.VerifyNoOtherCalls();
    }

    // ─── Add-only registrations ───

    [Fact]
    public async Task TheStayAlertsJob_RunsTheComplianceAlertsOnlyWhenTheStayAlertsWereNotSkipped()
    {
        var stay = new Mock<IStayAlertService>();
        var compliance = new Mock<IComplianceAlertService>();
        stay.SetupSequence(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StayAlertRunResult(Skipped: false, Sent: 0))
            .ReturnsAsync(new StayAlertRunResult(Skipped: true, Sent: 0));
        compliance.Setup(c => c.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ComplianceAlertRunResult(false, 0));
        var job = new Casazen.Web.BackgroundJobs.StayAlertsJob(stay.Object, compliance.Object);

        await job.ExecuteAsync();
        await job.ExecuteAsync();

        stay.Verify(s => s.RunAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        compliance.Verify(c => c.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

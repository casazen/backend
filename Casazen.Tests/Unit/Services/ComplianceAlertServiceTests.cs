using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F02/US02/T01 (NF-52, CHK-12, AC-NF52-6, AC-NF52-15): the alerts about a period and about the connection. Each stage is sent once and only once, a stage
/// that fails to be delivered is not repeated, the tourist tax with no deadline day stays silent, a period with its proof is never alerted and its "To do" is
/// closed, the connection alert follows the incident and starts again for the next one, and the module's flag turns it all off. EF InMemory; the engine, the
/// health of the connection, the senders and the delivery are mocks.
/// </summary>
public class ComplianceAlertServiceTests : IDisposable
{
    // 09:00 in Rome (winter time): the hour of the alerts of a period. The deadline day of October 2026 is 10 November.
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly Mock<IComplianceStatusService> _status = new();
    private readonly Mock<IComplianceConnectionHealthService> _health = new();
    private readonly Mock<IPropertySenderStore> _senders = new();
    private readonly Mock<IComplianceAlertRecipientResolver> _recipients = new();
    private readonly Mock<ITouristTaxDeadlineSource> _taxDeadlines = new();
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IComplianceAlertFollowUp> _followUp = new();
    private readonly Mock<IFeatureFlags> _flags = new();
    private readonly ComplianceOptions _options = new();

    private ConnectionHealthState _connection = ConnectionHealthState.Inactive;
    private ComplianceSenderType _sender = ComplianceSenderType.ChannelManager;

    public ComplianceAlertServiceTests()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(true);
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        _status
            .Setup(s => s.EvaluateAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceStatusWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ComplianceStatusResult.Empty);
        _recipients
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceAlertRecipients(["org@example.com"], ["auth0|owner"]));
        _taxDeadlines
            .Setup(t => t.DeadlineOfAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateOnly?)null);
        _senders
            .Setup(s => s.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, id => new PropertySenderInfo(id, Guid.Empty, _sender, _sender, SenderSource.Stored, true, null, null)));
        _health
            .Setup(h => h.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, id => HealthOf(id, _connection)));
    }

    public void Dispose() => _db.Dispose();

    private ComplianceAlertService Service() =>
        new(
            _db,
            _status.Object,
            _health.Object,
            _senders.Object,
            _recipients.Object,
            _taxDeadlines.Object,
            _notifications.Object,
            _followUp.Object,
            _flags.Object,
            Options.Create(_options),
            NullLogger<ComplianceAlertService>.Instance,
            _clock);

    private async Task<(Guid OrgId, Guid PropertyId)> SeedAsync()
    {
        var org = OrgTeamTestData.AddOrg(_db);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            RentalMode = RentalMode.Short,
            IsActive = true,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        return (org.Id, property.Id);
    }

    private async Task SeedStayAsync(Guid orgId, Guid propertyId, int daysAhead, BookingStatus status = BookingStatus.Confirmed, DateTime? noShowAt = null)
    {
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var checkIn = _clock.TodayInRome().AddDays(daysAhead);
        _db.AddRange(
            guest,
            new Booking
            {
                OrgId = orgId,
                PropertyId = propertyId,
                GuestId = guest.Id,
                CheckInDate = checkIn,
                CheckOutDate = checkIn.AddDays(2),
                Status = status,
                NoShowAt = noShowAt,
            });
        await _db.SaveChangesAsync();
    }

    private static ComplianceConnectionHealth HealthOf(Guid propertyId, ConnectionHealthState state, string? reason = null)
    {
        var line = new ConnectionHealthLine(ComplianceSenderType.ChannelManager, state, null, null, null, reason, null);
        return new ComplianceConnectionHealth(propertyId, true, line, line, false, false, Start.UtcDateTime);
    }

    /// <summary>The ISTAT item of October 2026 as the engine builds it: the verdict of the one rule class, the deadline day 10 November as its due instant.</summary>
    private static ComplianceStatusItem IstatItem(Guid propertyId, DateOnly today, bool proven = false)
    {
        var periodStart = new DateOnly(2026, 10, 1);
        var verdict = ComplianceLightRules.Istat(new IstatFacts
        {
            Sender = ComplianceSenderType.Manual,
            Today = today,
            PeriodStart = periodStart,
            DeadlineDay = 10,
            Proof = proven ? new ComplianceProof("manual-proof:1", ComplianceEvidenceSources.ManualUpload) : null,
        })!;
        return new ComplianceStatusItem(
            ComplianceItemKind.Istat,
            ComplianceCockpitAction.UploadIstatProof,
            propertyId,
            null,
            "2026-10",
            "Casa Test",
            verdict,
            RomeCalendar.StartOfDayUtc(ComplianceLightRules.IstatDeadline(periodStart, 10)));
    }

    private static ComplianceStatusItem TouristTaxItem(Guid propertyId)
    {
        var verdict = ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = new DateOnly(2026, 11, 7), PeriodStart = new DateOnly(2026, 10, 1) })!;
        return new ComplianceStatusItem(
            ComplianceItemKind.TouristTax, ComplianceCockpitAction.DeclareTouristTax, propertyId, null, "2026-10", "Casa Test", verdict, null);
    }

    private void Evaluation(Guid orgId, Guid propertyId, params ComplianceStatusItem[] items)
    {
        var universe = new ComplianceEvaluatedUniverse(
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            new HashSet<Guid> { propertyId },
            new HashSet<string>(StringComparer.Ordinal) { "2026-09", "2026-10" });
        _status
            .Setup(s => s.EvaluateAsync(It.Is<HostScope>(h => h.OrgId == orgId), It.IsAny<ComplianceStatusWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceStatusResult(items, universe));
    }

    private void VerifySent(ComplianceAlertKind kind, ComplianceAlertType type, Times times) =>
        _notifications.Verify(
            n => n.SendComplianceAlertAsync(
                It.Is<ComplianceAlert>(a => a.Kind == kind && a.Type == type),
                It.Is<IReadOnlyCollection<string>>(emails => emails.Single() == "org@example.com"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            times);

    // ─── The module's flag ───

    [Fact]
    public async Task RunAsync_TheModuleIsOff_DoesNothingAndWritesNothing()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 7)));
        _flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(false);

        var result = await Service().RunAsync();

        Assert.True(result.Skipped);
        Assert.Equal(0, result.Sent);
        _status.VerifyNoOtherCalls();
        _notifications.VerifyNoOtherCalls();
        _followUp.VerifyNoOtherCalls();
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
    }

    // ─── Periods ───

    [Fact]
    public async Task RunAsync_AnIstatMonthWithoutProof_IsAlertedOncePerStage_WithItsTodoAndItsPopup()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 7)));

        var first = await Service().RunAsync();
        var again = await Service().RunAsync();

        // The deadline is approaching: one alert, then nothing however many runs.
        Assert.Equal(1, first.Sent);
        Assert.Equal(0, again.Sent);
        VerifySent(ComplianceAlertKind.PeriodDeadlineApproaching, ComplianceAlertType.IstatPeriod, Times.Once());
        var state = await _db.ComplianceAlertStates.SingleAsync();
        Assert.Equal((propertyId, ComplianceAlertType.IstatPeriod, "2026-10", ComplianceAlertStage.DeadlineApproaching, 1), (state.PropertyId, state.Type, state.ReferenceKey, state.Stage, state.AlertCount));
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.IstatDeadline
                    && r.Action == ActivityAction.UploadIstatProof
                    && r.EntityType == "PropertyPeriod:2026-10"
                    && r.EntityId == propertyId
                    && r.PropertyId == propertyId
                    && r.Urgency == ActivityUrgency.Medium
                    && r.Reason == ComplianceReasonCodes.PeriodOpen
                    && r.Deadline == RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 11))),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _followUp.Verify(
            f => f.PopupAsync(
                It.Is<ComplianceAlertPopup>(p => p.OrgId == orgId
                    && p.PropertyId == propertyId
                    && p.Type == AlertType.IstatDeadline
                    && p.EntityType == "Property"
                    && p.EntityId == propertyId
                    && p.DeadlineUtc == RomeCalendar.StartOfDayUtc(new DateOnly(2026, 11, 11))),
                Start.UtcDateTime,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The morning after the deadline day the engine says red and the next stage is due: overdue, once.
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 11)));
        var overdue = await Service().RunAsync();
        var overdueAgain = await Service().RunAsync();

        Assert.Equal(1, overdue.Sent);
        Assert.Equal(0, overdueAgain.Sent);
        VerifySent(ComplianceAlertKind.PeriodOverdue, ComplianceAlertType.IstatPeriod, Times.Once());
        Assert.Equal(ComplianceAlertStage.Overdue, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.IstatDeadline && r.Urgency == ActivityUrgency.High && r.Reason == ComplianceReasonCodes.DeadlineOverdue),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_AnIstatMonthWithItsProof_IsNeverAlerted_AndItsTodoIsClosed()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 7), proven: true));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        _notifications.VerifyNoOtherCalls();
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
        _followUp.Verify(
            f => f.CloseTodosAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Single() == ActivityType.IstatDeadline),
                "PropertyPeriod:2026-10",
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == propertyId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_AnIstatMonthThatStillOwesItsProof_KeepsItsTodoOpen()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 7)));

        await Service().RunAsync();

        // The item is open: its "To do" is not among those closed, for the month it is about.
        _followUp.Verify(
            f => f.CloseTodosAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Single() == ActivityType.IstatDeadline),
                "PropertyPeriod:2026-10",
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_BeforeTheWarning_NothingIsSent()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 6)));
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        _notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_TheTouristTaxWithoutADeadlineDay_StaysSilent_NeverUsingAGuessedOne()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, TouristTaxItem(propertyId));
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        _notifications.VerifyNoOtherCalls();
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
        _taxDeadlines.Verify(t => t.DeadlineOfAsync(propertyId, new DateOnly(2026, 10, 1), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_TheTouristTaxWithADeadlineDay_FollowsTheSameStages_AsAComplianceIssue()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, TouristTaxItem(propertyId));
        _taxDeadlines
            .Setup(t => t.DeadlineOfAsync(propertyId, new DateOnly(2026, 10, 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DateOnly(2026, 11, 16));
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.Equal(1, result.Sent);
        VerifySent(ComplianceAlertKind.PeriodDeadlineApproaching, ComplianceAlertType.TouristTaxPeriod, Times.Once());
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.TouristTaxDeadline && r.Action == ActivityAction.DeclareTouristTax && r.EntityType == "PropertyPeriod:2026-10"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _followUp.Verify(
            f => f.PopupAsync(It.Is<ComplianceAlertPopup>(p => p.Type == AlertType.ComplianceIssue), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_AStageThatFailsToBeDelivered_IsClaimedAndNotRepeated_ButItsTodoIsStillOpened()
    {
        var (orgId, propertyId) = await SeedAsync();
        Evaluation(orgId, propertyId, IstatItem(propertyId, new DateOnly(2026, 11, 7)));
        _notifications
            .Setup(n => n.SendComplianceAlertAsync(It.IsAny<ComplianceAlert>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the queue is down"));

        var first = await Service().RunAsync();
        var second = await Service().RunAsync();

        Assert.Equal(0, first.Sent);
        Assert.Equal(0, second.Sent);
        _notifications.Verify(
            n => n.SendComplianceAlertAsync(It.IsAny<ComplianceAlert>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(ComplianceAlertStage.DeadlineApproaching, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
        _followUp.Verify(f => f.OpenTodoAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_AnOrgThatCannotBeProcessed_DoesNotStopTheOthers()
    {
        var (firstOrg, firstProperty) = await SeedAsync();
        var (secondOrg, secondProperty) = await SeedAsync();
        _status
            .Setup(s => s.EvaluateAsync(It.Is<HostScope>(h => h.OrgId == firstOrg), It.IsAny<ComplianceStatusWindow>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        Evaluation(secondOrg, secondProperty, IstatItem(secondProperty, new DateOnly(2026, 11, 7)));

        var result = await Service().RunAsync();

        Assert.Equal(1, result.Sent);
        Assert.Equal(secondProperty, (await _db.ComplianceAlertStates.SingleAsync()).PropertyId);
        Assert.NotEqual(firstProperty, secondProperty);
    }

    [Fact]
    public async Task ClaimAsync_TheSecondCallForTheSameStage_OrALowerOne_LosesTheCompareAndSet()
    {
        var (orgId, propertyId) = await SeedAsync();
        var approaching = ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.IstatPeriod, propertyId, "2026-10", new DateOnly(2026, 11, 10), _options, Start.UtcDateTime)!;
        var overdue = ComplianceAlertSchedule.DuePeriodStep(
            ComplianceAlertType.IstatPeriod, propertyId, "2026-10", new DateOnly(2026, 11, 10), _options, new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc))!;
        var service = Service();

        Assert.True(await service.ClaimAsync(orgId, approaching, Start.UtcDateTime, CancellationToken.None));
        Assert.False(await service.ClaimAsync(orgId, approaching, Start.UtcDateTime, CancellationToken.None));
        Assert.True(await service.ClaimAsync(orgId, overdue, Start.UtcDateTime, CancellationToken.None));
        Assert.False(await service.ClaimAsync(orgId, approaching, Start.UtcDateTime, CancellationToken.None));
        Assert.Equal(2, (await _db.ComplianceAlertStates.SingleAsync()).AlertCount);
    }

    // ─── Connection ───

    [Fact]
    public async Task RunAsync_AnInactiveConnectionWithAStayComingSoon_IsAlertedAtDetection_AndOnceMoreTwoDaysLater_ThenNever()
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 4);

        var first = await Service().RunAsync();
        var sameHour = await Service().RunAsync();

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, sameHour.Sent);
        VerifySent(ComplianceAlertKind.ConnectionInactive, ComplianceAlertType.ConnectionInactive, Times.Once());
        _followUp.Verify(
            f => f.OpenTodoAsync(
                It.Is<CreateTodoRequest>(r => r.Type == ActivityType.ReconnectChannelManager
                    && r.Action == ActivityAction.ActivateOctorateSend
                    && r.EntityType == "Property"
                    && r.EntityId == propertyId
                    && r.Urgency == ActivityUrgency.High
                    && r.Reason == ComplianceReasonCodes.ConnectionInactive),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // The connection is not a legal deadline: no popup.
        _followUp.Verify(f => f.PopupAsync(It.IsAny<ComplianceAlertPopup>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);

        _clock.Advance(TimeSpan.FromDays(2));
        var second = await Service().RunAsync();
        _clock.Advance(TimeSpan.FromDays(3));
        var never = await Service().RunAsync();

        Assert.Equal(1, second.Sent);
        Assert.Equal(0, never.Sent);
        _notifications.Verify(
            n => n.SendComplianceAlertAsync(
                It.Is<ComplianceAlert>(a => a.Kind == ComplianceAlertKind.ConnectionInactive && a.ReminderNumber == 1),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(ComplianceAlertStage.ConnectionReminder, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
    }

    [Fact]
    public async Task RunAsync_AnInactiveConnectionButNoStayComingSoon_IsNotAlerted()
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 9);

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        _notifications.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled, false)]
    [InlineData(BookingStatus.Pending, false)]
    [InlineData(BookingStatus.Confirmed, true)]
    public async Task RunAsync_OnlyAnActiveStayThatDidNotNoShowCounts(BookingStatus status, bool alerted)
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 2, status);

        var result = await Service().RunAsync();

        Assert.Equal(alerted ? 1 : 0, result.Sent);
    }

    [Fact]
    public async Task RunAsync_AStayThatIsANoShow_DoesNotCount()
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 2, BookingStatus.Confirmed, noShowAt: Start.UtcDateTime);

        Assert.Equal(0, (await Service().RunAsync()).Sent);
    }

    [Fact]
    public async Task RunAsync_WhenTheConnectorIsOff_OrTheHostSends_NothingIsReadOrAlerted()
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 2);

        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(false);
        Assert.Equal(0, (await Service().RunAsync()).Sent);
        _senders.VerifyNoOtherCalls();
        _health.VerifyNoOtherCalls();

        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        _sender = ComplianceSenderType.Manual;
        Assert.Equal(0, (await Service().RunAsync()).Sent);
        _health.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_APropertyWhoseConnectionWasNotReadThisTime_IsNeitherAlertedNorCleared()
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 2);
        _health
            .Setup(h => h.GetManyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                ids.ToDictionary(id => id, id => HealthOf(id, ConnectionHealthState.Unknown, ComplianceConnectionHealthRules.NotCheckedYetReason)));

        var result = await Service().RunAsync();

        Assert.Equal(0, result.Sent);
        _followUp.Verify(
            f => f.CloseTodosAsync(It.IsAny<IReadOnlyCollection<ActivityType>>(), "Property", It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenTheConnectionIsActiveAgain_TheStateStartsAgainAndTheTodoIsClosed_SoTheNextIncidentIsAlerted()
    {
        var (orgId, propertyId) = await SeedAsync();
        await SeedStayAsync(orgId, propertyId, daysAhead: 5);
        await Service().RunAsync();
        Assert.Equal(ComplianceAlertStage.ConnectionDetected, (await _db.ComplianceAlertStates.SingleAsync()).Stage);

        // The host fixes it: the send is active, the alert is cleared and its "To do" closed.
        _connection = ConnectionHealthState.Active;
        _clock.Advance(TimeSpan.FromHours(1));
        var cleared = await Service().RunAsync();

        Assert.Equal(0, cleared.Sent);
        Assert.Equal(0, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
        _followUp.Verify(
            f => f.CloseTodosAsync(
                It.Is<IReadOnlyCollection<ActivityType>>(t => t.Single() == ActivityType.ReconnectChannelManager),
                "Property",
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == propertyId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // A later incident is a new one: alerted from the first stage again.
        _connection = ConnectionHealthState.Inactive;
        _clock.Advance(TimeSpan.FromHours(1));
        var again = await Service().RunAsync();

        Assert.Equal(1, again.Sent);
        Assert.Equal(ComplianceAlertStage.ConnectionDetected, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
        _notifications.Verify(
            n => n.SendComplianceAlertAsync(
                It.Is<ComplianceAlert>(a => a.Kind == ComplianceAlertKind.ConnectionInactive),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}

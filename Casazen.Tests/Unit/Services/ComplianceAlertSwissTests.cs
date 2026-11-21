using System.Globalization;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E31/F02/US01/T01 (NF-131): the rules of the staged alerts about the notification of the guests of a property in a canton other than Ticino to the cantonal authority. The same three stages as
/// the 24 hours of Ticino (<see cref="ComplianceAlertSchedule.DueTicinoStep"/>, whose clock, daylight saving and outage rules <c>ComplianceAlertTicinoScheduleTests</c> prove), measured against the
/// deadline of the regime: the type, the kinds and the key this class proves, and the sequence on the deadline of a regime.
/// </summary>
public class ComplianceAlertSwissScheduleTests
{
    private static readonly Guid PropertyId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly TimeZoneInfo Zurich = RomeCalendar.ZoneOf("Europe/Zurich");
    private static readonly DateOnly Day = new(2026, 11, 19);
    private readonly ComplianceOptions _options = new();

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private ComplianceAlertStep? Step(string dueUtc, string nowUtc, TimeOnly? expected = null) =>
        ComplianceAlertSchedule.DueSwissStep(PropertyId, Day, Utc(dueUtc), Zurich, expected, _options, Utc(nowUtc));

    [Fact]
    public void DueSwissStep_FollowsTheSequenceAgainstTheDeadlineOfTheRegime_ArrivalDay_SixHoursBefore_Overdue_TwoReminders_ThenNothing()
    {
        // Schaffhausen: the notification of Thursday 19 November is due on Friday at 02:00 (01:00Z). No expected check-in: the first stage is at 18:00 on the clock of the property (17:00Z).
        const string due = "2026-11-20T01:00:00Z";

        (string Now, int? Stage, ComplianceAlertKind? Kind, int Reminder)[] timeline =
        [
            ("2026-11-19T08:00:00Z", null, null, 0),
            ("2026-11-19T16:59:59Z", null, null, 0),
            ("2026-11-19T17:00:00Z", ComplianceAlertStage.TicinoFileToPrepare, ComplianceAlertKind.SwissNotificationToPrepare, 0),
            ("2026-11-19T18:59:59Z", ComplianceAlertStage.TicinoFileToPrepare, ComplianceAlertKind.SwissNotificationToPrepare, 0),
            ("2026-11-19T19:00:00Z", ComplianceAlertStage.TicinoDeadlineApproaching, ComplianceAlertKind.SwissNotificationApproaching, 0),
            ("2026-11-20T00:59:59Z", ComplianceAlertStage.TicinoDeadlineApproaching, ComplianceAlertKind.SwissNotificationApproaching, 0),
            ("2026-11-20T01:00:00Z", ComplianceAlertStage.TicinoOverdue, ComplianceAlertKind.SwissNotificationOverdue, 0),
            ("2026-11-21T00:59:59Z", ComplianceAlertStage.TicinoOverdue, ComplianceAlertKind.SwissNotificationOverdue, 0),
            ("2026-11-21T01:00:00Z", ComplianceAlertStage.TicinoOverdueReminder(1), ComplianceAlertKind.SwissNotificationOverdue, 1),
            ("2026-11-22T01:00:00Z", ComplianceAlertStage.TicinoOverdueReminder(2), ComplianceAlertKind.SwissNotificationOverdue, 2),
            ("2026-11-22T23:59:59Z", ComplianceAlertStage.TicinoOverdueReminder(2), ComplianceAlertKind.SwissNotificationOverdue, 2),

            // The last stage is dropped a day after its time: never a late message of a day that has been overdue for days.
            ("2026-11-23T01:00:00Z", null, null, 0),
            ("2026-12-01T09:00:00Z", null, null, 0),
        ];

        foreach (var (now, stage, kind, reminder) in timeline)
        {
            var step = Step(due, now);

            Assert.True(stage == step?.Stage, $"at {now}: expected stage {stage}, got {step?.Stage}");
            if (step is null)
                continue;

            Assert.Equal(kind, step.Alert.Kind);
            Assert.Equal(reminder, step.Alert.ReminderNumber);
            Assert.Equal(2, step.Alert.MaxReminders);
            Assert.Equal(ComplianceAlertType.SwissNotification, step.Type);
            Assert.Equal(ComplianceAlertType.SwissNotification, step.Alert.Type);
            Assert.Equal("2026-11-19", step.ReferenceKey);
            Assert.Equal(Day, step.Alert.ArrivalDay);
            Assert.Equal(Utc(due), step.Alert.DueAtUtc);
            Assert.Equal(PropertyId, step.Alert.PropertyId);
            Assert.Null(step.Alert.Period);
        }
    }

    [Fact]
    public void DueSwissStep_IsTheTicinoSequence_WithTheSwissTypeAndKindsOnly()
    {
        // The same stage at the same moment for the same deadline: nothing of the clock, the daylight saving or the outage rules is written twice.
        foreach (var now in new[] { "2026-11-19T17:00:00Z", "2026-11-19T19:30:00Z", "2026-11-20T01:00:00Z", "2026-11-21T01:00:00Z", "2026-11-22T01:00:00Z", "2026-11-23T01:00:00Z" })
        {
            var ticino = ComplianceAlertSchedule.DueTicinoStep(PropertyId, Day, Utc("2026-11-20T01:00:00Z"), Zurich, null, _options, Utc(now));
            var swiss = Step("2026-11-20T01:00:00Z", now);

            Assert.Equal(ticino?.Stage, swiss?.Stage);
            Assert.Equal(ticino?.ReferenceKey, swiss?.ReferenceKey);
            Assert.Equal(ticino?.Alert.ReminderNumber, swiss?.Alert.ReminderNumber);
        }

        Assert.Equal(ComplianceAlertSchedule.TicinoReferenceKey(Day), ComplianceAlertSchedule.SwissReferenceKey(Day));
        Assert.InRange(ComplianceAlertSchedule.SwissReferenceKey(Day).Length, 1, ComplianceAlertState.ReferenceKeyMaxLength);
    }

    [Fact]
    public void DueSwissStep_WhereTheDeadlineIsTheArrival_TheFirstAlertOfARecordedArrivalIsTheOverdueOne()
    {
        // Zug: the notification is due at the arrival. The arrival is recorded at 15:00 (14:00Z): at the first run after it the highest stage due is the overdue one, never stages 1 and 2 as well.
        var step = Step("2026-11-19T14:00:00Z", "2026-11-19T14:05:00Z", expected: new TimeOnly(13, 0));

        Assert.Equal(ComplianceAlertStage.TicinoOverdue, step!.Stage);
        Assert.Equal(ComplianceAlertKind.SwissNotificationOverdue, step.Alert.Kind);

        // Before it, with the day not started on the clock of the property, nothing is due.
        Assert.Null(Step("2026-11-19T14:00:00Z", "2026-11-18T23:00:00Z"));
    }

    [Fact]
    public void TheKindsOfTheSwissAlert_AreAppendedToTheEnum_WithoutMovingTheOthers()
    {
        Assert.Equal(
            [
                "PeriodDeadlineApproaching", "PeriodOverdue", "ConnectionInactive", "TicinoFileToPrepare", "TicinoDeadlineApproaching", "TicinoOverdue",
                "SwissNotificationToPrepare", "SwissNotificationApproaching", "SwissNotificationOverdue",
            ],
            Enum.GetNames<ComplianceAlertKind>());
    }
}

/// <summary>
/// E31/F02/US01/T01 (NF-131): the alerts about the notification of the guests of a property in a canton other than Ticino to the cantonal authority, on the real service (EF InMemory): the days
/// come from <see cref="SwissNotificationService"/> (the one rule of a closed day), the delivery is the real <see cref="NotificationService"/> over recording queues, the audience a mock that answers
/// by org. 19 November 2026 is a Thursday, winter time (CET, UTC+1). Schaffhausen is the regime the tests use (verified, so its duty is <c>Required</c> by default; due the night after the arrival at 02:00).
/// </summary>
public class ComplianceAlertSwissTests : IDisposable
{
    private const string GuestFirstName = "Zxqvgfhj";
    private const string GuestLastName = "Plmnbvcx";
    private const string GuestEmail = "zxqvgfhj.plmnbvcx@example.com";
    private const string DocumentNumber = "QWERTY123456";
    private const string BookingCode = "7Q3KZ8M2XB";
    private const string Plate = "ZH4455667";
    private const string StreetOfTheGuest = "Via Segretissima 99";

    private static readonly DateOnly Day = SwissNotificationTestWorld.Day;

    private readonly SwissNotificationTestWorld _world = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _pushes = new();
    private readonly Mock<IComplianceAlertFollowUp> _followUp = new();
    private readonly Mock<IComplianceAlertRecipientResolver> _recipients = new();
    private readonly CapturingLogger<ComplianceAlertService> _log = new();
    private readonly ComplianceOptions _options = new();
    private readonly Dictionary<Guid, string> _addressOfOrg = [];

    public ComplianceAlertSwissTests()
    {
        // The alerts of the other cantons share the flag of the Ticino alerts; the traffic light stays off in these tests.
        _world.Flags.Setup(f => f.IsEnabled(FeatureFlags.TicinoNotificationAlerts)).Returns(true);
        _recipients
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid orgId, Guid _, CancellationToken _) => new ComplianceAlertRecipients([_addressOfOrg[orgId]], []));
    }

    public void Dispose() => _world.Dispose();

    private ComplianceAlertService Service(ISwissNotificationService? swiss = null, bool withSwiss = true) =>
        new(
            _world.Db,
            Mock.Of<IComplianceStatusService>(),
            Mock.Of<IComplianceConnectionHealthService>(),
            Mock.Of<IPropertySenderStore>(),
            _recipients.Object,
            Mock.Of<ITouristTaxDeadlineSource>(),
            new NotificationService(_world.Db, _emails, _pushes, EmailTestHelpers.Links(), NullLogger<NotificationService>.Instance),
            _followUp.Object,
            _world.Flags.Object,
            Options.Create(_options),
            _log,
            _world.Clock,
            new TicinoNotificationService(_world.Db, _world.Clock),
            swiss ?? (withSwiss ? _world.Service() : null));

    private (OrgEntity Org, Property Property) Tenant(string key, string canton = "SH", GuestNotificationOverride? choice = null, string timezone = "Europe/Zurich")
    {
        var org = _world.AddOrg($"{key}@example.com");
        _addressOfOrg[org.Id] = $"{key}@example.com";
        return (org, _world.AddProperty(canton, org, choice, timezone, name: $"Casa {key}"));
    }

    private async Task<int> RunAtAsync(string utc)
    {
        _world.Clock.SetUtcNow(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        return (await Service().RunAsync()).Sent;
    }

    private static string Subject(RecordingEmailQueue queue, int index) => queue.Snapshot()[index].Content.Subject;

    // ─── Each stage once ───

    [Fact]
    public async Task SwissAlerts_EachStageOnce_Test()
    {
        var (org, property) = Tenant("a");
        _world.AddStay(property);

        // The hourly job from the morning of the arrival day to the day after the last stage, and every hour twice (a retry, a manual trigger, a second server).
        var sentByHour = new List<(DateTimeOffset At, int Sent)>();
        for (var at = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero); at <= new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero); at = at.AddHours(1))
        {
            var sent = await RunAtAsync(at.ToString("O", CultureInfo.InvariantCulture)) + await RunAtAsync(at.ToString("O", CultureInfo.InvariantCulture));
            if (sent > 0)
                sentByHour.Add((at, sent));
        }

        // Five messages in all, one for each stage, each at the first run after its time (the job is hourly): 18:00 on the arrival day, six hours before 02:00 of the next day, the deadline, two reminders.
        Assert.Equal(
            [
                (new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 19, 19, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 21, 1, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 22, 1, 0, 0, TimeSpan.Zero), 1),
            ],
            sentByHour);
        var state = await _world.Db.ComplianceAlertStates.AsNoTracking().SingleAsync();
        Assert.Equal(
            (property.Id, org.Id, ComplianceAlertType.SwissNotification, "2026-11-19", ComplianceAlertStage.TicinoOverdueReminder(2), 5),
            (state.PropertyId, state.OrgId, state.Type, state.ReferenceKey, state.Stage, state.AlertCount));

        // One e-mail to the address of the org and one push to the hosts of the property for each stage, with a key of its own (a device gets one push per key).
        Assert.Equal(5, _emails.Snapshot().Count);
        Assert.All(_emails.Snapshot(), e => Assert.Equal(("a@example.com", EmailTemplates.Names.SwissNotificationAlert), (e.To, e.Template)));
        Assert.StartsWith("Notifica degli ospiti all'autorità da preparare - Casa a", Subject(_emails, 0), StringComparison.Ordinal);
        Assert.StartsWith("Notifica degli ospiti all'autorità in scadenza - Casa a", Subject(_emails, 1), StringComparison.Ordinal);
        Assert.StartsWith("Notifica degli ospiti all'autorità scaduta - Casa a", Subject(_emails, 2), StringComparison.Ordinal);
        Assert.Contains("Promemoria 1 di 2.", _emails.Snapshot()[3].Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Promemoria 2 di 2.", _emails.Snapshot()[4].Content.HtmlBody, StringComparison.Ordinal);
        var pushes = _pushes.Queued;
        Assert.Equal(5, pushes.Count);
        Assert.Equal(5, pushes.Select(p => p.DeliveryKey).Distinct().Count());
        Assert.All(pushes, p => Assert.Contains($"compliance-alert:{property.Id:N}:SwissNotification:2026-11-19:", p.DeliveryKey, StringComparison.Ordinal));
        Assert.Equal(
            [PushTypes.SwissNotificationDue, PushTypes.SwissNotificationDue, PushTypes.SwissNotificationOverdue, PushTypes.SwissNotificationOverdue, PushTypes.SwissNotificationOverdue],
            pushes.Select(p => p.Payload.Type).ToArray());
        Assert.All(pushes, p =>
        {
            Assert.Equal(PushAudience.PropertyHosts(property.Id), p.Audience);
            Assert.Equal(PushRoutes.Properties, p.Payload.Route);
            Assert.Null(p.Payload.BookingId);
            Assert.Null(p.Payload.ServiceRequestId);
        });

        // A Swiss day leaves no «To do» and no popup of the router (the cockpit lists it in its own section).
        _followUp.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SwissAlerts_AskOrNotRequiredOrARegisterOnlyCanton_AreNeverAlerted_Test()
    {
        // PROOF TEST (data): no false legal alarm. The canton whose sources are partial (Zurich, Nidwalden) is Ask, the host that said NotRequired is not alerted, and a canton that only asks for a
        // register (Bern) is never alerted, whatever the host says: nothing is read, written, e-mailed or pushed, and nobody is even asked who to tell.
        var ask = Tenant("zh", "ZH");
        var weekly = Tenant("nw", "NW");
        var notRequired = Tenant("sh", "SH", GuestNotificationOverride.NotRequired);
        var register = Tenant("be", "BE", GuestNotificationOverride.Required);
        foreach (var (_, property) in new[] { ask, weekly, notRequired, register })
            _world.AddStay(property, arrivedAtUtc: "2026-11-19T14:00:00Z");

        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T01:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-23T08:00:00Z"));

        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();

        // The host that answers Required turns on the alerts of its canton: a property of Nidwalden that said so is alerted (its deadline is the Sunday of the week), the other one still is not.
        var chosen = Tenant("nw2", "NW", GuestNotificationOverride.Required);
        _world.AddStay(chosen.Property);
        Assert.Equal(1, await RunAtAsync("2026-11-20T10:00:00Z"));
        Assert.Equal(chosen.Property.Id, Assert.Single(await _world.Db.ComplianceAlertStates.AsNoTracking().ToListAsync()).PropertyId);
    }

    [Fact]
    public async Task SwissAlerts_TicinoIsNeverAlertedHere_AndTheTicinoAlertsAreUnchanged()
    {
        var swiss = Tenant("sh", "SH");
        var ticino = Tenant("ti", "TI");
        _world.AddStay(swiss.Property);
        _world.AddStay(ticino.Property);

        Assert.Equal(2, await RunAtAsync("2026-11-19T17:00:00Z"));

        // One state row of each type, each about its own property; the Ticino stage is the one of E30.
        var states = await _world.Db.ComplianceAlertStates.AsNoTracking().ToListAsync();
        Assert.Equal(ComplianceAlertType.SwissNotification, Assert.Single(states, s => s.PropertyId == swiss.Property.Id).Type);
        Assert.Equal(ComplianceAlertType.TicinoNotification, Assert.Single(states, s => s.PropertyId == ticino.Property.Id).Type);
        Assert.Equal(PushTypes.SwissNotificationDue, Assert.Single(_pushes.Queued, p => p.Audience.Id == swiss.Property.Id).Payload.Type);
        Assert.Equal(PushTypes.TicinoNotificationDue, Assert.Single(_pushes.Queued, p => p.Audience.Id == ticino.Property.Id).Payload.Type);
        Assert.Equal(
            [EmailTemplates.Names.SwissNotificationAlert, EmailTemplates.Names.TicinoNotificationAlert],
            _emails.Snapshot().Select(e => e.Template).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("FR")]
    [InlineData("ZH")]
    [InlineData("BS")]
    [InlineData("NE")]
    public async Task SwissAlerts_ARegimeWithNoDeadline_IsNeverAlerted_ADeadlineIsNeverGuessed(string canton)
    {
        // Fribourg and Zurich have no deadline at all; Basel-Stadt and Neuchâtel say «every day» with no cut-off time: none of them has a moment an alert could be measured against.
        var tenant = Tenant(canton.ToLowerInvariant(), canton, GuestNotificationOverride.Required);
        _world.AddStay(tenant.Property);

        // The day is listed (the host said it is required) but has no deadline: no stage is due, whatever the time (also the midnight that would end the arrival day, and the days after it).
        var listed = Assert.Single(await _world.Service().ListPendingDaysAsync(null, null, Day));
        Assert.Equal((Day, (DateTime?)null), (listed.Day, listed.DueAtUtc));
        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-19T23:30:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T08:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-21T08:00:00Z"));
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SwissAlerts_ADeclaredDay_StopsTheAlerts_AndAnUndoneOneStartsThemAgain_Test()
    {
        var (_, property) = Tenant("a");
        _world.AddStay(property);
        Assert.Equal(1, await RunAtAsync("2026-11-19T17:00:00Z"));

        // The host uploaded nothing on the portal of the canton: it declared the day notified. The service of the days says it is closed, so no stage is due any more.
        await _world.Service().DeclareDayAsync(property.Id, Day, "auth0|anna");
        Assert.Equal(0, await RunAtAsync("2026-11-19T19:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T01:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-21T01:00:00Z"));
        Assert.Single(_emails.Snapshot());

        // A wrong declaration undone: the day is to notify again and the highest stage due goes out once (never the backlog).
        await _world.Service().UndeclareDayAsync(property.Id, Day, "auth0|anna");
        Assert.Equal(1, await RunAtAsync("2026-11-21T02:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-21T03:00:00Z"));
        Assert.Equal(2, _emails.Snapshot().Count);
        Assert.StartsWith("Notifica degli ospiti all'autorità scaduta", Subject(_emails, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwissAlerts_StaysThatOweNothing_AreNeverAlerted()
    {
        var (_, property) = Tenant("a");
        _world.AddStay(property, status: BookingStatus.Cancelled);
        _world.AddStay(property, status: BookingStatus.Pending);
        _world.AddStay(property, noShowAt: new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T02:00:00Z"));
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
    }

    [Fact]
    public async Task SwissAlerts_TheEarliestExpectedCheckInOfTheDay_MovesTheFirstStage_OnTheClockOfTheProperty()
    {
        var (_, property) = Tenant("a", timezone: "UTC");
        _world.AddStay(property, expectedArrival: "11:00");
        _world.AddStay(property, expectedArrival: "16:00");

        // 11:00 + 2 hours = 13:00 on the clock of the property, which is UTC: 13:00Z, not the 12:00Z of Zurich. The deadline is 02:00 of the next day on that clock: the day after at 02:00Z.
        Assert.Equal(0, await RunAtAsync("2026-11-19T12:59:00Z"));
        Assert.Equal(1, await RunAtAsync("2026-11-19T13:00:00Z"));
        Assert.Equal(ComplianceAlertStage.TicinoFileToPrepare, (await _world.Db.ComplianceAlertStates.SingleAsync()).Stage);
    }

    // ─── Permission and privacy ───

    [Fact]
    public async Task SwissAlerts_OtherOrganizationNeverAlerted_Test()
    {
        // PROOF TEST (permission/privacy): each org hears about its own properties only. Three orgs with a property of Schaffhausen, two with a stay: each e-mail goes to the address of its own org,
        // the audience is asked only for the pair (org, its own property), the push names its own property, and the third org (no stay) hears nothing about the days of the two others.
        var a = Tenant("a");
        var b = Tenant("b");
        var c = Tenant("c");
        _world.AddStay(a.Property);
        _world.AddStay(b.Property);

        Assert.Equal(2, await RunAtAsync("2026-11-19T17:00:00Z"));

        Assert.Equal(["a@example.com", "b@example.com"], _emails.Snapshot().Select(e => e.To!).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { a.Property.Id, b.Property.Id }.Order(),
            _pushes.Queued.Select(p => p.Audience.Id).Order());
        _recipients.Verify(r => r.ForPropertyAsync(a.Org.Id, a.Property.Id, It.IsAny<CancellationToken>()), Times.Once);
        _recipients.Verify(r => r.ForPropertyAsync(b.Org.Id, b.Property.Id, It.IsAny<CancellationToken>()), Times.Once);
        _recipients.Verify(r => r.ForPropertyAsync(c.Org.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _recipients.VerifyNoOtherCalls();
        Assert.Equal(2, await _world.Db.ComplianceAlertStates.CountAsync());
    }

    [Fact]
    public async Task SwissAlerts_PayloadHasNoPersonalData_Test()
    {
        // PROOF TEST (privacy): what leaves the system is the property, the day, the deadline and the TYPES of field that miss: never the name of a guest, a document, an address, a plate, a booking code or
        // an e-mail. The stay holds all of them, plus a guest whose date of birth and document type are missing, so the types of field are told.
        var (_, property) = Tenant("a");
        var stay = _world.AddStay(property, bookingCode: BookingCode, plate: Plate, guests: 2);
        _world.AddGuest(stay, position: 0, firstName: GuestFirstName, lastName: GuestLastName, documentNumber: DocumentNumber, street: StreetOfTheGuest);
        _world.AddGuest(stay, position: 1, firstName: "Qazwsxed", lastName: "Edcrfvtg", noBirth: true, documentTypeCode: null, documentNumber: "");

        await RunAtAsync("2026-11-19T17:00:00Z");
        await RunAtAsync("2026-11-20T01:00:00Z");

        var state = await _world.Db.ComplianceAlertStates.AsNoTracking().SingleAsync();
        var everything = string.Join(
            '\n',
            _emails.Snapshot().SelectMany(e => new[] { e.To, e.Template, e.Content.Subject, e.Content.HtmlBody })
                .Concat(_pushes.Queued.SelectMany(p => new[] { p.DeliveryKey, p.Payload.Title, p.Payload.Body, p.Payload.Type, p.Payload.Route }))
                .Concat([state.ReferenceKey, state.Type.ToString(), _log.AllOutput]));
        foreach (var personal in new[]
                 {
                     GuestFirstName, GuestLastName, GuestEmail, DocumentNumber, BookingCode, BookingCodes.Format(BookingCode), Plate, StreetOfTheGuest, "Qazwsxed", "Edcrfvtg", "booker@example.com", "Booker",
                 })
        {
            Assert.DoesNotContain(personal, everything, StringComparison.OrdinalIgnoreCase);
        }

        // What it says: the property, the types of field that miss.
        Assert.Contains("Casa a", everything, StringComparison.Ordinal);
        Assert.Contains("data di nascita", everything, StringComparison.Ordinal);
        Assert.Contains("tipo di documento", everything, StringComparison.Ordinal);
    }

    // ─── The flags ───

    [Fact]
    public async Task SwissAlerts_FlagOffSendsNothing_Test()
    {
        var (_, property) = Tenant("a");
        _world.AddStay(property);
        _world.Flags.Setup(f => f.IsEnabled(FeatureFlags.TicinoNotificationAlerts)).Returns(false);

        var result = await Service().RunAsync();
        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T02:00:00Z"));

        // Both flags off: the run does nothing and writes nothing; no e-mail, no push, nobody is asked who to tell.
        Assert.True(result.Skipped);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SwissAlerts_TheRegimesFlagOff_SendsNoSwissAlert_ButTheTicinoAlertsStillRun()
    {
        var swiss = Tenant("sh", "SH");
        var ticino = Tenant("ti", "TI");
        _world.AddStay(swiss.Property);
        _world.AddStay(ticino.Property);
        _world.RegimesOn = false;

        Assert.Equal(1, await RunAtAsync("2026-11-19T17:00:00Z"));

        var state = await _world.Db.ComplianceAlertStates.AsNoTracking().SingleAsync();
        Assert.Equal((ticino.Property.Id, ComplianceAlertType.TicinoNotification), (state.PropertyId, state.Type));
        Assert.Equal(PushTypes.TicinoNotificationDue, Assert.Single(_pushes.Queued).Payload.Type);
    }

    [Fact]
    public async Task SwissAlerts_FlagOnButNoServiceOfTheDays_SendNothing_AndSayWhy()
    {
        var (_, property) = Tenant("a");
        _world.AddStay(property);
        _world.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

        var result = await Service(withSwiss: false).RunAsync();

        Assert.False(result.Skipped);
        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
        Assert.Contains(_log.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("Swiss notification alerts skipped", StringComparison.Ordinal) && e.Message.Contains("not registered", StringComparison.Ordinal));
    }

    // ─── The day is read again before a stage is claimed ───

    [Fact]
    public async Task SwissAlerts_ADayThatClosesBetweenTheListAndTheSend_SendsNothing_AndWritesNoState()
    {
        var (_, property) = Tenant("a");
        var days = new Mock<ISwissNotificationService>();
        IReadOnlyList<SwissPendingDay> listed = [new SwissPendingDay(property.OrgId, property.Id, property.Name, "CH-SH", Day, 1, new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc))];
        days.Setup(d => d.ListPendingDaysAsync(null, null, Day, It.IsAny<CancellationToken>())).ReturnsAsync(listed);

        // The detail of the day no longer lists it: a co-host declared it between the two reads. Or the property changed canton: the day is listed under another regime.
        foreach (var regimeOfTheDetail in new string?[] { null, "CH-ZG" })
        {
            IReadOnlyList<SwissPendingDay> pendingDays = regimeOfTheDetail is null
                ? []
                : [new SwissPendingDay(property.OrgId, property.Id, property.Name, regimeOfTheDetail, Day, 1, null)];
            days.Setup(d => d.GetDayAsync(property.Id, Day, It.IsAny<CancellationToken>())).ReturnsAsync(DetailOfTheDay(property, pendingDays));
            _world.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

            var result = await Service(days.Object).RunAsync();

            Assert.Equal(0, result.Sent);
        }

        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("swiss_notification_not_applicable")]
    [InlineData("property_not_found")]
    public async Task SwissAlerts_APropertyThatIsNoLongerCoveredWhenTheDayIsRead_SendsNothingAndWritesNoState(string code)
    {
        var (_, property) = Tenant("a");
        var days = new Mock<ISwissNotificationService>();
        IReadOnlyList<SwissPendingDay> listed = [new SwissPendingDay(property.OrgId, property.Id, property.Name, "CH-SH", Day, 1, new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc))];
        days.Setup(d => d.ListPendingDaysAsync(null, null, Day, It.IsAny<CancellationToken>())).ReturnsAsync(listed);
        Exception gone = code == "property_not_found"
            ? new NotFoundException($"Property {property.Id} not found") { Code = code, MessageKey = "PropertyNotFound" }
            : new DomainRuleException(code, "SwissNotificationNotApplicable");
        days.Setup(d => d.GetDayAsync(property.Id, Day, It.IsAny<CancellationToken>())).ThrowsAsync(gone);
        _world.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

        var result = await Service(days.Object).RunAsync();

        days.Verify(d => d.GetDayAsync(property.Id, Day, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(await _world.Db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();

        // An answer to expect, told at information level with ids only: neither a warning nor an error of the run.
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    private static SwissNotificationDay DetailOfTheDay(Property property, IReadOnlyList<SwissPendingDay> pendingDays) => new(
        property.Id,
        Day,
        GuestRegistrationRegimeCatalog.ForCanton("SH"),
        GuestRegistrationEffectiveDuty.Required,
        1,
        [],
        [],
        new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc),
        null,
        null,
        pendingDays);

    // ─── The bell, the registration, the enumerations ───

    [Theory]
    [InlineData(PushTypes.SwissNotificationDue)]
    [InlineData(PushTypes.SwissNotificationOverdue)]
    [InlineData(PushTypes.SwissNotificationDeclared)]
    public void TheBellEntryOfASwissNotification_IsAboutTheProperty_NotABooking(string type)
    {
        var propertyId = Guid.NewGuid();
        var payload = new PushNotificationPayload("title", "body", type, null, PushRoutes.Properties);

        var queued = QueuedInAppNotification.From(PushAudience.PropertyHosts(propertyId), payload, new DateTime(2026, 11, 19, 17, 0, 0, DateTimeKind.Utc));

        Assert.True(PushTypes.IsSwissNotification(type));
        Assert.False(PushTypes.IsTicinoNotification(type));
        Assert.Equal(propertyId, queued.EntityId);
        Assert.Equal(type, queued.Type);

        // The property is named for the hosts of a property only: another audience (an id that is a booking, not a property) is never turned into it.
        Assert.Null(QueuedInAppNotification.From(PushAudience.BookingHosts(propertyId), payload, new DateTime(2026, 11, 19, 17, 0, 0, DateTimeKind.Utc)).EntityId);
    }

    [Fact]
    public void ThePushTypesOfTheSwissNotification_AreDistinctFromTheTicinoOnes_AndFitTheColumn()
    {
        Assert.Equal("swiss-notification-due", PushTypes.SwissNotificationDue);
        Assert.Equal("swiss-notification-overdue", PushTypes.SwissNotificationOverdue);
        Assert.Equal("swiss-notification-declared", PushTypes.SwissNotificationDeclared);
        Assert.False(PushTypes.IsSwissNotification(null));
        Assert.False(PushTypes.IsSwissNotification(PushTypes.TicinoNotificationDue));
        Assert.All(
            [PushTypes.SwissNotificationDue, PushTypes.SwissNotificationOverdue, PushTypes.SwissNotificationDeclared],
            type => Assert.InRange(type.Length, 1, InAppNotification.TypeMaxLength));
        Assert.Equal(PushTypes.SwissNotificationDue, PushTypes.ForComplianceAlert(ComplianceAlertType.SwissNotification));
    }

    [Theory]
    [InlineData(ComplianceAlertKind.SwissNotificationToPrepare, "swiss-notification-due")]
    [InlineData(ComplianceAlertKind.SwissNotificationApproaching, "swiss-notification-due")]
    [InlineData(ComplianceAlertKind.SwissNotificationOverdue, "swiss-notification-overdue")]
    public void ThePushTypeOfASwissAlert_IsDueBeforeTheDeadline_AndOverdueAfterIt(ComplianceAlertKind kind, string expected)
    {
        var alert = new ComplianceAlert(ComplianceAlertType.SwissNotification, Guid.NewGuid(), kind, ArrivalDay: Day);

        Assert.Equal(expected, PushTypes.ForComplianceAlert(alert));
    }

    [Fact]
    public async Task TheService_ResolvesWithValidateScopes_AndGetsTheServiceOfTheSwissDays()
    {
        var (_, property) = Tenant("a");
        _world.AddStay(property);
        _world.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));
        var notifications = new Mock<INotificationService>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_world.Clock);
        services.AddScoped(_ => _world.Db);
        services.AddSingleton(_world.Flags.Object);
        services.AddSingleton(_recipients.Object);
        services.AddSingleton(Mock.Of<IComplianceStatusService>());
        services.AddSingleton(Mock.Of<IComplianceConnectionHealthService>());
        services.AddSingleton(Mock.Of<IPropertySenderStore>());
        services.AddSingleton(Mock.Of<ITouristTaxDeadlineSource>());
        services.AddSingleton(_followUp.Object);
        services.AddSingleton(notifications.Object);
        services.AddOptions<ComplianceOptions>();
        services.AddScoped<ITicinoNotificationService, TicinoNotificationService>();
        services.AddScoped<IPropertySwissPlaceResolver, PropertySwissPlaceResolver>();
        services.AddScoped<IGuestRegistrationRegimeResolver, GuestRegistrationRegimeResolver>();
        services.AddScoped<ISwissNotificationService, SwissNotificationService>();
        services.AddScoped<IComplianceAlertService, ComplianceAlertService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IComplianceAlertService>().RunAsync();

        // The Swiss days were read: the optional service was given by the container, not left to its default.
        Assert.Equal(1, result.Sent);
        notifications.Verify(
            n => n.SendComplianceAlertAsync(
                It.Is<ComplianceAlert>(a => a.Type == ComplianceAlertType.SwissNotification
                    && a.Kind == ComplianceAlertKind.SwissNotificationToPrepare
                    && a.PropertyId == property.Id
                    && a.ArrivalDay == Day),
                It.Is<IReadOnlyCollection<string>>(emails => emails.Single() == "a@example.com"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void ComplianceAlertType_StoredValuesAreNeverRenumbered_AndTheSwissOneIsTheFifth()
    {
        Assert.Equal(
            [(1, "IstatPeriod"), (2, "TouristTaxPeriod"), (3, "ConnectionInactive"), (4, "TicinoNotification"), (5, "SwissNotification")],
            Enum.GetValues<ComplianceAlertType>().Select(t => ((int)t, t.ToString())).ToArray());
    }

    // ─── The texts ───

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void EveryKindOfSwissAlert_RendersTheEmailAndThePush_InBothLanguages_WithNoPlaceholderLeftAndNoGuestData(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var link = EmailTestHelpers.Links().HostCompliance();
        var due = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);
        var kinds = Enum.GetValues<ComplianceAlertKind>().Where(k => k.ToString().StartsWith("SwissNotification", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, kinds.Count);
        foreach (var kind in kinds)
        {
            var alert = new ComplianceAlert(
                ComplianceAlertType.SwissNotification,
                Guid.NewGuid(),
                kind,
                ReminderNumber: kind == ComplianceAlertKind.SwissNotificationOverdue ? 1 : 0,
                MaxReminders: 2,
                ArrivalDay: Day,
                DueAtUtc: due,
                MissingFields: ["birthDate", "documentType", "someFutureCode"]);

            var email = EmailTemplates.SwissNotificationAlert(culture, alert, "Villa Rosa", link);
            var push = EmailTemplates.SwissNotificationPush(culture, alert, "Villa Rosa");

            Assert.Contains($"<html lang=\"{culture.TwoLetterISOLanguageName}\">", email.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("Villa Rosa", email.Subject, StringComparison.Ordinal);
            Assert.Contains("Villa Rosa", push.Body, StringComparison.Ordinal);
            Assert.Contains($"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/short-rent/compliance\"", email.HtmlBody, StringComparison.Ordinal);
            foreach (var text in new[] { email.Subject, email.HtmlBody, push.Title, push.Body })
            {
                Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
                Assert.DoesNotContain("{1}", text, StringComparison.Ordinal);
                Assert.DoesNotContain("{2}", text, StringComparison.Ordinal);
                Assert.DoesNotContain("someFutureCode", text, StringComparison.Ordinal);
            }

            // A deadline at 01:00Z is 02:00 in Zurich in winter.
            Assert.Contains(culture.TwoLetterISOLanguageName == "en" ? "20 November 2026, 02:00" : "20/11/2026 02:00", email.HtmlBody, StringComparison.Ordinal);
        }

        var declared = EmailTemplates.SwissNotificationDeclaredPush(culture, "Villa Rosa", Day, declared: true);
        var undone = EmailTemplates.SwissNotificationDeclaredPush(culture, "Villa Rosa", Day, declared: false);
        Assert.NotEqual(declared.Body, undone.Body);
        Assert.All([declared.Title, declared.Body, undone.Title, undone.Body], text => Assert.DoesNotContain("{", text, StringComparison.Ordinal));
        Assert.Contains("Villa Rosa", declared.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSwissNotificationTexts_NameNoSupplier_AndExistInBothLanguages()
    {
        // The authorities may be named, the suppliers of software and of the cantonal systems never: no text of the notification reaches a user with a supplier in it.
        string[] suppliers = ["hoko", "unisys", "u-stay", "ustay", "feratel", "chekin", "stellar", "chargeautomation", "lodgify", "smoobu", "arhon", "tourx", "softec", "octorate", "airbnb", "concordato"];
        var root = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Casazen.sln")))
            root = root.Parent;

        var texts = new List<(string Where, string Text)>();
        foreach (var file in new[] { "EmailTexts.resx", "EmailTexts.en.resx" })
        {
            var path = Path.Combine(root!.FullName, "Casazen.Infrastructure", "Email", "Templates", file);
            texts.AddRange(XDocument.Load(path).Root!.Elements("data")
                .Where(d => ((string)d.Attribute("name")!).Contains("SwissNotification", StringComparison.Ordinal))
                .Select(d => ($"{file}:{(string)d.Attribute("name")!}", (string?)d.Element("value") ?? string.Empty)));
        }

        Assert.Equal(2 * 17, texts.Count);
        foreach (var (where, text) in texts)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), where);
            foreach (var supplier in suppliers)
                Assert.DoesNotContain(supplier, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System.Globalization;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Push;
using Casazen.Web.BackgroundJobs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ComplianceAlertTicinoScheduleTests
{
    private static readonly Guid PropertyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly TimeZoneInfo Zurich = RomeCalendar.ZoneOf("Europe/Zurich");
    private readonly ComplianceOptions _options = new();

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private ComplianceAlertStep? Step(DateOnly day, string dueUtc, string nowUtc, TimeOnly? expected = null, ComplianceOptions? options = null) =>
        ComplianceAlertSchedule.DueTicinoStep(PropertyId, day, Utc(dueUtc), Zurich, expected, options ?? _options, Utc(nowUtc));

    [Theory]
    [InlineData("2026-03-28", "2026-03-28T17:00:00Z")]
    [InlineData("2026-03-29", "2026-03-29T16:00:00Z")]
    [InlineData("2026-10-24", "2026-10-24T16:00:00Z")]
    [InlineData("2026-10-25", "2026-10-25T17:00:00Z")]
    [InlineData("2026-07-15", "2026-07-15T16:00:00Z")]
    [InlineData("2026-11-19", "2026-11-19T17:00:00Z")]
    public void DueTicinoStep_NoExpectedCheckIn_TheFirstStageIsDueAt1800OnTheClockOfTheProperty_AcrossBothDaylightSavingChanges(string day, string firstStageUtc)
    {
        var arrival = DateOnly.Parse(day, CultureInfo.InvariantCulture);
        var due = Utc(firstStageUtc).AddDays(2);

        Assert.Null(ComplianceAlertSchedule.DueTicinoStep(PropertyId, arrival, due, Zurich, null, _options, Utc(firstStageUtc).AddSeconds(-1)));
        var step = ComplianceAlertSchedule.DueTicinoStep(PropertyId, arrival, due, Zurich, null, _options, Utc(firstStageUtc));

        Assert.Equal(ComplianceAlertStage.TicinoFileToPrepare, step!.Stage);
        Assert.Equal(ComplianceAlertKind.TicinoFileToPrepare, step.Alert.Kind);
    }

    [Theory]
    [InlineData("2026-11-19", "15:30", "2026-11-19T16:30:00Z")]
    [InlineData("2026-07-15", "15:30", "2026-07-15T15:30:00Z")]
    [InlineData("2026-03-29", "03:30", "2026-03-29T03:30:00Z")]
    [InlineData("2026-03-29", "00:30", "2026-03-29T01:30:00Z")]
    [InlineData("2026-10-25", "00:30", "2026-10-25T00:30:00Z")]
    [InlineData("2026-10-25", "05:00", "2026-10-25T06:00:00Z")]
    [InlineData("2026-11-19", "23:30", "2026-11-19T22:00:00Z")]
    public void TicinoArrivalAlertAtUtc_TwoHoursAfterTheFirstExpectedCheckIn_OnTheClockOfTheProperty(string day, string expected, string atUtc)
    {
        var at = ComplianceAlertSchedule.TicinoArrivalAlertAtUtc(
            DateOnly.Parse(day, CultureInfo.InvariantCulture),
            Zurich,
            TimeOnly.ParseExact(expected, "HH:mm", CultureInfo.InvariantCulture),
            _options);

        Assert.Equal(Utc(atUtc), at);
        Assert.Equal(DateTimeKind.Utc, at.Kind);
    }

    [Fact]
    public void TicinoArrivalAlertAtUtc_TheClockOfTheProperty_IsTheOneOfItsOwnTimeZone()
    {
        var onUtc = ComplianceAlertSchedule.TicinoArrivalAlertAtUtc(new DateOnly(2026, 11, 19), TimeZoneInfo.Utc, null, _options);
        var onZurich = ComplianceAlertSchedule.TicinoArrivalAlertAtUtc(new DateOnly(2026, 11, 19), Zurich, null, _options);

        Assert.Equal(Utc("2026-11-19T18:00:00Z"), onUtc);
        Assert.Equal(Utc("2026-11-19T17:00:00Z"), onZurich);
    }

    [Fact]
    public void TicinoArrivalAlertAtUtc_TheThresholdsAreOptions()
    {
        var options = new ComplianceOptions { TicinoArrivalAlertDelayHours = 1, TicinoArrivalAlertDefaultHourLocal = 20 };

        Assert.Equal(
            Utc("2026-11-19T15:30:00Z"),
            ComplianceAlertSchedule.TicinoArrivalAlertAtUtc(new DateOnly(2026, 11, 19), Zurich, new TimeOnly(15, 30), options));
        Assert.Equal(
            Utc("2026-11-19T19:00:00Z"),
            ComplianceAlertSchedule.TicinoArrivalAlertAtUtc(new DateOnly(2026, 11, 19), Zurich, null, options));
    }

    [Fact]
    public void TicinoFirstExpectedArrival_IsTheEarliestValidTime_AndIgnoresWhatSaysNothingAboutWhen()
    {
        Assert.Equal(
            new TimeOnly(9, 15),
            ComplianceAlertSchedule.TicinoFirstExpectedArrival(["16:30", "9:15", "late", "", null, "25:00", "abc", "  "]));
        Assert.Null(ComplianceAlertSchedule.TicinoFirstExpectedArrival(["late", null, "", "noon"]));
        Assert.Null(ComplianceAlertSchedule.TicinoFirstExpectedArrival([]));
    }

    [Fact]
    public void DueTicinoStep_FollowsTheSequence_ArrivalDay_SixHoursBefore_Overdue_TwoReminders_ThenNothing()
    {
        var day = new DateOnly(2026, 11, 19);
        const string due = "2026-11-20T14:00:00Z";
        var expected = new TimeOnly(15, 30);

        (string Now, int? Stage, ComplianceAlertKind? Kind, int Reminder)[] timeline =
        [
            ("2026-11-19T12:00:00Z", null, null, 0),
            ("2026-11-19T16:29:59Z", null, null, 0),
            ("2026-11-19T16:30:00Z", ComplianceAlertStage.TicinoFileToPrepare, ComplianceAlertKind.TicinoFileToPrepare, 0),
            ("2026-11-20T07:59:59Z", ComplianceAlertStage.TicinoFileToPrepare, ComplianceAlertKind.TicinoFileToPrepare, 0),
            ("2026-11-20T08:00:00Z", ComplianceAlertStage.TicinoDeadlineApproaching, ComplianceAlertKind.TicinoDeadlineApproaching, 0),
            ("2026-11-20T13:59:59Z", ComplianceAlertStage.TicinoDeadlineApproaching, ComplianceAlertKind.TicinoDeadlineApproaching, 0),
            ("2026-11-20T14:00:00Z", ComplianceAlertStage.TicinoOverdue, ComplianceAlertKind.TicinoOverdue, 0),
            ("2026-11-21T13:59:59Z", ComplianceAlertStage.TicinoOverdue, ComplianceAlertKind.TicinoOverdue, 0),
            ("2026-11-21T14:00:00Z", ComplianceAlertStage.TicinoOverdueReminder(1), ComplianceAlertKind.TicinoOverdue, 1),
            ("2026-11-22T14:00:00Z", ComplianceAlertStage.TicinoOverdueReminder(2), ComplianceAlertKind.TicinoOverdue, 2),
            ("2026-11-23T13:59:59Z", ComplianceAlertStage.TicinoOverdueReminder(2), ComplianceAlertKind.TicinoOverdue, 2),

            ("2026-11-23T14:00:00Z", null, null, 0),
            ("2026-12-01T09:00:00Z", null, null, 0),
        ];

        foreach (var (now, stage, kind, reminder) in timeline)
        {
            var step = Step(day, due, now, expected);

            Assert.True(stage == step?.Stage, $"at {now}: expected stage {stage}, got {step?.Stage}");
            if (step is null)
                continue;

            Assert.Equal(kind, step.Alert.Kind);
            Assert.Equal(reminder, step.Alert.ReminderNumber);
            Assert.Equal(2, step.Alert.MaxReminders);
            Assert.Equal(ComplianceAlertType.TicinoNotification, step.Type);
            Assert.Equal(ComplianceAlertType.TicinoNotification, step.Alert.Type);
            Assert.Equal("2026-11-19", step.ReferenceKey);
            Assert.Equal(day, step.Alert.ArrivalDay);
            Assert.Equal(Utc(due), step.Alert.DueAtUtc);
            Assert.Equal(PropertyId, step.Alert.PropertyId);
            Assert.Null(step.Alert.Period);
        }
    }

    [Fact]
    public void DueTicinoStep_AfterAnOutage_SendsTheHighestStageDueOnly_NeverTheBacklog()
    {
        var step = Step(new DateOnly(2026, 11, 19), "2026-11-20T14:00:00Z", "2026-11-20T09:00:00Z", new TimeOnly(15, 30));

        Assert.Equal(ComplianceAlertStage.TicinoDeadlineApproaching, step!.Stage);
    }

    [Fact]
    public void DueTicinoStep_WithNoReminders_TheOverdueAlertIsTheLastStage_AndIsDroppedADayLater()
    {
        var options = new ComplianceOptions { TicinoMaxOverdueReminders = 0 };
        var day = new DateOnly(2026, 11, 19);

        Assert.Equal(ComplianceAlertStage.TicinoOverdue, Step(day, "2026-11-20T14:00:00Z", "2026-11-20T14:00:00Z", null, options)!.Stage);
        Assert.Equal(ComplianceAlertStage.TicinoOverdue, Step(day, "2026-11-20T14:00:00Z", "2026-11-21T13:59:59Z", null, options)!.Stage);
        Assert.Null(Step(day, "2026-11-20T14:00:00Z", "2026-11-21T14:00:00Z", null, options));
    }

    [Fact]
    public void DueTicinoStep_TheThresholdsAreOptions()
    {
        var options = new ComplianceOptions { TicinoDueWarningHours = 2, TicinoMaxOverdueReminders = 1, TicinoOverdueReminderHours = 12 };
        var day = new DateOnly(2026, 11, 19);
        const string due = "2026-11-20T14:00:00Z";

        Assert.Equal(ComplianceAlertStage.TicinoFileToPrepare, Step(day, due, "2026-11-20T11:59:59Z", null, options)!.Stage);
        Assert.Equal(ComplianceAlertStage.TicinoDeadlineApproaching, Step(day, due, "2026-11-20T12:00:00Z", null, options)!.Stage);
        Assert.Equal(ComplianceAlertStage.TicinoOverdueReminder(1), Step(day, due, "2026-11-21T02:00:00Z", null, options)!.Stage);
        Assert.Null(Step(day, due, "2026-11-22T02:00:00Z", null, options));
    }

    [Fact]
    public void DueTicinoStep_WithNoRecordedArrival_TheDeadlineIsTheEndOfTheDay_AndTheSixHoursCoincideWithTheDefaultFirstStage()
    {
        var day = new DateOnly(2026, 11, 19);
        var due = RomeCalendar.StartOfDayUtc(day.AddDays(1)).ToString("O", CultureInfo.InvariantCulture);

        Assert.Null(Step(day, due, "2026-11-19T16:59:59Z"));
        Assert.Equal(ComplianceAlertStage.TicinoDeadlineApproaching, Step(day, due, "2026-11-19T17:00:00Z")!.Stage);
        Assert.Equal(ComplianceAlertStage.TicinoOverdue, Step(day, due, "2026-11-19T23:00:00Z")!.Stage);
    }

    [Fact]
    public void DueTicinoStep_AbsurdOptions_AreHeldToTheirRange_AndNeverThrow()
    {
        var options = new ComplianceOptions
        {
            TicinoArrivalAlertDelayHours = int.MaxValue,
            TicinoArrivalAlertDefaultHourLocal = int.MinValue,
            TicinoDueWarningHours = int.MaxValue,
            TicinoMaxOverdueReminders = int.MaxValue,
            TicinoOverdueReminderHours = int.MinValue,
        };

        var step = Step(new DateOnly(2026, 11, 19), "2026-11-20T14:00:00Z", "2026-11-20T14:00:00Z", new TimeOnly(23, 59), options);

        Assert.NotNull(step);
    }

    [Fact]
    public void TicinoReferenceKey_IsTheArrivalDay_AndFitsTheColumn()
    {
        var key = ComplianceAlertSchedule.TicinoReferenceKey(new DateOnly(2026, 3, 9));

        Assert.Equal("2026-03-09", key);
        Assert.InRange(key.Length, 1, ComplianceAlertState.ReferenceKeyMaxLength);
    }

    [Fact]
    public void TheStagesOfTheTicinoNotification_AreDistinct_InTheOrderOfTheSequence()
    {
        Assert.Equal(
            [1, 2, 3, 4, 5],
            new[]
            {
                ComplianceAlertStage.TicinoFileToPrepare,
                ComplianceAlertStage.TicinoDeadlineApproaching,
                ComplianceAlertStage.TicinoOverdue,
                ComplianceAlertStage.TicinoOverdueReminder(1),
                ComplianceAlertStage.TicinoOverdueReminder(2),
            });
    }
}

public class ComplianceAlertTicinoTests : IDisposable
{
    private const string GuestFirstName = "Giulia";
    private const string GuestLastName = "Rossi";
    private const string GuestEmail = "giulia.rossi@example.com";
    private const string DocumentNumber = "AB1234567";

    private static readonly DateOnly Day = new(2026, 11, 19);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _pushes = new();
    private readonly Mock<IFeatureFlags> _flags = new();
    private readonly Mock<IComplianceAlertFollowUp> _followUp = new();
    private readonly Mock<IComplianceAlertRecipientResolver> _recipients = new();
    private readonly Mock<IContextAuthorizationService> _permissions = new();
    private readonly CapturingLogger<ComplianceAlertService> _log = new();
    private readonly ComplianceOptions _options = new();
    private readonly Dictionary<Guid, Tenant> _tenants = [];

    public ComplianceAlertTicinoTests()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.TicinoNotificationAlerts)).Returns(true);
        _recipients
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid orgId, Guid propertyId, CancellationToken _) =>
                new ComplianceAlertRecipients([_tenants[orgId].Address], [_tenants[orgId].OwnerId]));

        // The alert of the 24 hours is told to the people who can act on it (alloggiati.submit): the audience is read with that permission. Here the owner of the org holds it; the
        // audience that leaves out who does not is proved with the real services in ComplianceAlertAudienceTests.
        _recipients
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NotificationPermission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid orgId, Guid propertyId, NotificationPermission _, CancellationToken _) =>
                new ComplianceAlertRecipients([_tenants[orgId].Address], [_tenants[orgId].OwnerId]));
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(It.IsAny<string>(), ComplianceAlertAudience.AlloggiatiSubmit.ContextKey, ComplianceAlertAudience.AlloggiatiSubmit.PermissionKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public void Dispose() => _db.Dispose();

    private sealed record Tenant(Guid OrgId, Guid PropertyId, string OwnerId, string Address, string PropertyName);

    private ComplianceAlertService Service(bool withTheDaysToNotify = true, ITicinoNotificationService? daysToNotify = null) =>
        new(
            _db,
            Mock.Of<IComplianceStatusService>(),
            Mock.Of<IComplianceConnectionHealthService>(),
            Mock.Of<IPropertySenderStore>(),
            _recipients.Object,
            Mock.Of<ITouristTaxDeadlineSource>(),
            new NotificationService(_db, _emails, _pushes, EmailTestHelpers.Links(), NullLogger<NotificationService>.Instance),
            _followUp.Object,
            _flags.Object,
            Options.Create(_options),
            _log,
            _clock,
            daysToNotify ?? (withTheDaysToNotify ? new TicinoNotificationService(_db, _clock) : null));

    private async Task<Tenant> SeedTenantAsync(string key, bool ticino = true, string? structureId = "NL-99999999", string timezone = "Europe/Zurich")
    {
        foreach (var (code, canton, name) in new[] { ("5192", "TI", "Lugano"), ("261", "ZH", "Zürich") })
        {
            if (!await _db.SwissMunicipalities.AnyAsync(m => m.MunicipalityCode == code))
                _db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = code, CantonCode = canton, OfficialName = name });
        }

        var org = OrgTeamTestData.AddOrg(_db);
        var address = $"{key}@example.com";
        org.ContactEmail = address;
        var ownerId = $"auth0|{key}-owner";
        OrgTeamTestData.AddUser(_db, ownerId, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddMember(_db, ownerId, org.Id, OrgRole.Owner);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = ownerId,
            Name = $"Casa {key}",
            Address = "Via Test 1",
            City = "Lugano",
            PostalCode = "6900",
            Country = "CH",
            Currency = "CHF",
            Timezone = timezone,
            SwissMunicipalityCode = ticino ? "5192" : "261",
            TicinoStructureId = structureId,
            RentalMode = RentalMode.Short,
            IsActive = true,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();

        var tenant = new Tenant(org.Id, property.Id, ownerId, address, property.Name);
        _tenants[org.Id] = tenant;
        return tenant;
    }

    private enum GuestRows
    {
        None,
        Incomplete,
        Complete,
    }

    private async Task<Booking> AddStayAsync(
        Tenant tenant,
        DateOnly day,
        string? expectedArrival = "15:30",
        string? arrivedAtUtc = null,
        BookingStatus status = BookingStatus.Confirmed,
        DateTime? noShowAt = null,
        GuestRows guestRows = GuestRows.None)
    {
        var guest = new Guest { OrgId = tenant.OrgId, FirstName = GuestFirstName, LastName = GuestLastName, Email = GuestEmail };
        var checkIn = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var booking = new Booking
        {
            OrgId = tenant.OrgId,
            PropertyId = tenant.PropertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(2),
            Status = status,
            NoShowAt = noShowAt,
            NumberOfGuests = 1,
            ExpectedArrivalTime = expectedArrival,
            ArrivedAt = arrivedAtUtc is null ? null : DateTime.Parse(arrivedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        };
        _db.AddRange(guest, booking);
        if (guestRows != GuestRows.None)
        {
            var complete = guestRows == GuestRows.Complete;
            _db.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = tenant.OrgId,
                Position = 1,
                Type = StayGuestType.SingleGuest,
                FirstName = GuestFirstName,
                LastName = GuestLastName,
                DateOfBirth = complete ? new DateTime(1985, 4, 2, 0, 0, 0, DateTimeKind.Utc) : null,
                CitizenshipName = complete ? "ITA" : string.Empty,
                DocumentType = complete ? GuestDocumentType.Passport : null,
                DocumentNumber = DocumentNumber,
            });
        }

        await _db.SaveChangesAsync();
        return booking;
    }

    private async Task<int> RunAtAsync(string utc)
    {
        _clock.SetUtcNow(DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        return (await Service().RunAsync()).Sent;
    }

    private static string Subject(RecordingEmailQueue queue, int index) => queue.Snapshot()[index].Content.Subject;

    private static readonly DateTime DayDeadline = new(2026, 11, 20, 14, 0, 0, DateTimeKind.Utc);

    private static Mock<ITicinoNotificationService> DaysToNotifyWith(Tenant tenant)
    {
        IReadOnlyList<TicinoPendingDay> listed = [new TicinoPendingDay(tenant.OrgId, tenant.PropertyId, tenant.PropertyName, Day, 1, DayDeadline)];
        var days = new Mock<ITicinoNotificationService>();
        days.Setup(d => d.ListPendingDaysAsync(null, null, Day, It.IsAny<CancellationToken>())).ReturnsAsync(listed);
        return days;
    }

    private static TicinoNotificationDay DetailOfTheDay(Tenant tenant, IReadOnlyList<TicinoPendingDay> pendingDays) =>
        new(tenant.PropertyId, Day, "NL-99999999", 1, new TicinoDayPlan([], []), 0, DayDeadline, pendingDays);

    [Fact]
    public async Task TicinoAlerts_EachStageOnce_Test()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");

        // The hourly job from the morning of the arrival day to the day after the last stage, and every hour twice (a retry, a manual trigger, a second server).
        var sentByHour = new List<(DateTimeOffset At, int Sent)>();
        for (var at = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero); at <= new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero); at = at.AddHours(1))
        {
            var sent = await RunAtAsync(at.ToString("O", CultureInfo.InvariantCulture)) + await RunAtAsync(at.ToString("O", CultureInfo.InvariantCulture));
            if (sent > 0)
                sentByHour.Add((at, sent));
        }

        Assert.Equal(
            [
                (new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 21, 14, 0, 0, TimeSpan.Zero), 1),
                (new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero), 1),
            ],
            sentByHour);
        var state = await _db.ComplianceAlertStates.AsNoTracking().SingleAsync();
        Assert.Equal(
            (tenant.PropertyId, tenant.OrgId, ComplianceAlertType.TicinoNotification, "2026-11-19", ComplianceAlertStage.TicinoOverdueReminder(2), 5),
            (state.PropertyId, state.OrgId, state.Type, state.ReferenceKey, state.Stage, state.AlertCount));

        Assert.Equal(5, _emails.Snapshot().Count);
        Assert.All(_emails.Snapshot(), e => Assert.Equal(("a@example.com", EmailTemplates.Names.TicinoNotificationAlert), (e.To, e.Template)));
        Assert.StartsWith("Notifica alla polizia da preparare - Casa a", Subject(_emails, 0), StringComparison.Ordinal);
        Assert.StartsWith("Notifica alla polizia in scadenza - Casa a", Subject(_emails, 1), StringComparison.Ordinal);
        Assert.StartsWith("Notifica alla polizia scaduta - Casa a", Subject(_emails, 2), StringComparison.Ordinal);
        Assert.Contains("Promemoria 1 di 2.", _emails.Snapshot()[3].Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Promemoria 2 di 2.", _emails.Snapshot()[4].Content.HtmlBody, StringComparison.Ordinal);
        var pushes = _pushes.Queued;
        Assert.Equal(5, pushes.Count);
        Assert.Equal(5, pushes.Select(p => p.DeliveryKey).Distinct().Count());
        Assert.Equal(
            [PushTypes.TicinoNotificationDue, PushTypes.TicinoNotificationDue, PushTypes.TicinoNotificationOverdue, PushTypes.TicinoNotificationOverdue, PushTypes.TicinoNotificationOverdue],
            pushes.Select(p => p.Payload.Type).ToArray());
        Assert.All(pushes, p =>
        {
            Assert.Equal(PushAudience.PropertyHosts(tenant.PropertyId, ComplianceAlertAudience.AlloggiatiSubmit), p.Audience);
            Assert.Equal(PushRoutes.Properties, p.Payload.Route);
            Assert.Null(p.Payload.BookingId);
            Assert.Null(p.Payload.ServiceRequestId);
        });
    }

    [Fact]
    public async Task TicinoAlerts_ThePropertyClock_DecidesTheArrivalDayStage()
    {
        var tenant = await SeedTenantAsync("a", timezone: "UTC");
        await AddStayAsync(tenant, Day, expectedArrival: null, arrivedAtUtc: "2026-11-19T10:00:00Z");

        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(1, await RunAtAsync("2026-11-19T18:00:00Z"));
        Assert.Equal(ComplianceAlertStage.TicinoFileToPrepare, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
    }

    [Fact]
    public async Task TicinoAlerts_TheEarliestExpectedCheckInOfTheDay_MovesTheFirstStage_AndAStayThatOwesNothingDoesNotCount()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, expectedArrival: "16:00", arrivedAtUtc: "2026-11-19T15:00:00Z");
        await AddStayAsync(tenant, Day, expectedArrival: "11:00", arrivedAtUtc: "2026-11-19T15:30:00Z");

        await AddStayAsync(tenant, Day, expectedArrival: "06:00", noShowAt: new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc));
        await AddStayAsync(tenant, Day, expectedArrival: "06:30", status: BookingStatus.Cancelled);
        await AddStayAsync(tenant, Day, expectedArrival: "07:00", status: BookingStatus.Pending);

        Assert.Equal(0, await RunAtAsync("2026-11-19T11:59:00Z"));
        Assert.Equal(1, await RunAtAsync("2026-11-19T12:00:00Z"));
        Assert.Equal(1, await RunAtAsync("2026-11-20T09:00:00Z"));
        Assert.Equal(ComplianceAlertStage.TicinoDeadlineApproaching, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
    }

    [Fact]
    public async Task TicinoAlerts_StaysThatOweNothing_AreNeverAlerted()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, noShowAt: new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc));
        await AddStayAsync(tenant, Day, status: BookingStatus.Cancelled);
        await AddStayAsync(tenant, Day, status: BookingStatus.Pending);

        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T01:00:00Z"));
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
    }

    [Fact]
    public async Task TicinoAlerts_ADayClosedByAReceipt_StopsTheAlerts_Test()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        Assert.Equal(1, await RunAtAsync("2026-11-19T17:00:00Z"));

        _db.SwissComplianceReceipts.Add(new SwissComplianceReceipt
        {
            OrgId = tenant.OrgId,
            PropertyId = tenant.PropertyId,
            Type = "Police",
            ReportDate = DateTime.SpecifyKind(Day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            Reference = "TI-receipt",
            UploadedFileHash = "abc123",
        });
        await _db.SaveChangesAsync();

        Assert.Equal(0, await RunAtAsync("2026-11-20T08:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T14:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-21T14:00:00Z"));
        Assert.Single(_emails.Snapshot());
        Assert.Single(_pushes.Queued);
        Assert.Equal(ComplianceAlertStage.TicinoFileToPrepare, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
    }

    [Fact]
    public async Task TicinoAlerts_ADayClosedBeforeTheFirstAlert_NeverAlerts()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        _db.SwissComplianceReceipts.Add(new SwissComplianceReceipt
        {
            OrgId = tenant.OrgId,
            PropertyId = tenant.PropertyId,
            Type = "Police",
            ReportDate = DateTime.SpecifyKind(Day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            Reference = "TI-receipt",
            UploadedFileHash = "abc123",
        });
        await _db.SaveChangesAsync();

        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T14:00:00Z"));
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
    }

    [Fact]
    public async Task TicinoAlerts_ADayThatClosesBetweenTheListAndTheSend_SendsNothingAndWritesNoState()
    {
        var tenant = await SeedTenantAsync("a");
        var days = DaysToNotifyWith(tenant);
        days.Setup(d => d.GetDayAsync(tenant.PropertyId, Day, It.IsAny<CancellationToken>())).ReturnsAsync(DetailOfTheDay(tenant, []));
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

        var result = await Service(daysToNotify: days.Object).RunAsync();

        days.Verify(d => d.GetDayAsync(tenant.PropertyId, Day, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("property_not_in_ticino")]
    [InlineData("property_not_found")]
    public async Task TicinoAlerts_APropertyThatIsNoLongerOfTicinoWhenTheDayIsRead_SendsNothingAndWritesNoState(string code)
    {
        var tenant = await SeedTenantAsync("a");
        var days = DaysToNotifyWith(tenant);
        // The property left canton Ticino (its municipality was changed) or is gone since the list was read: the service of the days says so when the detail of the day is asked.
        Exception gone = code == "property_not_found"
            ? new NotFoundException($"Property {tenant.PropertyId} not found") { Code = code, MessageKey = "PropertyNotFound" }
            : new DomainRuleException(code, "PropertyNotInTicino", tenant.PropertyId);
        days.Setup(d => d.GetDayAsync(tenant.PropertyId, Day, It.IsAny<CancellationToken>())).ThrowsAsync(gone);
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

        var result = await Service(daysToNotify: days.Object).RunAsync();

        days.Verify(d => d.GetDayAsync(tenant.PropertyId, Day, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
        _recipients.VerifyNoOtherCalls();

        // An answer to expect, told at information level with ids only: neither a warning nor an error of the run.
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TicinoAlerts_APropertyInAnotherCanton_IsNeverAlerted()
    {
        var zurich = await SeedTenantAsync("zh", ticino: false);
        await AddStayAsync(zurich, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");

        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T14:00:00Z"));
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
    }

    [Fact]
    public async Task TicinoAlerts_OtherOrganizationNeverAlerted_Test()
    {
        var a = await SeedTenantAsync("a");
        var b = await SeedTenantAsync("b");
        var c = await SeedTenantAsync("c");
        await AddStayAsync(a, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        await AddStayAsync(b, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");

        Assert.Equal(2, await RunAtAsync("2026-11-19T17:00:00Z"));

        var emails = _emails.Snapshot();
        Assert.Equal(2, emails.Count);
        Assert.Contains(emails, e => e.To == a.Address && e.Content.Subject.Contains(a.PropertyName, StringComparison.Ordinal));
        Assert.Contains(emails, e => e.To == b.Address && e.Content.Subject.Contains(b.PropertyName, StringComparison.Ordinal));
        Assert.DoesNotContain(emails, e => e.To == c.Address);
        var alloggiatiSubmit = ComplianceAlertAudience.AlloggiatiSubmit;
        _recipients.Verify(r => r.ForPropertyAsync(a.OrgId, a.PropertyId, alloggiatiSubmit, It.IsAny<CancellationToken>()), Times.Once);
        _recipients.Verify(r => r.ForPropertyAsync(b.OrgId, b.PropertyId, alloggiatiSubmit, It.IsAny<CancellationToken>()), Times.Once);
        _recipients.Verify(r => r.ForPropertyAsync(c.OrgId, It.IsAny<Guid>(), It.IsAny<NotificationPermission>(), It.IsAny<CancellationToken>()), Times.Never);
        _recipients.Verify(r => r.ForPropertyAsync(a.OrgId, b.PropertyId, It.IsAny<NotificationPermission>(), It.IsAny<CancellationToken>()), Times.Never);
        _recipients.Verify(r => r.ForPropertyAsync(b.OrgId, a.PropertyId, It.IsAny<NotificationPermission>(), It.IsAny<CancellationToken>()), Times.Never);
        _recipients.Verify(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Equal(
            new[] { PushAudience.PropertyHosts(a.PropertyId, alloggiatiSubmit), PushAudience.PropertyHosts(b.PropertyId, alloggiatiSubmit) }.OrderBy(x => x.Id),
            _pushes.Queued.Select(p => p.Audience).OrderBy(x => x.Id));
        var publisher = new RecordingPublisher();
        foreach (var push in _pushes.Queued)
        {
            var job = new InAppNotificationJob(_db, _clock, publisher, NullLogger<InAppNotificationJob>.Instance, _permissions.Object);
            await job.CreateAsync(push.DeliveryKey, QueuedInAppNotification.From(push.Audience, push.Payload, _clock.GetUtcNow().UtcDateTime), CancellationToken.None);
        }

        var rows = await _db.InAppNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(
            new[] { (a.OwnerId, a.OrgId), (b.OwnerId, b.OrgId) }.OrderBy(x => x.Item1),
            rows.Select(r => (r.UserId, r.OrgId)).OrderBy(x => x.Item1));
        Assert.DoesNotContain(rows, r => r.UserId == c.OwnerId);

        Assert.Equal(
            new[] { (a.OwnerId, a.PropertyId), (b.OwnerId, b.PropertyId) }.OrderBy(x => x.Item1),
            rows.Select(r => (r.UserId, r.EntityId ?? Guid.Empty)).OrderBy(x => x.Item1));
    }

    private ComplianceAlertService ServiceWithTheRealAudience(IContextAuthorizationService permissions) =>
        new(
            _db,
            Mock.Of<IComplianceStatusService>(),
            Mock.Of<IComplianceConnectionHealthService>(),
            Mock.Of<IPropertySenderStore>(),
            new ComplianceAlertRecipientResolver(_db, new NotificationPreferenceService(_db, permissions, _clock, NullLogger<NotificationPreferenceService>.Instance)),
            Mock.Of<ITouristTaxDeadlineSource>(),
            new NotificationService(_db, _emails, _pushes, EmailTestHelpers.Links(), NullLogger<NotificationService>.Instance),
            _followUp.Object,
            _flags.Object,
            Options.Create(_options),
            _log,
            _clock,
            new TicinoNotificationService(_db, _clock));

    private ContextAuthorizationService RealPermissions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authorization:UserCacheSeconds"] = "0" }).Build();
        var accessor = new HttpContextAccessor();
        return new ContextAuthorizationService(
            new UserAuthorizationSnapshotStore(_db, new MemoryCache(new MemoryCacheOptions()), accessor, configuration),
            new LegalDocumentService(new ConfigurationBuilder().Build(), NullLogger<LegalDocumentService>.Instance),
            accessor,
            NullLogger<ContextAuthorizationService>.Instance);
    }

    /// <summary>The owner finished the onboarding (the host contexts are withheld before it, and with them the permission): it holds <c>alloggiati.submit</c> on its token.</summary>
    private async Task OnboardOwnerAsync(Tenant tenant)
    {
        var owner = await _db.Users.SingleAsync(u => u.Id == tenant.OwnerId);
        await HostOnboardingSeed.MarkOnboardedAsync(_db, owner, tenant.OrgId, new LegalDocumentService(new ConfigurationBuilder().Build(), NullLogger<LegalDocumentService>.Instance));
        await _db.SaveChangesAsync();
    }

    /// <summary>A collaborator «Solo alcuni» of the org, given the flat and put in charge of it, with the role of the staff (it does not hold <c>alloggiati.submit</c>).</summary>
    private async Task<string> AddCollaboratorInChargeAsync(Tenant tenant)
    {
        var id = tenant.OwnerId.Replace("-owner", "-collab", StringComparison.Ordinal);
        var user = OrgTeamTestData.AddUser(_db, id, tenant.OrgId);
        await HostOnboardingSeed.MarkOnboardedAsync(_db, user, tenant.OrgId, new LegalDocumentService(new ConfigurationBuilder().Build(), NullLogger<LegalDocumentService>.Instance));
        OrgTeamTestData.AddMember(_db, id, tenant.OrgId, OrgRole.Collaborator).PropertyScope = PropertyScope.Selected;
        OrgTeamTestData.AddMembership(_db, id, "short-rent", "staff");
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = tenant.OrgId, UserId = id, PropertyId = tenant.PropertyId });
        (await _db.Properties.SingleAsync(p => p.Id == tenant.PropertyId)).ResponsibleUserId = id;
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task<string[]> BellOfTheQueuedAlertsAsync(IContextAuthorizationService permissions)
    {
        var job = new InAppNotificationJob(_db, _clock, new RecordingPublisher(), NullLogger<InAppNotificationJob>.Instance, permissions);
        foreach (var push in _pushes.Queued)
            await job.CreateAsync(push.DeliveryKey, QueuedInAppNotification.From(push.Audience, push.Payload, _clock.GetUtcNow().UtcDateTime), CancellationToken.None);

        return (await _db.InAppNotifications.AsNoTracking().Select(n => n.UserId).ToListAsync()).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task TicinoAlerts_ACollaboratorInChargeWithoutAlloggiatiSubmit_IsNotToldInTheApp_ButTheOwnerAndTheAddressOfTheOrgAre_Test()
    {
        var tenant = await SeedTenantAsync("a");
        await OnboardOwnerAsync(tenant);
        var collaborator = await AddCollaboratorInChargeAsync(tenant);
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        var permissions = RealPermissions();

        _clock.SetUtcNow(DateTimeOffset.Parse("2026-11-19T17:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        Assert.Equal(1, (await ServiceWithTheRealAudience(permissions).RunAsync()).Sent);

        Assert.Equal(ComplianceAlertStage.TicinoFileToPrepare, (await _db.ComplianceAlertStates.SingleAsync()).Stage);
        Assert.Equal(tenant.Address, Assert.Single(_emails.Snapshot()).To);
        var push = Assert.Single(_pushes.Queued);
        Assert.Equal(PushAudience.PropertyHosts(tenant.PropertyId, ComplianceAlertAudience.AlloggiatiSubmit), push.Audience);
        Assert.Equal([tenant.OwnerId], await BellOfTheQueuedAlertsAsync(permissions));
        Assert.DoesNotContain(collaborator, await _db.InAppNotifications.AsNoTracking().Select(n => n.UserId).ToListAsync());

        var italian = await new ComplianceAlertRecipientResolver(_db, new NotificationPreferenceService(_db, permissions, _clock, NullLogger<NotificationPreferenceService>.Instance))
            .ForPropertyAsync(tenant.OrgId, tenant.PropertyId, CancellationToken.None);
        Assert.Contains(collaborator, italian.UserIds);
    }

    [Fact]
    public async Task TicinoAlerts_WhenNobodyWhoReachesTheFlatHoldsAlloggiatiSubmit_TheAlertGoesOnAsToday_OnlyNobodyIsToldInTheApp_Test()
    {
        var tenant = await SeedTenantAsync("a");
        var collaborator = await AddCollaboratorInChargeAsync(tenant);
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        var permissions = RealPermissions();

        _clock.SetUtcNow(DateTimeOffset.Parse("2026-11-19T17:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        Assert.Equal(1, (await ServiceWithTheRealAudience(permissions).RunAsync()).Sent);

        var state = await _db.ComplianceAlertStates.SingleAsync();
        Assert.Equal((ComplianceAlertType.TicinoNotification, "2026-11-19", ComplianceAlertStage.TicinoFileToPrepare), (state.Type, state.ReferenceKey, state.Stage));
        Assert.Equal(tenant.Address, Assert.Single(_emails.Snapshot()).To);
        var listed = await new TicinoNotificationService(_db, _clock).ListPendingDaysAsync(tenant.OrgId, null, Day);
        Assert.Equal(Day, Assert.Single(listed).Day);

        Assert.Empty(await BellOfTheQueuedAlertsAsync(permissions));
        Assert.Contains("nobody who reaches the property holds alloggiati.submit", _log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(collaborator, _log.AllOutput, StringComparison.Ordinal);

        Assert.Equal(0, (await ServiceWithTheRealAudience(permissions).RunAsync()).Sent);
    }

    [Fact]
    public async Task TicinoAlerts_PayloadHasNoPersonalData_Test()
    {
        var tenant = await SeedTenantAsync("a", structureId: null);
        var booking = await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z", guestRows: GuestRows.Incomplete);

        Assert.Equal(1, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(1, await RunAtAsync("2026-11-20T08:00:00Z"));
        Assert.Equal(1, await RunAtAsync("2026-11-20T14:00:00Z"));

        string[] personal = [GuestFirstName, GuestLastName, GuestEmail, DocumentNumber, booking.BookingCode, booking.Id.ToString("N"), booking.GuestId.ToString("N")];
        var state = await _db.ComplianceAlertStates.AsNoTracking().SingleAsync();
        var texts = _emails.Snapshot().SelectMany(e => new[] { e.Content.Subject, e.Content.HtmlBody, e.To ?? string.Empty })
            .Concat(_pushes.Queued.SelectMany(p => new[] { p.DeliveryKey, p.Payload.Title, p.Payload.Body, p.Payload.Type, p.Payload.Route }))
            .Concat([state.ReferenceKey, state.Type.ToString(), _log.AllOutput])
            .ToList();
        Assert.NotEmpty(texts);
        foreach (var value in personal.Where(v => !string.IsNullOrEmpty(v)))
            Assert.DoesNotContain(texts, t => t.Contains(value, StringComparison.OrdinalIgnoreCase));

        foreach (var email in _emails.Snapshot())
        {
            Assert.Contains(tenant.PropertyName, email.Content.Subject, StringComparison.Ordinal);
            Assert.Contains("19/11/2026", email.Content.Subject, StringComparison.Ordinal);
            Assert.Contains("numero identificativo della struttura, data di nascita, nazionalità, tipo di documento", email.Content.HtmlBody, StringComparison.Ordinal);
        }

        Assert.All(
            _pushes.Queued,
            p =>
            {
                Assert.Contains(tenant.PropertyName, p.Payload.Body, StringComparison.Ordinal);
                Assert.Contains("19/11/2026", p.Payload.Body, StringComparison.Ordinal);
                Assert.Contains("Mancano: numero identificativo della struttura, data di nascita, nazionalità, tipo di documento.", p.Payload.Body, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task TicinoAlerts_ADayWithNoCompleteRow_StillAlerts_AndTheStageDoesNotWaitForTheData()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z", guestRows: GuestRows.Incomplete);

        Assert.Equal(1, await RunAtAsync("2026-11-19T17:00:00Z"));

        var email = Assert.Single(_emails.Snapshot());
        Assert.Contains("Dati ancora mancanti per il file: data di nascita, nazionalità, tipo di documento.", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("numero identificativo", email.Content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicinoAlerts_ADayWithCompleteData_NamesNothingThatMisses()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z", guestRows: GuestRows.Complete);

        Assert.Equal(1, await RunAtAsync("2026-11-19T17:00:00Z"));

        Assert.DoesNotContain("Dati ancora mancanti", Assert.Single(_emails.Snapshot()).Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Mancano:", Assert.Single(_pushes.Queued).Payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TicinoAlerts_FlagOffSendsNothing_Test()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        _flags.Setup(f => f.IsEnabled(FeatureFlags.TicinoNotificationAlerts)).Returns(false);

        var result = await Service().RunAsync();
        Assert.Equal(0, await RunAtAsync("2026-11-19T17:00:00Z"));
        Assert.Equal(0, await RunAtAsync("2026-11-20T14:00:00Z"));

        Assert.True(result.Skipped);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
        Assert.Empty(await _db.InAppNotifications.ToListAsync());
        _recipients.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TicinoAlerts_TheirOwnFlagIsEnough_TheTrafficLightIsNotNeeded()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

        var result = await Service().RunAsync();

        Assert.False(result.Skipped);
        Assert.Equal(1, result.Sent);
        _flags.Verify(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight), Times.Once);
    }

    [Fact]
    public async Task TicinoAlerts_FlagOnButNoServiceOfTheDays_SendNothing_AndSayWhy()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));

        var result = await Service(withTheDaysToNotify: false).RunAsync();

        Assert.False(result.Skipped);
        Assert.Equal(0, result.Sent);
        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
        Assert.Empty(await _db.ComplianceAlertStates.ToListAsync());
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not registered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TicinoAlerts_LeaveNoTodoAndNoPopupOfTheRouter()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");

        await RunAtAsync("2026-11-19T17:00:00Z");
        await RunAtAsync("2026-11-20T14:00:00Z");

        _followUp.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TicinoAlerts_TheBellAndTheLiveUpdate_GetTheNewTypesWithThePropertyAndNoText()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z", guestRows: GuestRows.Incomplete);
        await RunAtAsync("2026-11-19T17:00:00Z");
        await RunAtAsync("2026-11-20T14:00:00Z");

        var publisher = new RecordingPublisher();
        foreach (var push in _pushes.Queued)
        {
            var job = new InAppNotificationJob(_db, _clock, publisher, NullLogger<InAppNotificationJob>.Instance, _permissions.Object);
            await job.CreateAsync(push.DeliveryKey, QueuedInAppNotification.From(push.Audience, push.Payload, _clock.GetUtcNow().UtcDateTime), CancellationToken.None);
        }

        var rows = await _db.InAppNotifications.AsNoTracking().OrderBy(r => r.Type).ToListAsync();
        Assert.Equal([PushTypes.TicinoNotificationDue, PushTypes.TicinoNotificationOverdue], rows.Select(r => r.Type).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(tenant.OwnerId, r.UserId);
            Assert.Equal(tenant.PropertyId, r.EntityId);
        });

        Assert.Equal(
            [(tenant.OwnerId, PushTypes.TicinoNotificationDue, (Guid?)tenant.PropertyId), (tenant.OwnerId, PushTypes.TicinoNotificationOverdue, (Guid?)tenant.PropertyId)],
            publisher.Published.ToArray());
    }

    [Theory]
    [InlineData(PushTypes.TicinoNotificationDue)]
    [InlineData(PushTypes.TicinoNotificationOverdue)]
    public void TheBellEntryOfATicinoAlert_IsAboutTheProperty_NotABooking(string type)
    {
        var propertyId = Guid.NewGuid();
        var payload = new PushNotificationPayload("title", "body", type, null, PushRoutes.Properties);

        var queued = QueuedInAppNotification.From(PushAudience.PropertyHosts(propertyId), payload, new DateTime(2026, 11, 19, 17, 0, 0, DateTimeKind.Utc));

        Assert.True(PushTypes.IsTicinoNotification(type));
        Assert.Equal(propertyId, queued.EntityId);
        Assert.Equal(type, queued.Type);
    }

    [Fact]
    public void TheEntityOfEveryOtherBellEntry_IsUnchanged_OnlyTheTwoTicinoTypesNameTheProperty()
    {
        var propertyId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var at = new DateTime(2026, 11, 19, 17, 0, 0, DateTimeKind.Utc);

        // The ISTAT alert is about a property too and still names no entity.
        var istat = new PushNotificationPayload("title", "body", PushTypes.IstatDeadline, null, PushRoutes.Properties);
        Assert.Null(QueuedInAppNotification.From(PushAudience.PropertyHosts(propertyId), istat, at).EntityId);

        var stay = new PushNotificationPayload("title", "body", PushTypes.AlloggiatiDeadline, bookingId, PushRoutes.Booking(bookingId));
        Assert.Equal(bookingId, QueuedInAppNotification.From(PushAudience.BookingHosts(bookingId), stay, at).EntityId);
        var request = new PushNotificationPayload("title", "body", PushTypes.ServiceRequestCreated, bookingId, PushRoutes.Properties, requestId);
        Assert.Equal(requestId, QueuedInAppNotification.From(PushAudience.PropertyHosts(propertyId), request, at).EntityId);

        Assert.False(PushTypes.IsTicinoNotification(PushTypes.IstatDeadline));
        Assert.False(PushTypes.IsTicinoNotification(null));
        var ticino = new PushNotificationPayload("title", "body", PushTypes.TicinoNotificationDue, null, PushRoutes.Properties);
        Assert.Null(QueuedInAppNotification.From(PushAudience.BookingHosts(bookingId), ticino, at).EntityId);
    }

    [Fact]
    public async Task TheService_ResolvesWithValidateScopes_AndGetsTheServiceOfTheTicinoDays()
    {
        var tenant = await SeedTenantAsync("a");
        await AddStayAsync(tenant, Day, arrivedAtUtc: "2026-11-19T14:00:00Z");
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 19, 17, 0, 0, TimeSpan.Zero));
        var notifications = new Mock<INotificationService>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped(_ => _db);
        services.AddSingleton(_flags.Object);
        services.AddSingleton(_recipients.Object);
        services.AddSingleton(Mock.Of<IComplianceStatusService>());
        services.AddSingleton(Mock.Of<IComplianceConnectionHealthService>());
        services.AddSingleton(Mock.Of<IPropertySenderStore>());
        services.AddSingleton(Mock.Of<ITouristTaxDeadlineSource>());
        services.AddSingleton(_followUp.Object);
        services.AddSingleton(notifications.Object);
        services.AddOptions<ComplianceOptions>();
        services.AddScoped<ITicinoNotificationService, TicinoNotificationService>();
        services.AddScoped<IComplianceAlertService, ComplianceAlertService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IComplianceAlertService>().RunAsync();

        Assert.Equal(1, result.Sent);
        notifications.Verify(
            n => n.SendComplianceAlertAsync(
                It.Is<ComplianceAlert>(a => a.Type == ComplianceAlertType.TicinoNotification
                    && a.Kind == ComplianceAlertKind.TicinoFileToPrepare
                    && a.PropertyId == tenant.PropertyId
                    && a.ArrivalDay == Day),
                It.Is<IReadOnlyCollection<string>>(emails => emails.Single() == tenant.Address),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void ComplianceAlertType_StoredValuesAreNeverRenumbered_AndEveryTypeHasADeclaredPushType()
    {
        Assert.Equal(
            [(1, "IstatPeriod"), (2, "TouristTaxPeriod"), (3, "ConnectionInactive"), (4, "TicinoNotification"), (5, "SwissNotification")],
            Enum.GetValues<ComplianceAlertType>().Select(t => ((int)t, t.ToString())).ToArray());

        var declared = typeof(PushTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var type in Enum.GetValues<ComplianceAlertType>())
        {
            var pushType = PushTypes.ForComplianceAlert(type);
            Assert.Contains(pushType, declared);
            Assert.InRange(pushType.Length, 1, InAppNotification.TypeMaxLength);
        }

        Assert.Equal("ticino-notification-due", PushTypes.TicinoNotificationDue);
        Assert.Equal("ticino-notification-overdue", PushTypes.TicinoNotificationOverdue);
        Assert.Equal(PushTypes.TicinoNotificationDue, PushTypes.ForComplianceAlert(ComplianceAlertType.TicinoNotification));
    }

    [Theory]
    [InlineData(ComplianceAlertKind.TicinoFileToPrepare, "ticino-notification-due")]
    [InlineData(ComplianceAlertKind.TicinoDeadlineApproaching, "ticino-notification-due")]
    [InlineData(ComplianceAlertKind.TicinoOverdue, "ticino-notification-overdue")]
    public void ThePushTypeOfATicinoAlert_IsDueBeforeTheDeadline_AndOverdueAfterIt(ComplianceAlertKind kind, string expected)
    {
        var alert = new ComplianceAlert(ComplianceAlertType.TicinoNotification, Guid.NewGuid(), kind, ArrivalDay: Day);

        Assert.Equal(expected, PushTypes.ForComplianceAlert(alert));
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public void EveryKindOfTicinoAlert_RendersTheEmailAndThePush_InBothLanguages_WithNoPlaceholderLeftAndNoGuestData(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var link = EmailTestHelpers.Links().HostCompliance();
        var due = new DateTime(2026, 11, 20, 14, 0, 0, DateTimeKind.Utc);
        var kinds = Enum.GetValues<ComplianceAlertKind>().Where(k => k.ToString().StartsWith("Ticino", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, kinds.Count);
        foreach (var kind in kinds)
        {
            var alert = new ComplianceAlert(
                ComplianceAlertType.TicinoNotification,
                Guid.NewGuid(),
                kind,
                ReminderNumber: kind == ComplianceAlertKind.TicinoOverdue ? 1 : 0,
                MaxReminders: 2,
                ArrivalDay: Day,
                DueAtUtc: due,
                MissingFields: [TicinoAlertMissing.StructureId, "birthDate", "someFutureCode"]);

            var email = EmailTemplates.TicinoNotificationAlert(culture, alert, "Villa Rosa", link);
            var push = EmailTemplates.TicinoNotificationPush(culture, alert, "Villa Rosa");

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

            Assert.Contains(culture.TwoLetterISOLanguageName == "en" ? "20 November 2026, 15:00" : "20/11/2026 15:00", email.HtmlBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheTypesOfFieldThatMiss_AreTranslatedInTheOrderOfTheFile_AndACodeWithNoTextIsToldAsOtherData()
    {
        string[] codes = ["documentType", "birthDate", TicinoAlertMissing.StructureId, "zzz", "guests", "firstName", "lastName", "nationality", "documentType"];

        Assert.Equal(
            "numero identificativo della struttura, ospiti non ancora registrati, nome, cognome, data di nascita, nazionalità, tipo di documento, altri dati",
            EmailTemplates.TicinoMissingLabels(codes, CultureInfo.GetCultureInfo("it-IT")));
        Assert.Equal(
            "structure identifier, guests not registered yet, first name, last name, date of birth, nationality, document type, other data",
            EmailTemplates.TicinoMissingLabels(codes, CultureInfo.GetCultureInfo("en")));
        Assert.Equal(string.Empty, EmailTemplates.TicinoMissingLabels(null, CultureInfo.GetCultureInfo("en")));
        Assert.Equal(string.Empty, EmailTemplates.TicinoMissingLabels([], CultureInfo.GetCultureInfo("it-IT")));
        Assert.Equal("altri dati", EmailTemplates.TicinoMissingLabels(["<b>x</b>", ""], CultureInfo.GetCultureInfo("it-IT")));
    }

    [Fact]
    public void ThePlaceholderJobOfTheTicinoReminder_NoLongerExists_AndTheHourlyJobIsStillTheOneThatRunsTheAlerts()
    {
        Assert.Null(typeof(StayAlertsJob).Assembly.GetType("Casazen.Web.BackgroundJobs.TicinoReminderJob"));

        var registered = new List<string>();
        var manager = new Mock<Hangfire.IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Hangfire.Common.Job>(), It.IsAny<string>(), It.IsAny<Hangfire.RecurringJobOptions>()))
            .Callback<string, Hangfire.Common.Job, string, Hangfire.RecurringJobOptions>((id, _, _, _) => registered.Add(id));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        Assert.DoesNotContain("ticino-reminder", registered);
        Assert.Contains(StayAlertsJob.RecurringJobId, registered);
    }

    private sealed class RecordingPublisher : IInAppNotificationPublisher
    {
        public List<(string UserId, string? Type, Guid? EntityId)> Published { get; } = [];

        public Task PublishAsync(string userId, string? type, Guid? entityId, CancellationToken cancellationToken = default)
        {
            Published.Add((userId, type, entityId));
            return Task.CompletedTask;
        }
    }
}

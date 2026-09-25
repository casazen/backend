using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Questura = Casazen.Web.BackgroundJobs.RliDeadlineReminderJob.QuesturaThresholds;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// LT-04 (A7-04) on real PostgreSQL: the RLI reminder job works on thresholds of the deadline min(stipula, start) + 30
/// (≤ 15, ≤ 7, ≤ 1 days, overdue from the day after), for every lease not registered yet, once per threshold and
/// deadline, recorded only when the email was sent. LT-07 (A7-08): the Questura communication of an extra-EU tenant has
/// its own thresholds on the 48 hours from the delivery and is never declared by a reminder. Each test has its own
/// database and runs the job with a fake clock.
/// </summary>
public class RliDeadlineReminderJobTests : IAsyncLifetime
{
    // Signed 1/8/2026, start 1/10/2026 → deadline 31/8/2026.
    private static readonly DateTime SignedAt = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartOctober = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Deadline = "2026-08-31";

    private readonly Mock<IEmailService> _email = new();
    private readonly List<(string To, string Subject, string Body)> _sent = [];
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();

        _email
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((to, subject, body) => _sent.Add((to, subject, body)))
            .ReturnsAsync(EmailSendResult.Sent());
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task ExecuteAsync_FifteenDaysBefore_SendsReminderForCorrectedDeadline()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        await RunAsync("2026-08-16T08:00:00Z");

        Assert.Equal([$"t-15:{Deadline}"], await PayloadsAsync(lease));
        var (to, subject, body) = Assert.Single(_sent);
        Assert.Equal("host@example.com", to);
        Assert.Contains("31/08/2026", subject, StringComparison.Ordinal);
        Assert.Contains("Giorni rimanenti: <strong>15</strong>", body, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ExecuteAsync_ThresholdDaySkipped_SendsReminderTheDayAfter()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        await RunAsync("2026-08-16T08:00:00Z"); // 15 days before: t-15
        // No run on 24/8 (7 days before): the next run still sends the ≤ 7 reminder.
        await RunAsync("2026-08-25T08:00:00Z");

        Assert.Equal([$"t-15:{Deadline}", $"t-7:{Deadline}"], await PayloadsAsync(lease));
        Assert.Contains("Giorni rimanenti: <strong>6</strong>", _sent[^1].Body, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ExecuteAsync_TwoRunsSameDay_NoDuplicate()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        await RunAsync("2026-08-25T06:00:00Z");
        await RunAsync("2026-08-25T18:00:00Z");
        await RunAsync("2026-08-26T08:00:00Z"); // still within the ≤ 7 threshold

        Assert.Equal([$"t-7:{Deadline}"], await PayloadsAsync(lease));
        Assert.Single(_sent);
    }

    [PostgresFact]
    public async Task ExecuteAsync_FirstRunCloseToDeadline_SendsOnlyTheMostUrgentThreshold()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        await RunAsync("2026-08-28T08:00:00Z"); // 3 days before: ≤ 7, not also ≤ 15

        Assert.Equal([$"t-7:{Deadline}"], await PayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_DeadlineDay_NotOverdueYet()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        await RunAsync("2026-08-31T08:00:00Z");

        Assert.Equal([$"t-1:{Deadline}"], await PayloadsAsync(lease));
        Assert.DoesNotContain("scaduta", _sent.Single().Subject, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task ExecuteAsync_DayAfterDeadline_SendsOverdueOnce()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        await RunAsync("2026-08-31T08:00:00Z");
        await RunAsync("2026-09-01T08:00:00Z");
        await RunAsync("2026-09-02T08:00:00Z");

        Assert.Equal([$"t-1:{Deadline}", $"overdue:{Deadline}"], await PayloadsAsync(lease));
        Assert.Contains("Registrazione RLI scaduta", _sent[^1].Subject, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ExecuteAsync_AfterMidnightInRome_CountsDaysFromRomeCalendarDay()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);

        // 22:30 UTC on 23/8 is 24/8 in Rome: 7 days to 31/8 (8 on the UTC calendar).
        await RunAsync("2026-08-23T22:30:00Z");

        Assert.Equal([$"t-7:{Deadline}"], await PayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_RegisteredLease_NoReminder()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Registered, StartOctober, SignedAt);

        await RunAsync("2026-09-10T08:00:00Z");

        Assert.Empty(await PayloadsAsync(lease));
        Assert.Empty(_sent);
    }

    [PostgresFact]
    public async Task ExecuteAsync_DraftLeaseWithStartDatePassed_SendsReminderWithNotSignedNotice()
    {
        // Not signed yet, start 1/8: the stipula can only come later, so the deadline is 31/8 from the start date.
        var lease = await SeedLeaseAsync(LeaseStatus.Draft, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), signedAt: null);

        await RunAsync("2026-08-20T08:00:00Z");

        Assert.Equal([$"t-15:{Deadline}"], await PayloadsAsync(lease));
        Assert.Contains("non risulta ancora firmato da tutte le parti", _sent.Single().Body, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ExecuteAsync_DraftLeaseWithStartDateAhead_NoReminder()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.AwaitingSignature, StartOctober, signedAt: null);

        await RunAsync("2026-09-24T08:00:00Z");

        Assert.Empty(await PayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_SignedWithoutStipulaDate_NoReminderDeadlineNotGuessed()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), signedAt: null);

        await RunAsync("2026-09-24T08:00:00Z");

        Assert.Empty(await PayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_EmailNotAccepted_NotRecordedAndRetriedAtNextRun()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt);
        _email
            .SetupSequence(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(false, "provider_error", IsTransient: true))
            .ReturnsAsync(EmailSendResult.Sent());

        await RunAsync("2026-08-25T08:00:00Z");
        Assert.Empty(await PayloadsAsync(lease));

        await RunAsync("2026-08-26T08:00:00Z");
        Assert.Equal([$"t-7:{Deadline}"], await PayloadsAsync(lease));
    }

    // LT-07 (A7-08): Questura communication for an extra-EU tenant. Start 1/10/2026 = delivery by default, so the
    // 48 hours end at the latest on 3/10/2026.
    private const string QuesturaDeadline = "2026-10-03";

    [PostgresFact]
    public async Task ExecuteAsync_ExtraEuTenant_QuesturaThresholdsEachSentOnce()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt, extraEu: true);

        await RunAsync("2026-09-27T08:00:00Z"); // delivery in 4 days: nothing yet
        Assert.Empty(await QuesturaPayloadsAsync(lease));
        await RunAsync("2026-09-28T08:00:00Z"); // delivery in 3 days
        await RunAsync("2026-09-28T18:00:00Z"); // same day: no duplicate
        await RunAsync("2026-09-29T08:00:00Z"); // still "before delivery"
        await RunAsync("2026-10-01T08:00:00Z"); // delivery day
        await RunAsync("2026-10-02T08:00:00Z"); // still within the 48 hours
        await RunAsync("2026-10-03T08:00:00Z"); // the 48 hours end
        await RunAsync("2026-10-04T08:00:00Z"); // overdue
        await RunAsync("2026-10-05T08:00:00Z"); // overdue already sent

        Assert.Equal(
            [
                $"{Questura.BeforeDelivery}:{QuesturaDeadline}",
                $"{Questura.Delivery}:{QuesturaDeadline}",
                $"{Questura.Deadline}:{QuesturaDeadline}",
                $"{Questura.Overdue}:{QuesturaDeadline}",
            ],
            await QuesturaPayloadsAsync(lease));
        var questuraEmails = _sent.Where(e => e.Subject.Contains("Questura", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, questuraEmails.Count);
        Assert.Equal("Comunicazione alla Questura entro il 03/10/2026 — Casa Seveso", questuraEmails[0].Subject);
        Assert.Contains("data di inizio del contratto", questuraEmails[0].Body, StringComparison.Ordinal);
        Assert.Equal("Comunicazione alla Questura non dichiarata — Casa Seveso", questuraEmails[^1].Subject);
        Assert.All(questuraEmails, e => Assert.Equal("host@example.com", e.To));
    }

    [PostgresFact]
    public async Task ExecuteAsync_ExtraEuTenant_ReminderDoesNotDeclareTheCommunication()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt, extraEu: true);

        await RunAsync("2026-10-01T08:00:00Z");

        await using var db = _database!.CreateContext();
        var stored = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == lease);
        Assert.Null(stored.QuesturaCommunicationDate);
        Assert.False(await db.LeaseEvents.AnyAsync(e =>
            e.LeaseContractId == lease && e.EventType == LeaseEventType.QuesturaCommunicationMarkedDone));
    }

    [PostgresFact]
    public async Task ExecuteAsync_FirstRunAfterTheDeadline_SendsOnlyOverdue()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Signed, StartOctober, SignedAt, extraEu: true);

        await RunAsync("2026-10-20T08:00:00Z");

        Assert.Equal([$"{Questura.Overdue}:{QuesturaDeadline}"], await QuesturaPayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_RegisteredLeaseWithExtraEuTenant_StillRemindsTheQuestura()
    {
        // The RLI registration does not replace the Questura communication (fiscale.md L14).
        var lease = await SeedLeaseAsync(LeaseStatus.Registered, StartOctober, SignedAt, extraEu: true);

        await RunAsync("2026-10-01T08:00:00Z");

        Assert.Equal([$"{Questura.Delivery}:{QuesturaDeadline}"], await PayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_DeclaredDeliveryDate_ThresholdsCountFromIt()
    {
        var lease = await SeedLeaseAsync(
            LeaseStatus.Signed, StartOctober, SignedAt, extraEu: true,
            deliveryDate: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));

        await RunAsync("2026-10-01T08:00:00Z"); // start date, but the property is delivered on 10/10
        Assert.Empty(await QuesturaPayloadsAsync(lease));

        await RunAsync("2026-10-12T08:00:00Z");
        Assert.Equal([$"{Questura.Deadline}:2026-10-12"], await QuesturaPayloadsAsync(lease));
        Assert.DoesNotContain(
            "data di inizio del contratto",
            _sent.Single(e => e.Subject.Contains("Questura", StringComparison.Ordinal)).Body,
            StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ExecuteAsync_QuesturaCommunicationDeclared_NoReminder()
    {
        var lease = await SeedLeaseAsync(
            LeaseStatus.Signed, StartOctober, SignedAt, extraEu: true,
            questuraCommunicationDate: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        await RunAsync("2026-10-01T08:00:00Z");
        await RunAsync("2026-10-05T08:00:00Z");

        Assert.Empty(await QuesturaPayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_LeaseEnded_NoQuesturaReminder()
    {
        var lease = await SeedLeaseAsync(
            LeaseStatus.Registered, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), SignedAt, extraEu: true,
            endDate: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));

        await RunAsync("2026-10-01T08:00:00Z");

        Assert.Empty(await PayloadsAsync(lease));
    }

    [PostgresFact]
    public async Task ExecuteAsync_EuOnly_NoQuesturaReminder()
    {
        var lease = await SeedLeaseAsync(LeaseStatus.Registered, StartOctober, SignedAt);

        await RunAsync("2026-10-01T08:00:00Z");

        Assert.Empty(await PayloadsAsync(lease));
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(6, null)]
    [InlineData(5, Questura.BeforeDelivery)]
    [InlineData(3, Questura.BeforeDelivery)]
    [InlineData(2, Questura.Delivery)]
    [InlineData(1, Questura.Delivery)]
    [InlineData(0, Questura.Deadline)]
    [InlineData(-1, Questura.Overdue)]
    [InlineData(-90, Questura.Overdue)]
    public void QuesturaThresholds_Reached_MostUrgentThresholdOfTheDay(int daysToDeadline, string? expected)
    {
        Assert.Equal(expected, Questura.Reached(daysToDeadline));
    }

    [Theory]
    [InlineData(16, null)]
    [InlineData(15, RliDeadlineReminderJob.Thresholds.Days15)]
    [InlineData(8, RliDeadlineReminderJob.Thresholds.Days15)]
    [InlineData(7, RliDeadlineReminderJob.Thresholds.Days7)]
    [InlineData(2, RliDeadlineReminderJob.Thresholds.Days7)]
    [InlineData(1, RliDeadlineReminderJob.Thresholds.Days1)]
    [InlineData(0, RliDeadlineReminderJob.Thresholds.Days1)]
    [InlineData(-1, RliDeadlineReminderJob.Thresholds.Overdue)]
    [InlineData(-40, RliDeadlineReminderJob.Thresholds.Overdue)]
    public void Thresholds_Reached_MostUrgentThresholdOfTheDay(int daysRemaining, string? expected)
    {
        Assert.Equal(expected, RliDeadlineReminderJob.Thresholds.Reached(daysRemaining));
    }

    private async Task RunAsync(string utcNow)
    {
        await using var db = _database!.CreateContext();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture));
        var job = new RliDeadlineReminderJob(db, _email.Object, NullLogger<RliDeadlineReminderJob>.Instance, clock);
        await job.ExecuteAsync();
    }

    private async Task<List<string?>> PayloadsAsync(Guid leaseId)
    {
        await using var db = _database!.CreateContext();
        return await db.LeaseEvents
            .Where(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.DeadlineReminderSent)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Payload)
            .ToListAsync();
    }

    private async Task<List<string?>> QuesturaPayloadsAsync(Guid leaseId) =>
        (await PayloadsAsync(leaseId)).Where(p => p!.StartsWith("questura-", StringComparison.Ordinal)).ToList();

    private async Task<Guid> SeedLeaseAsync(
        LeaseStatus status,
        DateTime startDate,
        DateTime? signedAt,
        bool extraEu = false,
        DateTime? deliveryDate = null,
        DateTime? questuraCommunicationDate = null,
        DateTime? endDate = null)
    {
        var org = new OrgEntity { Name = "Org LT-04", Slug = $"lt04-{Guid.NewGuid():N}", DisplayName = "Org LT-04", IsActive = true };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|lt04",
            Name = "Casa Seveso",
            Address = "Via Roma 1",
            City = "Seveso",
            PostalCode = "20822",
            MaxGuests = 2,
            NightlyRate = 100m,
            IsActive = true,
        };
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = status,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = startDate,
            EndDate = endDate ?? startDate.AddYears(4),
            MonthlyRent = 800m,
            DataRetentionUntil = startDate.AddYears(10),
            PropertyDeliveryDate = deliveryDate,
            QuesturaCommunicationDate = questuraCommunicationDate,
            Parties =
            [
                new Party
                {
                    Role = PartyRole.Landlord,
                    FirstName = "Mario",
                    LastName = "Rossi",
                    FiscalCode = "RSSMRA80A01H501U",
                    Citizenship = "IT",
                    ContactEmail = "host@example.com",
                },
                new Party
                {
                    Role = PartyRole.Tenant,
                    FirstName = "John",
                    LastName = "Doe",
                    FiscalCode = "XXXXXX00A00A000X",
                    Citizenship = extraEu ? "US" : "IT",
                    ContactEmail = "tenant@example.com",
                    IsExtraEU = extraEu,
                },
            ],
        };
        if (signedAt is { } signed)
            lease.RecordStipula(signed);

        await using var db = _database!.CreateContext();
        db.AddRange(org, property, lease);
        await db.SaveChangesAsync();
        return lease.Id;
    }
}

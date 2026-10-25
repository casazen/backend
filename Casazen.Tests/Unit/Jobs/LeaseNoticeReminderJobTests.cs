using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Jobs;

/// <summary>
/// LR-11c-a on real PostgreSQL, the riskiest rule of the reminder of the last day to give notice of a lease: it goes to the landlord and
/// to nobody else, once per threshold (30 days, 7 days, the day itself) and last day, however many times the job runs, and never for a
/// lease whose notice was given, that was ended early, that is a draft or not registered yet, that has no notice at all (transitory) or
/// whose last day has passed. The job runs with a fake clock, as the RLI reminders are tested.
/// </summary>
public class LeaseNoticeReminderJobTests : IAsyncLifetime
{
    // A libero lease whose current period ends on 15/10/2027: the last day to give notice is six months earlier, 15/04/2027.
    private const string LastDay = "2027-04-15";
    private static readonly DateTime Start = new(2023, 10, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IEmailService> _email = new();
    private readonly List<(string To, string Subject, string Body)> _sent = [];
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync();

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
    public async Task ExecuteAsync_FromThirtyDaysBeforeTheLastDay_RemindsTheLandlordOncePerThresholdAndNobodyElse()
    {
        var due = await SeedLeaseAsync(LeaseStatus.Registered);
        var noticeGiven = await SeedLeaseAsync(LeaseStatus.Registered, noticeOn: new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var endedEarly = await SeedLeaseAsync(LeaseStatus.Registered, terminatedOn: End);
        var draft = await SeedLeaseAsync(LeaseStatus.Draft);
        var notRegistered = await SeedLeaseAsync(LeaseStatus.Signed);
        var transitory = await SeedLeaseAsync(LeaseStatus.Registered, LeaseContractType.Transitorio);

        await RunAsync("2027-03-10T08:30:00Z"); // 36 days before: too early
        Assert.Empty(_sent);

        await RunAsync("2027-03-16T08:30:00Z"); // 30 days before
        await RunAsync("2027-03-16T18:00:00Z"); // the same day again: no duplicate
        await RunAsync("2027-04-01T08:30:00Z"); // 14 days before: the 30-day reminder was sent already
        await RunAsync("2027-04-08T08:30:00Z"); // 7 days before
        await RunAsync("2027-04-15T08:30:00Z"); // the last day
        await RunAsync("2027-04-16T08:30:00Z"); // the day after: nothing is sent after it

        Assert.Equal([$"notice-t-30:{LastDay}", $"notice-t-7:{LastDay}", $"notice-t-0:{LastDay}"], await PayloadsAsync(due));

        // Three emails in all, to the landlord only: never to the tenant, no tenant name, no amount.
        Assert.Equal(3, _sent.Count);
        Assert.All(_sent, m => Assert.Equal("host@example.com", m.To));
        Assert.All(_sent, m => Assert.Contains("Casa Seveso", m.Subject, StringComparison.Ordinal));
        Assert.All(_sent, m => Assert.Contains("15/04/2027", m.Subject, StringComparison.Ordinal));
        Assert.All(_sent, m => Assert.DoesNotContain("Doe", m.Body, StringComparison.Ordinal));
        Assert.All(_sent, m => Assert.DoesNotContain("800", m.Body, StringComparison.Ordinal));
        Assert.Contains("Giorni rimanenti: <strong>30</strong>", _sent[0].Body, StringComparison.Ordinal);
        Assert.Contains("Giorni rimanenti: <strong>7</strong>", _sent[1].Body, StringComparison.Ordinal);
        Assert.Contains("<strong>oggi</strong>", _sent[2].Body, StringComparison.Ordinal);

        // The other leases got nothing: the notice is given, the lease ended early, it is a draft or not registered, it has no notice.
        foreach (var lease in new[] { noticeGiven, endedEarly, draft, notRegistered, transitory })
            Assert.Empty(await PayloadsAsync(lease));
    }

    private async Task RunAsync(string utcNow)
    {
        await using var db = _database!.CreateContext();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));
        var job = new LeaseNoticeReminderJob(db, _email.Object, NullLogger<LeaseNoticeReminderJob>.Instance, clock);
        await job.ExecuteAsync();
    }

    /// <summary>The reminders recorded for the lease, without the landlord suffix (<c>:{partyId}</c>), in the order they were sent.</summary>
    private async Task<List<string?>> PayloadsAsync(Guid leaseId)
    {
        await using var db = _database!.CreateContext();
        var payloads = await db.LeaseEvents
            .Where(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.DeadlineReminderSent)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Payload)
            .ToListAsync();
        return payloads.Select(p => p is null ? null : p[..p.LastIndexOf(':')]).ToList();
    }

    private async Task<Guid> SeedLeaseAsync(
        LeaseStatus status,
        LeaseContractType type = LeaseContractType.Libero,
        DateTime? noticeOn = null,
        DateTime? terminatedOn = null)
    {
        var org = new OrgEntity { Name = "Org LR-11c-a", Slug = $"lr11ca-{Guid.NewGuid():N}", DisplayName = "Org LR-11c-a", IsActive = true };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|lr11ca",
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
            ContractType = type,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = Start,
            EndDate = End,
            MonthlyRent = 800m,
            NoticeOn = noticeOn,
            NoticeFrom = noticeOn is null ? null : LeaseNoticeParty.Landlord,
            TerminatedOn = terminatedOn,
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
                    FiscalCode = "XXXXXX00A00A000T",
                    Citizenship = "IT",
                    ContactEmail = "tenant@example.com",
                },
            ],
        };

        await using var db = _database!.CreateContext();
        db.AddRange(org, property, lease);
        await db.SaveChangesAsync();
        return lease.Id;
    }
}

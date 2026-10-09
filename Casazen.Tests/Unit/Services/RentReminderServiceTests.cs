using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-01, B1: <see cref="RentBillingService.SendReminderAsync"/> on EF InMemory, without the web host: the interval is
/// configurable, the time recorded is the one the database keeps, what is logged names no one, the note is checked again at the
/// service. The cases of the whole request (authorization, JSON, the payment link and its public page) are
/// <c>LongRentRentReminderIntegrationTests</c>; the two reminders at the same moment are <c>LongRentAggregatesPostgresTests</c>.
/// </summary>
public class RentReminderServiceTests
{
    private const string TenantEmail = "giulia.verdi@tenants.example";
    private const string TenantName = "Giulia Verdi";
    private const string SecretNote = "il-mio-messaggio-riservato";

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public required AppDbContext Db { get; init; }

        public required RentBillingService Service { get; init; }

        public required RecordingEmailQueue Emails { get; init; }

        public required FakeTimeProvider Clock { get; init; }

        public required ListLogger Log { get; init; }

        public required Guid LeaseId { get; init; }

        public required Guid InstallmentId { get; init; }
    }

    private static async Task<Fixture> CreateAsync(string? intervalHours = null, bool connect = false)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = new OrgEntity
        {
            Name = "Casa Rossi Srl",
            Slug = $"casa-rossi-{Guid.NewGuid():N}",
            DisplayName = "Casa Rossi",
            ContactEmail = "info@casarossi.example",
            IsActive = true,
            StripeConnectedAccountId = connect ? "acct_unit" : null,
            ConnectChargesEnabled = connect,
        };
        var property = new Property { OrgId = org.Id, OwnerId = "auth0|owner", Name = "Bilocale Sparano", Address = "Via Sparano 1", City = "Bari" };
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Registered,
            StartDate = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2028, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
            Parties =
            [
                new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = TenantEmail },
            ],
        };
        var schedule = new RentSchedule { OrgId = org.Id, LeaseContractId = lease.Id, BillingDayOfMonth = 5, Amount = 900m, IsActive = true };
        var entry = new RentLedgerEntry
        {
            OrgId = org.Id,
            LeaseContractId = lease.Id,
            RentScheduleId = schedule.Id,
            PeriodStart = new DateOnly(2026, 10, 1),
            PeriodEnd = new DateOnly(2026, 10, 31),
            DueDate = new DateOnly(2026, 10, 5),
            AmountDue = 900m,
        };
        db.AddRange(org, property, lease, schedule, entry);
        await db.SaveChangesAsync();

        var values = new Dictionary<string, string?>();
        if (intervalHours is not null)
            values["RentBilling:ReminderIntervalHours"] = intervalHours;
        var clock = new FakeTimeProvider(Start);
        var emails = new RecordingEmailQueue();
        var log = new ListLogger();
        var service = new RentBillingService(
            db,
            Mock.Of<IStripeService>(),
            emails,
            EmailTestHelpers.Links(),
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            log,
            clock);
        return new Fixture { Db = db, Service = service, Emails = emails, Clock = clock, Log = log, LeaseId = lease.Id, InstallmentId = entry.Id };
    }

    [Fact]
    public async Task SendReminder_TheDefaultInterval_IsTwentyFourHours()
    {
        var f = await CreateAsync();
        var first = await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null);

        Assert.Equal(1, first.ReminderCount);
        Assert.Equal(Start.UtcDateTime.AddHours(24), first.NextReminderAllowedAt);

        f.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        var tooSoon = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null));
        Assert.Equal("rent_reminder_too_soon", tooSoon.Code);
        Assert.Equal(new object[] { 24 }, tooSoon.MessageArgs);

        f.Clock.Advance(TimeSpan.FromTicks(1));
        var second = await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null);
        Assert.Equal(2, second.ReminderCount);
        Assert.Equal(2, f.Emails.Snapshot().Count);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("12", 12)]
    [InlineData("72", 72)]
    public async Task SendReminder_TheIntervalIsConfigurable(string configured, int hours)
    {
        var f = await CreateAsync(configured);
        await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null);

        f.Clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromMinutes(1));
        var tooSoon = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null));
        Assert.Equal(new object[] { hours }, tooSoon.MessageArgs);

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var next = await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null);
        Assert.Equal(Start.UtcDateTime.AddHours(hours).AddHours(hours), next.NextReminderAllowedAt);
    }

    [Fact]
    public async Task SendReminder_TheTimeRecorded_IsTheMicrosecondTheDatabaseKeeps()
    {
        // A clock with a sub-microsecond fraction: PostgreSQL would keep the microseconds only, and the value compared back after the
        // round trip (to take a reminder back, to show "last reminder") must be the one that was written.
        var f = await CreateAsync();
        f.Clock.SetUtcNow(Start.AddTicks(7));

        var result = await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, null);

        Assert.Equal(0, result.SentAt.Ticks % 10);
        Assert.Equal(Start.UtcDateTime, result.SentAt);
        Assert.Equal(DateTimeKind.Utc, result.SentAt.Kind);
        Assert.Equal(result.SentAt, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync()).LastReminderAt);
    }

    [Fact]
    public async Task SendReminder_LogsTheIdsAndCountsOnly_NoNameAddressOrNote()
    {
        var f = await CreateAsync(connect: true);

        await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, SecretNote);

        var logged = string.Join('\n', f.Log.Messages);
        Assert.Contains(f.InstallmentId.ToString(), logged, StringComparison.Ordinal);
        Assert.DoesNotContain(TenantEmail, logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Giulia", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretNote, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Casa Rossi", logged, StringComparison.Ordinal);
        // Nor the link, nor the token behind it.
        Assert.DoesNotContain("/rent/pay/", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("token", logged, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendReminder_AFailureToQueue_LogsNothingAboutTheTenantEither()
    {
        var f = await CreateAsync();
        f.Emails.Refuse = true;

        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, SecretNote));

        var logged = string.Join('\n', f.Log.Messages);
        Assert.DoesNotContain(TenantEmail, logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretNote, logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_TheNote_IsTrimmed_AndLongerThanTheLimitIsRefusedAgainAtTheService()
    {
        var f = await CreateAsync();

        await f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, $"  {SecretNote}  \n");
        Assert.Contains($"<br />{SecretNote}</p>", f.Emails.Snapshot().Single().Content.HtmlBody, StringComparison.Ordinal);

        f.Clock.Advance(TimeSpan.FromDays(2));
        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(
            () => f.Service.SendReminderAsync(f.LeaseId, f.InstallmentId, new string('x', RentCharges.MaxReminderNoteLength + 1)));
        Assert.Equal("rent_reminder_note_invalid", tooLong.Code);
        Assert.Equal(1, (await f.Db.RentLedgerEntries.AsNoTracking().SingleAsync()).ReminderCount);
    }

    [Fact]
    public async Task SendReminder_AnInstallmentOfAnotherLease_IsNotFound_NothingIsSent()
    {
        var f = await CreateAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => f.Service.SendReminderAsync(f.LeaseId, Guid.NewGuid(), null));

        Assert.Equal("rent_installment_not_found", ex.Code);
        Assert.Empty(f.Emails.Snapshot());
    }

    [Fact]
    public async Task SendReminder_ALeaseThatIsNotThere_IsNotFound()
    {
        var f = await CreateAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => f.Service.SendReminderAsync(Guid.NewGuid(), f.InstallmentId, null));

        Assert.Equal("lease_not_found", ex.Code);
    }

    private sealed class ListLogger : ILogger<RentBillingService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Messages.Add(exception.ToString());
        }
    }
}

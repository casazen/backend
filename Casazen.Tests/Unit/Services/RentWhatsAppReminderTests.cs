using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
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
/// WhatsApp rent reminder: a tenant with a phone and no email is messaged, and Meta is called only after the reminder
/// is recorded, so a later failure does not send the same message again.
/// </summary>
public class RentWhatsAppReminderTests
{
    private const string Phone = "+393331234567";
    private const string OtherPhone = "+393339998888";

    private static readonly DateTimeOffset Start = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public required DbContextOptions<AppDbContext> Options { get; init; }

        public required RentBillingService Service { get; init; }

        public required ScriptedSender Sender { get; init; }

        public required FakeTimeProvider Clock { get; init; }

        public required ListLogger Log { get; init; }

        public required Guid LeaseId { get; init; }

        public required Guid InstallmentId { get; init; }

        public async Task<RentLedgerEntry> StoredAsync()
        {
            await using var db = new AppDbContext(Options);
            return await db.RentLedgerEntries.AsNoTracking().SingleAsync();
        }
    }

    private sealed class ScriptedSender : IWhatsAppSender
    {
        public List<string> Phones { get; } = [];

        public Func<int, Task>? DuringSend { get; set; }

        public Func<int, Exception?>? Fail { get; set; }

        public async Task<WhatsAppSendResult> SendTemplateAsync(
            string phone, string template, string language, IReadOnlyList<WhatsAppTemplateParameter> body,
            CancellationToken cancellationToken = default)
        {
            var index = Phones.Count;
            Phones.Add(phone);
            if (DuringSend is not null)
                await DuringSend(index);
            if (Fail?.Invoke(index) is { } error)
                throw error;
            return new WhatsAppSendResult(true, "wamid." + index);
        }
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
        }
    }

    private static async Task<Fixture> CreateAsync(params Party[] tenants)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var org = new OrgEntity
        {
            Name = "Casa Rossi Srl",
            Slug = $"casa-rossi-{Guid.NewGuid():N}",
            DisplayName = "Casa Rossi",
            ContactEmail = "info@casarossi.example",
            IsActive = true,
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
            Parties = tenants.Length == 0 ? [Tenant(Phone, email: "")] : tenants.ToList(),
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

        var sender = new ScriptedSender();
        var clock = new FakeTimeProvider(Start);
        var log = new ListLogger();
        var service = new RentBillingService(
            new AppDbContext(options),
            Mock.Of<IStripeService>(),
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            new ConfigurationBuilder().Build(),
            log,
            clock,
            sender);
        return new Fixture
        {
            Options = options,
            Service = service,
            Sender = sender,
            Clock = clock,
            Log = log,
            LeaseId = lease.Id,
            InstallmentId = entry.Id,
        };
    }

    private static Party Tenant(string? phone, string email = "", int position = 0, bool anonymized = false) => new()
    {
        Role = PartyRole.Tenant,
        Position = position,
        FirstName = "Giulia",
        LastName = "Verdi",
        FiscalCode = "VRDGLI85B02F205" + (char)('A' + position),
        Citizenship = "IT",
        ContactEmail = email,
        ContactPhone = phone,
        AnonymizedAt = anonymized ? Start.UtcDateTime : null,
    };

    [Fact]
    public async Task SendWhatsApp_ATenantWithAPhoneAndNoEmail_IsMessagedAndRecorded()
    {
        var f = await CreateAsync();
        DateTime? recordedDuringSend = null;
        f.Sender.DuringSend = async _ =>
        {
            recordedDuringSend = (await f.StoredAsync()).LastReminderAt;
        };

        var result = await f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null);

        Assert.Equal([Phone], f.Sender.Phones);
        Assert.Equal(1, result.RecipientCount);
        Assert.Equal(Start.UtcDateTime, recordedDuringSend);
        Assert.Equal(Start.UtcDateTime, (await f.StoredAsync()).LastReminderAt);
        Assert.DoesNotContain(Phone, string.Join('\n', f.Log.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendWhatsApp_AnEmailWithoutAPhone_SendsNothingAndRecordsNothing()
    {
        var f = await CreateAsync(Tenant(phone: null, email: "giulia.verdi@tenants.example"));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null));

        Assert.Equal("rent_reminder_not_sent", ex.Code);
        Assert.Empty(f.Sender.Phones);
        Assert.Null((await f.StoredAsync()).LastReminderAt);
        Assert.Equal(0, (await f.StoredAsync()).ReminderCount);
    }

    [Fact]
    public async Task SendWhatsApp_AnAnonymizedPhone_IsSkipped()
    {
        var f = await CreateAsync(Tenant(Phone, anonymized: true));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null));

        Assert.Equal("rent_reminder_not_sent", ex.Code);
        Assert.Empty(f.Sender.Phones);
    }

    [Fact]
    public async Task SendWhatsApp_TheSameNumberTwice_IsOneMessage()
    {
        var f = await CreateAsync(Tenant(Phone), Tenant(Phone, position: 1));

        var result = await f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null);

        Assert.Equal([Phone], f.Sender.Phones);
        Assert.Equal(1, result.RecipientCount);
    }

    [Fact]
    public async Task SendWhatsApp_ALaterRecipientThrowing_KeepsTheAcceptedMessageAndDoesNotSendItAgain()
    {
        var f = await CreateAsync(Tenant(Phone), Tenant(OtherPhone, position: 1));
        f.Sender.Fail = index => index == 1 ? new TaskCanceledException("timeout for " + OtherPhone) : null;

        var result = await f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, "nota privata");

        Assert.Equal([Phone, OtherPhone], f.Sender.Phones);
        Assert.Equal(1, result.RecipientCount);
        Assert.Equal(1, (await f.StoredAsync()).ReminderCount);
        var logged = string.Join('\n', f.Log.Messages);
        Assert.Contains("TaskCanceledException", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(Phone, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherPhone, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("nota privata", logged, StringComparison.Ordinal);

        f.Clock.Advance(TimeSpan.FromHours(1));
        var tooSoon = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null));
        Assert.Equal("rent_reminder_too_soon", tooSoon.Code);
        Assert.Equal([Phone, OtherPhone], f.Sender.Phones);
    }

    [Fact]
    public async Task SendWhatsApp_EverySendFailing_TakesTheReminderBack_SoItCanBeTriedAgain()
    {
        var f = await CreateAsync();
        f.Sender.Fail = _ => new HttpRequestException("timeout for " + Phone);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null));

        Assert.Equal("rent_reminder_not_sent", ex.Code);
        Assert.Null((await f.StoredAsync()).LastReminderAt);
        Assert.Equal(0, (await f.StoredAsync()).ReminderCount);
        Assert.DoesNotContain(Phone, string.Join('\n', f.Log.Messages), StringComparison.Ordinal);

        f.Sender.Fail = null;
        var retried = await f.Service.SendWhatsAppReminderAsync(f.LeaseId, f.InstallmentId, null);
        Assert.Equal(1, retried.RecipientCount);
        Assert.Equal([Phone, Phone], f.Sender.Phones);
    }
}

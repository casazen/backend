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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Linking a bank is optional. The id stored is the Financial Connections account id. A due stays unpaid until Stripe
/// confirms, and the offline declaration still marks it paid when no debit is in flight.
/// </summary>
public class BankLinkServiceTests : IDisposable
{
    private const string Token = "rent-link-token";
    private const string AccountId = "fca_1Nlinked";
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeGateway _gateway = new();
    private readonly BankLinkService _service;
    private readonly RentBillingService _rent;

    public BankLinkServiceTests()
    {
        var billing = new Mock<IStripeBillingService>();
        billing.Setup(s => s.EnsureCustomerAsync(It.IsAny<OrgEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity org, CancellationToken _) => org.StripeCustomerId ?? "cus_org");
        _service = new BankLinkService(
            _db,
            billing.Object,
            _gateway,
            Mock.Of<IStripeService>(),
            new RentBillingService(
                _db,
                Mock.Of<IStripeService>(),
                new RecordingEmailQueue(),
                EmailTestHelpers.Links(),
                new ConfigurationBuilder().Build(),
                NullLogger<RentBillingService>.Instance),
            NullLogger<BankLinkService>.Instance);
        _gateway.RowIsSaved = () => _db.BankLinkPayments.Any(p => p.StripePaymentIntentId == "pi_linked");
        _rent = new RentBillingService(
            _db,
            Mock.Of<IStripeService>(),
            new RecordingEmailQueue(),
            EmailTestHelpers.Links(),
            new ConfigurationBuilder().Build(),
            NullLogger<RentBillingService>.Instance);
    }

    [Fact]
    public async Task Attach_StoresTheAccountId_NotABankNumber()
    {
        var seeded = await SeedAsync();
        seeded.Org.StripeCustomerId = "cus_org";
        await _db.SaveChangesAsync();

        var linked = await _service.AttachOrgAccountAsync(seeded.Org.Id, AccountId);

        Assert.Equal(AccountId, linked.AccountId);
        Assert.Equal(AccountId, (await _db.Orgs.SingleAsync()).FinancialConnectionsAccountId);
        await Assert.ThrowsAsync<DomainRuleException>(() => _service.AttachOrgAccountAsync(seeded.Org.Id, "IT60X0542811101000000123456"));
        Assert.Equal(AccountId, (await _db.Orgs.SingleAsync()).FinancialConnectionsAccountId);
    }

    [Fact]
    public async Task StartPayment_DoesNotMarkTheDuePaid_UntilStripeConfirms()
    {
        var seeded = await SeedAsync(linked: true);
        var started = await _service.StartOrgPaymentAsync(
            seeded.Org.Id,
            new StartBankLinkPayment(90_000, "eur", null, null, "idem-key-1"),
            new BankLinkCaller("203.0.113.5", "CasaZen"));

        Assert.False(started.DueSettled);
        Assert.Equal(BankLinkCharges.SepaDebit, started.PaymentMethodType);
        var row = await _db.BankLinkPayments.SingleAsync();
        Assert.Equal(BankLinkPaymentStatus.Processing, row.Status);
        Assert.Equal(AccountId, row.FinancialConnectionsAccountId);

        await _service.ApplyStripeEventAsync(new BankLinkStripeEvent(started.PaymentIntentId, "payment_intent.processing", 90_000, null));
        Assert.Equal(BankLinkPaymentStatus.Processing, (await _db.BankLinkPayments.SingleAsync()).Status);

        await _service.ApplyStripeEventAsync(new BankLinkStripeEvent(started.PaymentIntentId, "payment_intent.succeeded", 90_000, null));
        Assert.Equal(BankLinkPaymentStatus.Succeeded, (await _db.BankLinkPayments.SingleAsync()).Status);
    }

    [Fact]
    public async Task AdHocDebit_WithoutAnIdempotencyKey_DoesNotStartAPayment()
    {
        var seeded = await SeedAsync(linked: true);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.StartOrgPaymentAsync(
            seeded.Org.Id,
            new StartBankLinkPayment(90_000, "eur", null, null, null),
            new BankLinkCaller("203.0.113.5", "CasaZen")));

        Assert.Equal(BankLinkErrorCodes.IdempotencyKeyInvalid, ex.Code);
        Assert.Empty(_gateway.CreatedKeys);
        Assert.Empty(_db.BankLinkPayments);
    }

    [Fact]
    public async Task EurInstallment_StartsSepa_AndStaysUnpaidUntilStripeConfirms()
    {
        var seeded = await SeedAsync(linked: true);
        var started = await _service.StartOrgPaymentAsync(
            seeded.Org.Id,
            new StartBankLinkPayment(null, null, seeded.Lease.Id, seeded.Entry.Id, null),
            new BankLinkCaller("203.0.113.5", "CasaZen"));

        Assert.False(started.DueSettled);
        Assert.Equal(BankLinkCharges.SepaDebit, started.PaymentMethodType);
        var entry = await _db.RentLedgerEntries.SingleAsync();
        Assert.Equal(RentLedgerStatus.Processing, entry.Status);
        Assert.NotEqual(RentLedgerStatus.Paid, entry.Status);
        var row = await _db.BankLinkPayments.SingleAsync();
        Assert.Equal("eur", row.Currency);
        Assert.Equal(AccountId, row.FinancialConnectionsAccountId);
        Assert.DoesNotContain("IT60", row.FinancialConnectionsAccountId);

        await _service.ApplyStripeEventAsync(new BankLinkStripeEvent(started.PaymentIntentId, "payment_intent.succeeded", 90_000, null));
        var paid = await _db.RentLedgerEntries.SingleAsync();
        Assert.Equal(RentLedgerStatus.Paid, paid.Status);
        Assert.Equal(RentPaymentChannel.Stripe, paid.PaidVia);
    }

    [Fact]
    public async Task RetryOfADue_ConfirmsTheSavedPayment_AndDoesNotCreateASecondDebit()
    {
        var seeded = await SeedAsync(linked: true);
        var caller = new BankLinkCaller("203.0.113.5", "CasaZen");
        await _service.StartOrgPaymentAsync(
            seeded.Org.Id,
            new StartBankLinkPayment(null, null, seeded.Lease.Id, seeded.Entry.Id, null),
            caller);
        await _service.StartOrgPaymentAsync(
            seeded.Org.Id,
            new StartBankLinkPayment(null, null, seeded.Lease.Id, seeded.Entry.Id, null),
            caller);

        var key = Assert.Single(_gateway.CreatedKeys);
        Assert.Equal($"bank-link:{seeded.Entry.Id:N}", key);
        Assert.Equal(1, await _db.BankLinkPayments.CountAsync());
    }

    [Fact]
    public async Task ProcessingEvent_DoesNotReopenAFailedPayment()
    {
        var seeded = await SeedAsync(linked: true);
        _db.BankLinkPayments.Add(new BankLinkPayment
        {
            OrgId = seeded.Org.Id,
            LeaseContractId = seeded.Lease.Id,
            RentLedgerEntryId = seeded.Entry.Id,
            Payer = BankLinkPayer.Landlord,
            FinancialConnectionsAccountId = AccountId,
            StripePaymentIntentId = "pi_failed",
            PaymentMethodType = BankLinkCharges.SepaDebit,
            AmountCents = 90_000,
            Currency = "eur",
            Status = BankLinkPaymentStatus.Failed,
        });
        seeded.Entry.StripePaymentIntentId = "pi_failed";
        seeded.Entry.Status = RentLedgerStatus.Failed;
        await _db.SaveChangesAsync();

        await _service.ApplyStripeEventAsync(new BankLinkStripeEvent("pi_failed", "payment_intent.processing", 90_000, null));
        await _service.ApplyStripeEventAsync(new BankLinkStripeEvent("pi_failed", "payment_intent.succeeded", 90_000, null));

        Assert.Equal(BankLinkPaymentStatus.Failed, (await _db.BankLinkPayments.SingleAsync()).Status);
        Assert.Equal(RentLedgerStatus.Failed, (await _db.RentLedgerEntries.SingleAsync()).Status);
    }

    [Fact]
    public async Task CanceledPayment_DoesNotStayProcessing_AfterTheIntentIdIsCleared()
    {
        var seeded = await SeedAsync(linked: true);
        seeded.Entry.Status = RentLedgerStatus.Processing;
        seeded.Entry.StripePaymentIntentId = "pi_cancel";
        _db.BankLinkPayments.Add(new BankLinkPayment
        {
            OrgId = seeded.Org.Id,
            LeaseContractId = seeded.Lease.Id,
            RentLedgerEntryId = seeded.Entry.Id,
            Payer = BankLinkPayer.Landlord,
            FinancialConnectionsAccountId = AccountId,
            StripePaymentIntentId = "pi_cancel",
            PaymentMethodType = BankLinkCharges.SepaDebit,
            AmountCents = 90_000,
            Currency = "eur",
            Status = BankLinkPaymentStatus.Processing,
        });
        await _db.SaveChangesAsync();

        await _rent.ApplyLinkedBankPaymentAsync(new RentPaymentIntentEvent(
            "pi_cancel", "payment_intent.canceled", null, seeded.Entry.Id, 90_000, null));

        var entry = await _db.RentLedgerEntries.SingleAsync();
        Assert.Null(entry.StripePaymentIntentId);
        Assert.NotEqual(RentLedgerStatus.Processing, entry.Status);
        Assert.Equal(BankLinkPaymentStatus.Canceled, (await _db.BankLinkPayments.SingleAsync()).Status);
    }

    [Fact]
    public async Task WithoutALinkedAccount_TheManualDeclarationStillPaysTheDue()
    {
        var seeded = await SeedAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() => _service.StartOrgPaymentAsync(
            seeded.Org.Id,
            new StartBankLinkPayment(null, null, seeded.Lease.Id, seeded.Entry.Id, null),
            new BankLinkCaller(null, null)));
        Assert.Equal(RentLedgerStatus.Scheduled, (await _db.RentLedgerEntries.SingleAsync()).Status);

        await _rent.MarkPaidOfflineAsync(
            seeded.Lease.Id,
            seeded.Entry.Id,
            new MarkRentPaidOfflineRequest(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "bonifico TEST"),
            "auth0|owner");

        var paid = await _db.RentLedgerEntries.SingleAsync();
        Assert.Equal(RentLedgerStatus.Paid, paid.Status);
        Assert.Equal(RentPaymentChannel.Offline, paid.PaidVia);
        Assert.Equal("bonifico TEST", paid.OfflinePaymentNote);
    }

    [Fact]
    public async Task TenantLink_IsScopedToTheLease_AndEurDueWaitsForManualOrStripeConfirmation()
    {
        var seeded = await SeedAsync();
        seeded.Entry.PaymentTokenHash = CheckoutOutcomes.HashToken(Token);
        await _db.SaveChangesAsync();
        _gateway.Holder = "cus_lease";

        await _service.CreateLeaseSessionAsync(seeded.Entry.Id, Token);
        await _service.AttachLeaseAccountAsync(seeded.Entry.Id, Token, AccountId);
        Assert.Equal(AccountId, (await _db.LeaseContracts.SingleAsync()).FinancialConnectionsAccountId);
        Assert.Null((await _db.Orgs.SingleAsync()).FinancialConnectionsAccountId);

        var started = await _service.StartLeasePaymentAsync(seeded.Entry.Id, Token, new BankLinkCaller("203.0.113.5", "CasaZen"));
        Assert.Equal(BankLinkCharges.SepaDebit, started.PaymentMethodType);
        Assert.Equal(RentLedgerStatus.Processing, (await _db.RentLedgerEntries.SingleAsync()).Status);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.AttachLeaseAccountAsync(seeded.Entry.Id, "wrong-token", AccountId));

        seeded.Entry.Status = RentLedgerStatus.Processing;
        seeded.Entry.StripePaymentIntentId = "pi_confirmed";
        _db.BankLinkPayments.Add(new BankLinkPayment
        {
            OrgId = seeded.Org.Id,
            LeaseContractId = seeded.Lease.Id,
            RentLedgerEntryId = seeded.Entry.Id,
            Payer = BankLinkPayer.Tenant,
            FinancialConnectionsAccountId = AccountId,
            StripePaymentIntentId = "pi_confirmed",
            PaymentMethodType = BankLinkCharges.SepaDebit,
            AmountCents = 90_000,
            Currency = "eur",
        });
        await _db.SaveChangesAsync();

        await _service.ApplyStripeEventAsync(new BankLinkStripeEvent("pi_confirmed", "payment_intent.succeeded", 90_000, null));
        var paid = await _db.RentLedgerEntries.SingleAsync();
        Assert.Equal(RentLedgerStatus.Paid, paid.Status);
        Assert.Equal(RentPaymentChannel.Stripe, paid.PaidVia);
        Assert.Equal(BankLinkPaymentStatus.Succeeded, (await _db.BankLinkPayments.SingleAsync(p => p.StripePaymentIntentId == "pi_confirmed")).Status);
    }

    private async Task<Seed> SeedAsync(bool linked = false)
    {
        var org = new OrgEntity
        {
            Name = "Casa Rossi Srl",
            Slug = $"casa-rossi-{Guid.NewGuid():N}",
            DisplayName = "Casa Rossi",
            ContactEmail = "info@casarossi.example",
            IsActive = true,
            StripeCustomerId = "cus_org",
            FinancialConnectionsAccountId = linked ? AccountId : null,
        };
        var property = new Property { OrgId = org.Id, OwnerId = "auth0|owner", Name = "Bilocale", Address = "Via 1", City = "Bari" };
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Registered,
            StartDate = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2028, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
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
        _db.AddRange(org, property, lease, schedule, entry);
        await _db.SaveChangesAsync();
        return new Seed(org, lease, entry);
    }

    public void Dispose() => _db.Dispose();

    private sealed record Seed(OrgEntity Org, LeaseContract Lease, RentLedgerEntry Entry);

    private sealed class FakeGateway : IStripeBankLinkGateway
    {
        public List<string> Supported { get; set; } = [BankLinkCharges.SepaDebit];

        public string Holder { get; set; } = "cus_org";

        public Task<string> EnsureCustomerAsync(
            string? existingCustomerId, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(existingCustomerId ?? "cus_lease");

        public Task<BankLinkSession> CreateSessionAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BankLinkSession("fcsess_1", "secret"));

        public Task<LinkedFinancialAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinkedFinancialAccount(accountId, Holder, "Test Bank", "6789", "Checking", "active", Supported));

        public List<string> CreatedKeys { get; } = [];

        public Func<bool>? RowIsSaved { get; set; }

        public Task<StartedBankDebit> CreateDebitAsync(BankDebitCommand command, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(command.Currency, BankLinkCharges.Currency, StringComparison.Ordinal))
                throw new DomainRuleException(BankLinkErrorCodes.CurrencyInvalid, "BankLinkCurrencyInvalid");
            CreatedKeys.Add(command.IdempotencyKey);
            return Task.FromResult(new StartedBankDebit("pi_linked", "secret", "requires_confirmation", BankLinkCharges.SepaDebit, "pm_sepa"));
        }

        public Task<StartedBankDebit> ConfirmDebitAsync(
            string paymentIntentId, string idempotencyKey, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
        {
            if (RowIsSaved is not null && !RowIsSaved())
                throw new InvalidOperationException("PaymentIntent confirmed before the bank-link row was saved.");
            return Task.FromResult(new StartedBankDebit(paymentIntentId, "secret", "processing", BankLinkCharges.SepaDebit, "pm_sepa"));
        }
    }
}

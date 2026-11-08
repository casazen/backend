using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The tenant opens a request, another org cannot see it, a rejection cannot be paid, and an approval is paid
/// once by the euro debit: a second pay does not start another PaymentIntent.
/// </summary>
public class LeaseMaintenanceServiceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeGateway _gateway = new();
    private readonly LeaseMaintenanceService _service;

    public LeaseMaintenanceServiceTests()
    {
        _service = new LeaseMaintenanceService(_db, _gateway, new MemoryStorage(), NullLogger<LeaseMaintenanceService>.Instance);
        _gateway.Saved = id => _db.BankLinkPayments.Any(p => p.StripePaymentIntentId == id);
    }

    [Theory]
    [InlineData("\"Normal\"", LeaseMaintenanceUrgency.Normal)]
    [InlineData("\"High\"", LeaseMaintenanceUrgency.High)]
    [InlineData("\"Emergency\"", LeaseMaintenanceUrgency.Emergency)]
    public void Urgency_JsonStringEnumConverter_AcceptsPascalCase(string json, LeaseMaintenanceUrgency expected)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.Equal(expected, JsonSerializer.Deserialize<LeaseMaintenanceUrgency>(json, options));
    }

    [Theory]
    [InlineData("\"Approved\"", LeaseMaintenanceDecision.Approved)]
    [InlineData("\"Rejected\"", LeaseMaintenanceDecision.Rejected)]
    public void Decision_JsonStringEnumConverter_AcceptsPascalCase(string json, LeaseMaintenanceDecision expected)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.Equal(expected, JsonSerializer.Deserialize<LeaseMaintenanceDecision>(json, options));
    }

    [Fact]
    public async Task TenantCreates_RequestStartsWaiting_WithThePhoto()
    {
        var world = await SeedAsync();
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(withPhoto: true), default);

        Assert.Equal(LeaseMaintenanceStatus.InAttesa, created.Status);
        Assert.False(created.Paid);
        Assert.Equal(LeaseMaintenanceUrgency.Normal, created.Urgency);
        Assert.Equal("Rubinetto", created.Title);
        var photo = Assert.Single(created.Photos);
        Assert.Equal("image/jpeg", photo.ContentType);
        Assert.Equal(LeaseEventType.MaintenanceRequested, Assert.Single(_db.LeaseEvents).EventType);
    }

    [Fact]
    public async Task ViewerOfAnotherOrg_CannotSeeTheRequest()
    {
        var world = await SeedAsync();
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(), default);

        var other = world.Tenant with { OrgId = world.OtherOrg.Id, CanReadLease = true, CanManageLease = true };
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ListAsync(other, world.Lease.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.DecideAsync(other, world.Lease.Id, created.Id, new LeaseMaintenanceDecisionCommand(LeaseMaintenanceDecision.Approved, null), default));
        Assert.Null(await _service.FindLeaseAsync(world.OtherOrg.Id, world.Lease.Id, default));
    }

    [Fact]
    public async Task Reject_BlocksPayment_AndDoesNotStartADebit()
    {
        var world = await SeedAsync();
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(), default);
        var rejected = await _service.DecideAsync(
            world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenanceDecisionCommand(LeaseMaintenanceDecision.Rejected, "Non urgente"), default);

        Assert.Equal(LeaseMaintenanceStatus.Rifiutata, rejected.Status);
        Assert.False(rejected.Paid);
        Assert.Equal("Non urgente", rejected.RejectionReason);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            _service.PayAsync(world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(4_500), Caller(), default));
        Assert.Equal("maintenance_not_payable", ex.Code);
        Assert.Empty(_gateway.CreatedKeys);
    }

    [Fact]
    public async Task WaitingRequest_CannotBePaid()
    {
        var world = await SeedAsync();
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(), default);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            _service.PayAsync(world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(4_500), Caller(), default));
        Assert.Equal("maintenance_not_payable", ex.Code);
        Assert.Empty(_gateway.CreatedKeys);
    }

    [Fact]
    public async Task Approve_DoesNotMarkPaid_ThenOneDebit_AndASecondPayDoesNotChargeAgain()
    {
        var world = await SeedAsync(linked: true);
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(), default);
        var approved = await _service.DecideAsync(
            world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenanceDecisionCommand(LeaseMaintenanceDecision.Approved, null), default);

        Assert.Equal(LeaseMaintenanceStatus.Approvata, approved.Status);
        Assert.False(approved.Paid);
        Assert.Null(approved.AmountCents);

        var first = await _service.PayAsync(
            world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(4_500), Caller(), default);
        Assert.False(first.Paid);
        Assert.Equal(LeaseMaintenanceStatus.Approvata, first.Status);
        Assert.Equal(4_500, first.AmountCents);
        Assert.Equal("eur", first.Currency);
        Assert.Equal(BankLinkCharges.SepaDebit, (await _db.BankLinkPayments.SingleAsync()).PaymentMethodType);

        await _service.MarkPaidFromBankAsync(world.Org.Id, created.Id, 4_500);
        var paid = await _service.ListAsync(world.Tenant, world.Lease.Id, default);
        var row = Assert.Single(paid.Items);
        Assert.Equal(LeaseMaintenanceStatus.Pagata, row.Status);
        Assert.True(row.Paid);

        var second = await _service.PayAsync(
            world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(9_900), Caller(), default);
        Assert.True(second.Paid);
        Assert.Equal(4_500, second.AmountCents);
        Assert.Equal(first.PaymentIntentId, second.PaymentIntentId);
        Assert.Single(_gateway.CreatedKeys);
        Assert.Equal(1, await _db.BankLinkPayments.CountAsync());
    }

    [Fact]
    public async Task SecondPayWhileTheDebitIsInFlight_DoesNotStartAnother()
    {
        var world = await SeedAsync(linked: true);
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(), default);
        await _service.DecideAsync(
            world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenanceDecisionCommand(LeaseMaintenanceDecision.Approved, null), default);

        await _service.PayAsync(world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(4_500), Caller(), default);
        var again = await _service.PayAsync(
            world.Landlord, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(4_500), Caller(), default);

        Assert.False(again.Paid);
        Assert.Single(_gateway.CreatedKeys);
        Assert.Equal(1, await _db.BankLinkPayments.CountAsync());
    }

    [Fact]
    public async Task Tenant_CannotApproveOrPay()
    {
        var world = await SeedAsync(linked: true);
        var created = await _service.CreateAsync(world.Tenant, world.Lease.Id, Command(), default);

        var decide = await Assert.ThrowsAsync<DomainForbiddenException>(() =>
            _service.DecideAsync(world.Tenant, world.Lease.Id, created.Id, new LeaseMaintenanceDecisionCommand(LeaseMaintenanceDecision.Approved, null), default));
        var pay = await Assert.ThrowsAsync<DomainForbiddenException>(() =>
            _service.PayAsync(world.Tenant, world.Lease.Id, created.Id, new LeaseMaintenancePaymentCommand(4_500), Caller(), default));

        Assert.Equal("maintenance_forbidden", decide.Code);
        Assert.Equal("maintenance_forbidden", pay.Code);
        Assert.Empty(_gateway.CreatedKeys);
    }

    public void Dispose() => _db.Dispose();

    private static CreateLeaseMaintenanceCommand Command(bool withPhoto = false) =>
        new(
            "Rubinetto",
            "Perde nel bagno.",
            LeaseMaintenanceUrgency.Normal,
            withPhoto ? [new LeaseMaintenancePhotoContent("rubinetto.jpg", TinyJpeg())] : []);

    private static BankLinkCaller Caller() => new("203.0.113.8", "CasaZen");

    private static byte[] TinyJpeg() =>
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00,
    ];

    private async Task<World> SeedAsync(bool linked = false)
    {
        var org = new OrgEntity
        {
            Name = "Casa Rossi Srl",
            Slug = $"casa-rossi-{Guid.NewGuid():N}",
            DisplayName = "Casa Rossi",
            ContactEmail = "info@casarossi.example",
            IsActive = true,
            StripeCustomerId = linked ? "cus_org" : null,
            FinancialConnectionsAccountId = linked ? "fca_1Nlinked" : null,
        };
        var other = new OrgEntity
        {
            Name = "Altra Casa",
            Slug = $"altra-{Guid.NewGuid():N}",
            DisplayName = "Altra",
            ContactEmail = "altra@example.com",
            IsActive = true,
        };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|landlord",
            Name = "Bilocale",
            Address = "Via 1",
            City = "Bari",
        };
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Registered,
            StartDate = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2028, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
        };
        var tenant = new Party
        {
            LeaseContractId = lease.Id,
            Role = PartyRole.Tenant,
            FirstName = "Giulia",
            LastName = "Verdi",
            FiscalCode = "VRDGLI85B02F205A",
            Citizenship = "IT",
            ContactEmail = "giulia.verdi@example.com",
        };
        _db.AddRange(org, other, property, lease, tenant);
        await _db.SaveChangesAsync();
        return new World(
            org,
            other,
            lease,
            new LeaseMaintenanceCaller(org.Id, "auth0|tenant", "giulia.verdi@example.com", true, false, false),
            new LeaseMaintenanceCaller(org.Id, "auth0|landlord", "mario.rossi@example.com", true, true, true));
    }

    private sealed record World(OrgEntity Org, OrgEntity OtherOrg, LeaseContract Lease, LeaseMaintenanceCaller Tenant, LeaseMaintenanceCaller Landlord);

    private sealed class MemoryStorage : IFileStorage
    {
        public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public string GetPublicUrl(string key) => "https://files.example/" + key;

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }

    private sealed class FakeGateway : IStripeBankLinkGateway
    {
        public List<string> CreatedKeys { get; } = [];

        public Func<string, bool>? Saved { get; set; }

        public Task<string> EnsureCustomerAsync(
            string? existingCustomerId, string idempotencyKey, IReadOnlyDictionary<string, string> metadata, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(existingCustomerId ?? "cus_lease");

        public Task<BankLinkSession> CreateSessionAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BankLinkSession("fcsess_1", "secret"));

        public Task<LinkedFinancialAccount> GetAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinkedFinancialAccount(accountId, "cus_org", "Test Bank", "6789", "Checking", "active", [BankLinkCharges.SepaDebit]));

        public Task<StartedBankDebit> CreateDebitAsync(BankDebitCommand command, CancellationToken cancellationToken = default)
        {
            CreatedKeys.Add(command.IdempotencyKey);
            return Task.FromResult(new StartedBankDebit(
                "pi_" + command.IdempotencyKey, "secret", "requires_confirmation", BankLinkCharges.SepaDebit, "pm_sepa"));
        }

        public Task<StartedBankDebit> ConfirmDebitAsync(
            string paymentIntentId, string idempotencyKey, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
        {
            if (Saved is not null && !Saved(paymentIntentId))
                throw new InvalidOperationException("PaymentIntent confirmed before the bank-link row was saved.");
            return Task.FromResult(new StartedBankDebit(paymentIntentId, "secret", "processing", BankLinkCharges.SepaDebit, "pm_sepa"));
        }
    }
}

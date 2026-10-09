using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-14: Stripe's <c>account.updated</c> for the connected account of a supplier org is applied by the existing handler
/// (<c>StripeWebhookHandler</c> to <c>ConnectOnboardingService.ApplyAccountUpdatedAsync</c>, which finds the org by
/// <c>StripeConnectedAccountId</c>): it updates that org and no other, and what the supplier then reads is the new state. The
/// handler is built without any feature flag on purpose: the processing of what Stripe sends is never behind
/// <c>SupplierOnlinePayments</c> (money in flight, later SP-15).
/// </summary>
public class SupplierAccountUpdatedWebhookTests
{
    private const string AccountA = "acct_supplier_a";
    private const string AccountB = "acct_supplier_b";
    private const string AccountHost = "acct_host";

    private readonly Mock<IStripeConnectGateway> _gateway = new(MockBehavior.Strict);

    [Fact]
    public async Task AccountUpdated_OfASupplierAccount_UpdatesThatSupplierOrgAndNoOtherOrg()
    {
        await using var db = CreateDb();
        var supplierA = await SeedOrgAsync(db, AccountA, OrgType.Supplier, charges: false, payouts: false, requirementsJson: """["external_account"]""");
        var supplierB = await SeedOrgAsync(db, AccountB, OrgType.Supplier, charges: false, payouts: false, requirementsJson: """["business_profile.url"]""");
        var host = await SeedOrgAsync(db, AccountHost, OrgType.Host, charges: true, payouts: true, requirementsJson: null);

        await CreateHandler(db).HandleEventAsync(AccountUpdated("evt_a_1", AccountA, charges: true, payouts: true, submitted: true, due: []), WebhookSource.Connected);

        var a = await ReloadAsync(db, supplierA);
        Assert.True(a.ConnectChargesEnabled);
        Assert.True(a.ConnectPayoutsEnabled);
        Assert.True(a.ConnectDetailsSubmitted);
        Assert.Null(a.ConnectRequirementsDueJson);
        Assert.Equal(AccountA, a.StripeConnectedAccountId);

        // Nobody else: the other supplier keeps its capabilities and its requirement, the host org keeps its own.
        var b = await ReloadAsync(db, supplierB);
        Assert.False(b.ConnectChargesEnabled);
        Assert.False(b.ConnectPayoutsEnabled);
        Assert.False(b.ConnectDetailsSubmitted);
        Assert.Equal("""["business_profile.url"]""", b.ConnectRequirementsDueJson);
        var h = await ReloadAsync(db, host);
        Assert.True(h.ConnectChargesEnabled);
        Assert.True(h.ConnectPayoutsEnabled);
        Assert.Null(h.ConnectRequirementsDueJson);
        // The webhook never calls Stripe back (strict mock without setups).
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AccountUpdated_TheSupplierReadsTheNewStateWithoutAnyStripeCall()
    {
        await using var db = CreateDb();
        var supplier = await SeedOrgAsync(db, AccountA, OrgType.Supplier, charges: false, payouts: false, requirementsJson: """["external_account"]""");
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplier,
            Status = SupplierStatus.Active,
            VatNumber = "IT12345678901",
            LegalName = "Fornitore Srl",
            Phone = "+39 06 000000",
            Email = "fornitore@example.com",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var payments = new SupplierPaymentsAccountService(
            db,
            new ConnectOnboardingService(db, _gateway.Object, NullLogger<ConnectOnboardingService>.Instance),
            _gateway.Object,
            NullLogger<SupplierPaymentsAccountService>.Instance);

        var before = await payments.GetAccountAsync(supplier, refreshFromStripe: false);
        Assert.False(before.CanReceivePayments);
        Assert.False(before.Verified);
        Assert.Equal(["external_account"], before.RequirementsDue);

        await CreateHandler(db).HandleEventAsync(AccountUpdated("evt_a_2", AccountA, charges: true, payouts: true, submitted: true, due: []), WebhookSource.Connected);

        var after = await payments.GetAccountAsync(supplier, refreshFromStripe: false);
        Assert.True(after.ChargesEnabled);
        Assert.True(after.PayoutsEnabled);
        Assert.True(after.CanReceivePayments);
        Assert.True(after.Verified);
        Assert.Empty(after.RequirementsDue);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AccountUpdated_StripeDisablesTheAccountAgain_TheSupplierLosesVerifiedAndSeesTheRequirements()
    {
        await using var db = CreateDb();
        var supplier = await SeedOrgAsync(db, AccountA, OrgType.Supplier, charges: true, payouts: true, requirementsJson: null);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplier,
            Status = SupplierStatus.Active,
            VatNumber = "IT12345678901",
            LegalName = "Fornitore Srl",
            Phone = "+39 06 000000",
            Email = "fornitore@example.com",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var payments = new SupplierPaymentsAccountService(
            db,
            new ConnectOnboardingService(db, _gateway.Object, NullLogger<ConnectOnboardingService>.Instance),
            _gateway.Object,
            NullLogger<SupplierPaymentsAccountService>.Instance);
        Assert.True((await payments.GetAccountAsync(supplier, refreshFromStripe: false)).Verified);

        await CreateHandler(db).HandleEventAsync(
            AccountUpdated("evt_a_3", AccountA, charges: false, payouts: false, submitted: true, due: ["individual.verification.document"]),
            WebhookSource.Connected);

        var state = await payments.GetAccountAsync(supplier, refreshFromStripe: false);
        Assert.False(state.CanReceivePayments);
        Assert.False(state.Verified);
        Assert.Contains(Casazen.Core.Suppliers.SupplierVerification.PaymentsNotEnabled, state.VerificationMissing);
        Assert.Equal(["individual.verification.document"], state.RequirementsDue);
    }

    [Fact]
    public async Task AccountUpdated_OfAnAccountNoOrgHolds_ChangesNothingAndDoesNotFail()
    {
        await using var db = CreateDb();
        var supplier = await SeedOrgAsync(db, AccountA, OrgType.Supplier, charges: false, payouts: false, requirementsJson: null);
        var host = await SeedOrgAsync(db, AccountHost, OrgType.Host, charges: true, payouts: true, requirementsJson: null);

        await CreateHandler(db).HandleEventAsync(
            AccountUpdated("evt_unknown", "acct_nobody", charges: true, payouts: true, submitted: true, due: []),
            WebhookSource.Connected);

        Assert.False((await ReloadAsync(db, supplier)).ConnectChargesEnabled);
        Assert.True((await ReloadAsync(db, host)).ConnectChargesEnabled);
        Assert.True(await db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_unknown"));
    }

    [Fact]
    public async Task AccountUpdated_OnThePlatformEndpoint_IsNotApplied()
    {
        await using var db = CreateDb();
        var supplier = await SeedOrgAsync(db, AccountA, OrgType.Supplier, charges: false, payouts: false, requirementsJson: null);

        // account.updated of a connected account arrives on the Connect endpoint; the platform endpoint ignores it.
        await CreateHandler(db).HandleEventAsync(
            AccountUpdated("evt_platform", AccountA, charges: true, payouts: true, submitted: true, due: []),
            WebhookSource.Platform);

        Assert.False((await ReloadAsync(db, supplier)).ConnectChargesEnabled);
    }

    [Fact]
    public async Task AccountUpdated_DeliveredTwice_IsAppliedOnce()
    {
        await using var db = CreateDb();
        var supplier = await SeedOrgAsync(db, AccountA, OrgType.Supplier, charges: false, payouts: false, requirementsJson: null);
        var stripeEvent = AccountUpdated("evt_twice", AccountA, charges: true, payouts: true, submitted: true, due: []);
        await CreateHandler(db).HandleEventAsync(stripeEvent, WebhookSource.Connected);

        // Something else changed the org after the first delivery; the duplicate must not overwrite it.
        var tracked = await db.Orgs.SingleAsync(o => o.Id == supplier);
        tracked.ConnectChargesEnabled = false;
        await db.SaveChangesAsync();
        await CreateHandler(db).HandleEventAsync(stripeEvent, WebhookSource.Connected);

        Assert.False((await ReloadAsync(db, supplier)).ConnectChargesEnabled);
        Assert.Equal(1, await db.ProcessedStripeEvents.CountAsync(e => e.EventId == "evt_twice"));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private StripeWebhookHandler CreateHandler(AppDbContext db) =>
        new(
            new PaymentRepository(db),
            new BookingRepository(db),
            new ConnectOnboardingService(db, _gateway.Object, NullLogger<ConnectOnboardingService>.Instance),
            db,
            Mock.Of<IStripeBillingService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IPlatformInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(db, null),
            TestDeferredCharges.Create(db),
            NullLogger<StripeWebhookHandler>.Instance);

    private static Event AccountUpdated(
        string eventId,
        string accountId,
        bool charges,
        bool payouts,
        bool submitted,
        string[] due) =>
        new()
        {
            Id = eventId,
            Type = "account.updated",
            Account = accountId,
            Data = new EventData
            {
                Object = new Account
                {
                    Id = accountId,
                    ChargesEnabled = charges,
                    PayoutsEnabled = payouts,
                    DetailsSubmitted = submitted,
                    Requirements = new AccountRequirements { CurrentlyDue = due.ToList() },
                },
            },
        };

    private static async Task<Guid> SeedOrgAsync(
        AppDbContext db,
        string accountId,
        OrgType type,
        bool charges,
        bool payouts,
        string? requirementsJson)
    {
        var org = new OrgEntity
        {
            Name = $"Org {accountId}",
            Slug = $"{type.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..30],
            DisplayName = $"Org {accountId}",
            ContactEmail = $"{accountId}@example.com",
            OrgType = type,
            IsActive = true,
            StripeConnectedAccountId = accountId,
            ConnectChargesEnabled = charges,
            ConnectPayoutsEnabled = payouts,
            ConnectDetailsSubmitted = charges,
            ConnectRequirementsDueJson = requirementsJson,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return org.Id;
    }

    private static async Task<OrgEntity> ReloadAsync(AppDbContext db, Guid orgId)
    {
        db.ChangeTracker.Clear();
        return await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"supplier-account-updated-{Guid.NewGuid()}")
            .Options);
}

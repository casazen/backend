using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-14: the supplier's Stripe Connect account on the real <see cref="ConnectOnboardingService"/> and a strict mock of the
/// gateway (a call that is not set up fails the test): the state is read from the database unless a refresh is asked, the
/// onboarding creates the account once, the dashboard link needs an existing account, the "Verificato" state follows the
/// profile, Stripe and the VAT number, and an org without a supplier profile never reaches Stripe.
/// </summary>
public class SupplierPaymentsAccountServiceTests
{
    private const string Account = "acct_supplier_1";
    private const string ReturnUrl = "https://app.example.org/app/supplier/settings?stripe_return=1";
    private const string RefreshUrl = "https://app.example.org/app/supplier/settings?stripe_refresh=1";

    private readonly Mock<IStripeConnectGateway> _gateway = new(MockBehavior.Strict);

    // ─── State ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccountAsync_SupplierWithoutAccount_HasNoAccountAndIsNotVerified()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: false);

        Assert.False(state.HasAccount);
        Assert.False(state.ChargesEnabled);
        Assert.False(state.PayoutsEnabled);
        Assert.False(state.DetailsSubmitted);
        Assert.Empty(state.RequirementsDue);
        Assert.False(state.CanReceivePayments);
        Assert.False(state.Verified);
        Assert.Equal([SupplierVerification.PaymentsNotEnabled], state.VerificationMissing);
    }

    [Fact]
    public async Task GetAccountAsync_WithoutRefresh_ReadsTheDatabaseAndNeverCallsStripe()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account, charges: true, payouts: true, detailsSubmitted: true);

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: false);

        // The strict mock has no setup: any Stripe call would have thrown.
        Assert.True(state.HasAccount);
        Assert.True(state.ChargesEnabled);
        Assert.True(state.PayoutsEnabled);
        Assert.True(state.DetailsSubmitted);
        Assert.True(state.CanReceivePayments);
        Assert.True(state.Verified);
        Assert.Empty(state.VerificationMissing);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAccountAsync_Refresh_ReadsStripeAndStoresTheCapabilities()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account, requirementsJson: """["external_account"]""");
        _gateway
            .Setup(g => g.GetAccountAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot(Account, ChargesEnabled: true, PayoutsEnabled: true, DetailsSubmitted: true, RequirementsDue: []));

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: true);

        Assert.True(state.CanReceivePayments);
        Assert.Empty(state.RequirementsDue);
        var stored = await ReloadOrgAsync(db, orgId);
        Assert.True(stored.ConnectChargesEnabled);
        Assert.True(stored.ConnectPayoutsEnabled);
        Assert.True(stored.ConnectDetailsSubmitted);
        Assert.Null(stored.ConnectRequirementsDueJson);
        _gateway.Verify(g => g.GetAccountAsync(Account, It.IsAny<CancellationToken>()), Times.Once);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAccountAsync_RefreshWithoutAccount_NeverCallsStripe()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: true);

        Assert.False(state.HasAccount);
        _gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task GetAccountAsync_ChargesAndPayouts_BothNeededToReceivePayments(bool charges, bool payouts, bool expected)
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account, charges: charges, payouts: payouts, detailsSubmitted: true);

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: false);

        Assert.Equal(charges, state.ChargesEnabled);
        Assert.Equal(payouts, state.PayoutsEnabled);
        Assert.Equal(expected, state.CanReceivePayments);
        // Verified follows the payments once the profile is Active and has a VAT number.
        Assert.Equal(expected, state.Verified);
        Assert.Equal(expected, !state.VerificationMissing.Contains(SupplierVerification.PaymentsNotEnabled));
    }

    [Fact]
    public async Task GetAccountAsync_RequirementsDue_AreTheFieldNamesStripeReports()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(
            db,
            accountId: Account,
            requirementsJson: """["external_account","individual.verification.document"]""");

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: false);

        Assert.Equal(["external_account", "individual.verification.document"], state.RequirementsDue);
        Assert.False(state.CanReceivePayments);
    }

    [Theory]
    [InlineData(SupplierStatus.Active, "IT12345678901", true, true)]
    [InlineData(SupplierStatus.Pending, "IT12345678901", true, false)]
    [InlineData(SupplierStatus.Suspended, "IT12345678901", true, false)]
    [InlineData(SupplierStatus.Active, null, true, false)]
    [InlineData(SupplierStatus.Active, "  ", true, false)]
    [InlineData(SupplierStatus.Active, "IT12345678901", false, false)]
    public async Task GetAccountAsync_Verified_NeedsActiveProfilePaymentsAndAVatNumber(
        SupplierStatus status,
        string? vatNumber,
        bool paymentsEnabled,
        bool expectedVerified)
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(
            db, status, vatNumber, accountId: Account, charges: paymentsEnabled, payouts: paymentsEnabled, detailsSubmitted: true);

        var state = await Service(db).GetAccountAsync(orgId, refreshFromStripe: false);

        Assert.Equal(expectedVerified, state.Verified);
        Assert.Equal(expectedVerified, state.VerificationMissing.Count == 0);
        Assert.Equal(status != SupplierStatus.Active, state.VerificationMissing.Contains(SupplierVerification.ProfileNotActive));
        Assert.Equal(string.IsNullOrWhiteSpace(vatNumber), state.VerificationMissing.Contains(SupplierVerification.VatNumberMissing));
    }

    [Fact]
    public async Task GetAccountAsync_VerificationDoesNotChangeTheProfile()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, SupplierStatus.Pending, vatNumber: null, accountId: Account, charges: true, payouts: true);

        await Service(db).GetAccountAsync(orgId, refreshFromStripe: false);

        // Read-only: the activation and the profile are exactly as they were.
        db.ChangeTracker.Clear();
        var profile = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
        Assert.Equal(SupplierStatus.Pending, profile.Status);
        Assert.Null(profile.VatNumber);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit", StripeConnectFailure.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, StripeConnectFailure.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, null, StripeConnectFailure.Configuration)]
    [InlineData(HttpStatusCode.BadRequest, null, StripeConnectFailure.Rejected)]
    public async Task GetAccountAsync_RefreshFailsAtStripe_ThrowsAndKeepsTheStoredState(
        HttpStatusCode status,
        string? code,
        StripeConnectFailure expected)
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account, charges: true, payouts: true, detailsSubmitted: true);
        _gateway.Setup(g => g.GetAccountAsync(Account, It.IsAny<CancellationToken>())).ThrowsAsync(StripeFailure(status, code));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Service(db).GetAccountAsync(orgId, refreshFromStripe: true));

        Assert.Equal(expected, ex.Failure);
        var stored = await ReloadOrgAsync(db, orgId);
        Assert.Equal(Account, stored.StripeConnectedAccountId);
        Assert.True(stored.ConnectChargesEnabled);
        Assert.True(stored.ConnectPayoutsEnabled);
    }

    // ─── The account and the onboarding link ─────────────────────────────────────

    [Fact]
    public async Task EnsureAccountAsync_NoAccount_CreatesItOnceWithTheOrgIdempotencyKey()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync("fornitore@example.com", $"connect-account:{orgId:N}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account);
        _gateway
            .Setup(g => g.GetAccountAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot(Account, false, false, false, ["external_account"]));
        var service = Service(db);

        var first = await service.EnsureAccountAsync(orgId);
        var second = await service.EnsureAccountAsync(orgId);

        Assert.True(first.HasAccount);
        Assert.True(second.HasAccount);
        Assert.Equal(["external_account"], second.RequirementsDue);
        Assert.False(second.CanReceivePayments);
        // One account for two requests: the second finds the first one's (the PostgreSQL lock covers the parallel case).
        _gateway.Verify(
            g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(Account, (await ReloadOrgAsync(db, orgId)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task CreateOnboardingLinkAsync_NoAccount_CreatesTheAccountAndLinksWithTheGivenUrls()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync("fornitore@example.com", $"connect-account:{orgId:N}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account);
        _gateway
            .Setup(g => g.GetAccountAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot(Account, false, false, false, []));
        _gateway
            .Setup(g => g.CreateAccountOnboardingLinkAsync(Account, ReturnUrl, RefreshUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://connect.stripe.test/onboard/abc");

        var url = await Service(db).CreateOnboardingLinkAsync(orgId, ReturnUrl, RefreshUrl);

        Assert.Equal("https://connect.stripe.test/onboard/abc", url);
        Assert.Equal(Account, (await ReloadOrgAsync(db, orgId)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task CreateOnboardingLinkAsync_ExistingAccount_UsesItWithoutCreatingAnother()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account);
        _gateway
            .Setup(g => g.GetAccountAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot(Account, false, false, false, ["business_profile.url"]));
        _gateway
            .Setup(g => g.CreateAccountOnboardingLinkAsync(Account, ReturnUrl, RefreshUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://connect.stripe.test/onboard/again");

        var url = await Service(db).CreateOnboardingLinkAsync(orgId, ReturnUrl, RefreshUrl);

        Assert.Equal("https://connect.stripe.test/onboard/again", url);
        _gateway.Verify(
            g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateOnboardingLinkAsync_StripeUnavailable_ThrowsAndLinksNothing()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.ServiceUnavailable, null));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Service(db).CreateOnboardingLinkAsync(orgId, ReturnUrl, RefreshUrl));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
        Assert.Null((await ReloadOrgAsync(db, orgId)).StripeConnectedAccountId);
    }

    [Theory]
    [InlineData("", RefreshUrl)]
    [InlineData(ReturnUrl, "  ")]
    public async Task CreateOnboardingLinkAsync_BlankUrl_ThrowsBeforeCallingStripe(string returnUrl, string refreshUrl)
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);

        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).CreateOnboardingLinkAsync(orgId, returnUrl, refreshUrl));

        _gateway.VerifyNoOtherCalls();
    }

    // ─── The dashboard link ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDashboardLinkAsync_NoAccount_IsNotReadyAndNeverCallsStripe()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).CreateDashboardLinkAsync(orgId));

        Assert.Equal("supplier_payments_not_ready", ex.Code);
        Assert.Equal("SupplierPaymentsNotReady", ex.MessageKey);
        // No account is created to open its dashboard.
        _gateway.VerifyNoOtherCalls();
        Assert.Null((await ReloadOrgAsync(db, orgId)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task CreateDashboardLinkAsync_ExistingAccount_ReturnsTheLoginLinkWithoutReadingTheAccount()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account, detailsSubmitted: true);
        _gateway
            .Setup(g => g.CreateDashboardLoginLinkAsync(Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://connect.stripe.test/express/abc");

        var url = await Service(db).CreateDashboardLinkAsync(orgId);

        Assert.Equal("https://connect.stripe.test/express/abc", url);
        _gateway.Verify(g => g.CreateDashboardLoginLinkAsync(Account, It.IsAny<CancellationToken>()), Times.Once);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateDashboardLinkAsync_StripeRefusesBecauseTheOnboardingIsIncomplete_IsNotReadyNotAServerError()
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account);
        _gateway
            .Setup(g => g.CreateDashboardLoginLinkAsync(Account, It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.BadRequest, null));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).CreateDashboardLinkAsync(orgId));

        Assert.Equal("supplier_payments_not_ready", ex.Code);
        // The account stays linked: the supplier finishes the onboarding with the onboarding link.
        Assert.Equal(Account, (await ReloadOrgAsync(db, orgId)).StripeConnectedAccountId);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit", StripeConnectFailure.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, null, StripeConnectFailure.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, null, StripeConnectFailure.Configuration)]
    [InlineData(HttpStatusCode.NotFound, "resource_missing", StripeConnectFailure.AccountUnavailable)]
    public async Task CreateDashboardLinkAsync_OtherStripeFailures_PropagateWithTheirKind(
        HttpStatusCode status,
        string? code,
        StripeConnectFailure expected)
    {
        await using var db = CreateDb();
        var orgId = await SeedSupplierAsync(db, accountId: Account, detailsSubmitted: true);
        _gateway
            .Setup(g => g.CreateDashboardLoginLinkAsync(Account, It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(status, code));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Service(db).CreateDashboardLinkAsync(orgId));

        Assert.Equal(expected, ex.Failure);
        // A dashboard link never unlinks the account, whatever Stripe answers.
        Assert.Equal(Account, (await ReloadOrgAsync(db, orgId)).StripeConnectedAccountId);
    }

    // ─── Only a supplier org ─────────────────────────────────────────────────────

    [Fact]
    public async Task EveryOperation_OrgWithoutASupplierProfile_IsNotFoundAndNeverReachesStripe()
    {
        await using var db = CreateDb();
        var hostOrg = new OrgEntity
        {
            Name = "Host",
            Slug = $"host-{Guid.NewGuid():N}",
            DisplayName = "Host",
            ContactEmail = "host@example.com",
            IsActive = true,
            StripeConnectedAccountId = "acct_host",
            ConnectChargesEnabled = true,
            ConnectPayoutsEnabled = true,
        };
        db.Orgs.Add(hostOrg);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = Service(db);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAccountAsync(hostOrg.Id, refreshFromStripe: true));
        await Assert.ThrowsAsync<NotFoundException>(() => service.EnsureAccountAsync(hostOrg.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateOnboardingLinkAsync(hostOrg.Id, ReturnUrl, RefreshUrl));
        var dashboard = await Assert.ThrowsAsync<NotFoundException>(() => service.CreateDashboardLinkAsync(hostOrg.Id));

        Assert.Equal("SupplierProfileNotFound", dashboard.MessageKey);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EveryOperation_UnknownOrg_IsNotFound()
    {
        await using var db = CreateDb();
        var service = Service(db);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAccountAsync(Guid.NewGuid(), refreshFromStripe: false));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CreateDashboardLinkAsync(Guid.NewGuid()));
        _gateway.VerifyNoOtherCalls();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private SupplierPaymentsAccountService Service(AppDbContext db) =>
        new(
            db,
            new ConnectOnboardingService(db, _gateway.Object, NullLogger<ConnectOnboardingService>.Instance),
            _gateway.Object,
            NullLogger<SupplierPaymentsAccountService>.Instance);

    /// <summary>The exception the real gateway throws for this Stripe answer.</summary>
    private static StripeConnectException StripeFailure(HttpStatusCode status, string? code) =>
        StripeConnectGateway.ToConnectException(StripeConnectGatewayTests.StripeError(status, code), "test");

    private static async Task<Guid> SeedSupplierAsync(
        AppDbContext db,
        SupplierStatus status = SupplierStatus.Active,
        string? vatNumber = "IT12345678901",
        string? accountId = null,
        bool charges = false,
        bool payouts = false,
        bool detailsSubmitted = false,
        string? requirementsJson = null)
    {
        var org = new OrgEntity
        {
            Name = "Fornitore SP-14",
            Slug = $"supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Fornitore SP-14",
            ContactEmail = "fornitore@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
            StripeConnectedAccountId = accountId,
            ConnectChargesEnabled = charges,
            ConnectPayoutsEnabled = payouts,
            ConnectDetailsSubmitted = detailsSubmitted,
            ConnectRequirementsDueJson = requirementsJson,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Status = status,
            VatNumber = vatNumber,
            LegalName = "Fornitore SP-14 Srl",
            Phone = "+39 06 000000",
            Email = "fornitore@example.com",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return org.Id;
    }

    private static async Task<OrgEntity> ReloadOrgAsync(AppDbContext db, Guid orgId)
    {
        db.ChangeTracker.Clear();
        return await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"supplier-payments-{Guid.NewGuid()}")
            .Options);
}

using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-09 (A3-19): the org's connected account is replaced only when Stripe says it does not exist or was revoked. A rate
/// limit, an outage or a key problem throws and leaves the account and its capabilities as they were. The creation
/// carries an idempotency key bound to the org (and to the replaced account).
/// </summary>
public class ConnectOnboardingServiceTests
{
    private const string VerifiedAccount = "acct_verified_host";

    private readonly Mock<IStripeConnectGateway> _gateway = new(MockBehavior.Strict);

    public static TheoryData<HttpStatusCode, string?> ErrorsThatKeepTheAccount => new()
    {
        { HttpStatusCode.TooManyRequests, "rate_limit" },
        { HttpStatusCode.TooManyRequests, "lock_timeout" },
        { HttpStatusCode.ServiceUnavailable, null },
        { HttpStatusCode.Unauthorized, null },
        { HttpStatusCode.Forbidden, null },
        { HttpStatusCode.BadRequest, null },
    };

    [Theory]
    [MemberData(nameof(ErrorsThatKeepTheAccount))]
    public async Task EnsureExpressAccountAsync_StripeErrorOtherThanMissingAccount_KeepsAccountAndThrows(
        HttpStatusCode status,
        string? code)
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, VerifiedAccount, chargesEnabled: true);
        var failure = StripeFailure(status, code);
        _gateway.Setup(g => g.GetAccountAsync(VerifiedAccount, It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Service(db).EnsureExpressAccountAsync(org.Id));

        Assert.Equal(failure.Failure, ex.Failure);
        Assert.NotEqual(StripeConnectFailure.AccountUnavailable, ex.Failure);
        var persisted = await ReloadAsync(db, org.Id);
        Assert.Equal(VerifiedAccount, persisted.StripeConnectedAccountId);
        Assert.True(persisted.ConnectChargesEnabled);
        Assert.True(persisted.ConnectPayoutsEnabled);
        _gateway.Verify(
            g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_GetAccountRateLimited_ThrowsTransientAndKeepsAccount()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, VerifiedAccount, chargesEnabled: true);
        _gateway
            .Setup(g => g.GetAccountAsync(VerifiedAccount, It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.TooManyRequests, "rate_limit"));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Service(db).EnsureExpressAccountAsync(org.Id));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
        Assert.Equal(VerifiedAccount, (await ReloadAsync(db, org.Id)).StripeConnectedAccountId);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "resource_missing")]
    [InlineData(HttpStatusCode.Forbidden, "account_invalid")]
    public async Task EnsureExpressAccountAsync_AccountMissingOrRevoked_CreatesReplacementWithReplacementKey(
        HttpStatusCode status,
        string code)
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "acct_deleted", chargesEnabled: true);
        _gateway.Setup(g => g.GetAccountAsync("acct_deleted", It.IsAny<CancellationToken>())).ThrowsAsync(StripeFailure(status, code));
        _gateway
            .Setup(g => g.CreateExpressAccountAsync("owner@example.com", "IT", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("acct_replacement");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_replacement", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_replacement", false, false, false, ["business_type"]));

        var status2 = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_replacement", status2.ConnectedAccountId);
        Assert.False(status2.ChargesEnabled);
        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "IT",
            $"connect-account:{org.Id:N}:replaces:acct_deleted",
            It.IsAny<CancellationToken>()), Times.Once);
        var persisted = await ReloadAsync(db, org.Id);
        Assert.Equal("acct_replacement", persisted.StripeConnectedAccountId);
        Assert.False(persisted.ConnectChargesEnabled);
        Assert.False(persisted.ConnectPayoutsEnabled);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_NoAccount_CreatesWithOrgIdempotencyKey()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync("owner@example.com", "IT", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("acct_first");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_first", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_first", false, false, false, []));

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_first", status.ConnectedAccountId);
        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "IT",
            $"connect-account:{org.Id:N}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_CreationFailsTransiently_LinksNothing()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.ServiceUnavailable, null));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() => Service(db).EnsureExpressAccountAsync(org.Id));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
        Assert.Null((await ReloadAsync(db, org.Id)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_RefreshAfterCreationFailsTransiently_KeepsNewAccountLinked()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("acct_created");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_created", It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.TooManyRequests, "rate_limit"));

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_created", status.ConnectedAccountId);
        Assert.Equal("acct_created", (await ReloadAsync(db, org.Id)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task GetStatusAsync_AccountMissing_ClearsCapabilitiesAndKeepsAccountId()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "acct_deleted", chargesEnabled: true);
        _gateway
            .Setup(g => g.GetAccountAsync("acct_deleted", It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.NotFound, "resource_missing"));

        var status = await Service(db).GetStatusAsync(org.Id, refreshFromStripe: true);

        // No checkout on an account Stripe no longer knows; the id stays for the replacement key of the onboarding.
        Assert.Equal("acct_deleted", status.ConnectedAccountId);
        Assert.False(status.ChargesEnabled);
        var persisted = await ReloadAsync(db, org.Id);
        Assert.Equal("acct_deleted", persisted.StripeConnectedAccountId);
        Assert.False(persisted.ConnectChargesEnabled);
        Assert.False(persisted.ConnectPayoutsEnabled);
    }

    [Fact]
    public async Task GetStatusAsync_RateLimited_ThrowsAndKeepsCapabilities()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, VerifiedAccount, chargesEnabled: true);
        _gateway
            .Setup(g => g.GetAccountAsync(VerifiedAccount, It.IsAny<CancellationToken>()))
            .ThrowsAsync(StripeFailure(HttpStatusCode.TooManyRequests, "rate_limit"));

        var ex = await Assert.ThrowsAsync<StripeConnectException>(() =>
            Service(db).GetStatusAsync(org.Id, refreshFromStripe: true));

        Assert.Equal(StripeConnectFailure.Transient, ex.Failure);
        var persisted = await ReloadAsync(db, org.Id);
        Assert.Equal(VerifiedAccount, persisted.StripeConnectedAccountId);
        Assert.True(persisted.ConnectChargesEnabled);
    }

    // Issue #1001 — the Stripe account follows the org's country. The Quokka360 test org is Swiss (billing country and confirmed
    // fiscal profile CH), yet its account was created as Italian: the hosted form asked for Italian data that a Swiss company
    // cannot give, and Stripe's country can never be changed.

    [Fact]
    public async Task EnsureExpressAccountAsync_OrgWithConfirmedSwissFiscalProfile_CreatesTheAccountInSwitzerland()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        await SeedFiscalProfileAsync(db, org.Id, "CH", confirmed: true);
        SetupCreation("CH", "acct_swiss");

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_swiss", status.ConnectedAccountId);
        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "CH",
            $"connect-account:{org.Id:N}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("CH", "CH")]
    [InlineData("ch", "CH")]
    [InlineData("IT", "IT")]
    [InlineData("DE", "IT")]
    [InlineData(null, "IT")]
    public async Task EnsureExpressAccountAsync_NoFiscalProfile_UsesTheBillingCountryOfTheOrgWhenTheProductSupportsIt(
        string? billingCountry,
        string expectedCountry)
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false, billingCountry);
        SetupCreation(expectedCountry, "acct_by_billing");

        await Service(db).EnsureExpressAccountAsync(org.Id);

        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            expectedCountry,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_FiscalProfileNotConfirmed_IsIgnored()
    {
        // An unconfirmed profile only holds what the wizard started with (a default country), not a declaration of the org.
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        await SeedFiscalProfileAsync(db, org.Id, "CH", confirmed: false);
        SetupCreation("IT", "acct_default");

        await Service(db).EnsureExpressAccountAsync(org.Id);

        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "IT",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_ConfirmedFiscalProfile_WinsOverTheBillingCountry()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false, billingCountry: "CH");
        await SeedFiscalProfileAsync(db, org.Id, "IT", confirmed: true);
        SetupCreation("IT", "acct_fiscal");

        await Service(db).EnsureExpressAccountAsync(org.Id);

        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "IT",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_ProfileOfAnOwnerOrOfAnotherOrg_IsNotTheOrgsCountry()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        var other = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        await SeedFiscalProfileAsync(db, other.Id, "CH", confirmed: true);
        await SeedFiscalProfileAsync(db, org.Id, "CH", confirmed: true, propertyOwnerId: Guid.NewGuid());
        SetupCreation("IT", "acct_own");

        await Service(db).EnsureExpressAccountAsync(org.Id);

        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "IT",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("CH", "CH")]
    [InlineData(" ch ", "CH")]
    [InlineData(null, "IT")]
    public async Task EnsureExpressAccountAsync_OrgWithoutCountry_UsesThePlatformDefault(string? configuredDefault, string expectedCountry)
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        SetupCreation(expectedCountry, "acct_platform_default");

        await Service(db, configuredDefault).EnsureExpressAccountAsync(org.Id);

        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            expectedCountry,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_UnsubmittedAccountOfAnotherCountry_IsReplacedByOneInTheOrgCountry()
    {
        // The Quokka360 case: Swiss org, Italian account whose onboarding never got through.
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "acct_italian", chargesEnabled: false, billingCountry: "CH");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_italian", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_italian", false, false, false, ["company.tax_id"], "IT"));
        SetupCreation("CH", "acct_swiss");

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_swiss", status.ConnectedAccountId);
        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            "CH",
            $"connect-account:{org.Id:N}:replaces:acct_italian",
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("acct_swiss", (await ReloadAsync(db, org.Id)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_SubmittedAccountOfAnotherCountry_IsKept()
    {
        // A submitted account may hold a verified business and money: it is never swapped under the org.
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "acct_italian", chargesEnabled: false, billingCountry: "CH");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_italian", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_italian", false, false, DetailsSubmitted: true, ["external_account"], "IT"));

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_italian", status.ConnectedAccountId);
        Assert.True(status.DetailsSubmitted);
        _gateway.Verify(
            g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("acct_italian", (await ReloadAsync(db, org.Id)).StripeConnectedAccountId);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_UnsubmittedAccountOfTheOrgCountry_IsKept()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "acct_swiss", chargesEnabled: false, billingCountry: "CH");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_swiss", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_swiss", false, false, false, ["external_account"], "ch"));

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_swiss", status.ConnectedAccountId);
        _gateway.Verify(
            g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("IT", "CH", false, false, false, true)]
    [InlineData("it", " CH ", false, false, false, true)]
    [InlineData("CH", "CH", false, false, false, false)]
    [InlineData(null, "CH", false, false, false, false)]
    [InlineData("", "CH", false, false, false, false)]
    [InlineData("IT", "CH", true, false, false, false)]
    [InlineData("IT", "CH", false, true, false, false)]
    [InlineData("IT", "CH", false, false, true, false)]
    public void IsUnfinishedInOtherCountry_ReplacesOnlyAnUntouchedAccountOfAnotherKnownCountry(
        string? accountCountry,
        string orgCountry,
        bool detailsSubmitted,
        bool chargesEnabled,
        bool payoutsEnabled,
        bool expected)
    {
        var snapshot = new ConnectAccountSnapshot("acct_x", chargesEnabled, payoutsEnabled, detailsSubmitted, [], accountCountry);

        Assert.Equal(expected, ConnectOnboardingService.IsUnfinishedInOtherCountry(snapshot, orgCountry.Trim()));
    }

    /// <summary>Stripe creates the account in <paramref name="country"/> and reads it back as such.</summary>
    private void SetupCreation(string country, string accountId)
    {
        _gateway
            .Setup(g => g.CreateExpressAccountAsync("owner@example.com", country, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accountId);
        _gateway
            .Setup(g => g.GetAccountAsync(accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot(accountId, false, false, false, ["business_type"], country));
    }

    /// <summary>The exception the real gateway throws for this Stripe answer.</summary>
    private static StripeConnectException StripeFailure(HttpStatusCode status, string? code) =>
        StripeConnectGateway.ToConnectException(StripeConnectGatewayTests.StripeError(status, code), "test");

    private ConnectOnboardingService Service(AppDbContext db, string? configuredDefaultCountry = null) =>
        new(
            db,
            _gateway.Object,
            NullLogger<ConnectOnboardingService>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:ConnectDefaultCountry"] = configuredDefaultCountry })
                .Build());

    private static async Task<OrgEntity> SeedOrgAsync(
        AppDbContext db,
        string? accountId,
        bool chargesEnabled,
        string? billingCountry = null)
    {
        var org = new OrgEntity
        {
            Name = "Connect org",
            Slug = $"connect-{Guid.NewGuid():N}",
            DisplayName = "Connect org",
            ContactEmail = "owner@example.com",
            IsActive = true,
            BillingCountry = billingCountry,
            StripeConnectedAccountId = accountId,
            ConnectChargesEnabled = chargesEnabled,
            ConnectPayoutsEnabled = chargesEnabled,
            ConnectDetailsSubmitted = chargesEnabled,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return org;
    }

    private static async Task SeedFiscalProfileAsync(
        AppDbContext db,
        Guid orgId,
        string country,
        bool confirmed,
        Guid? propertyOwnerId = null)
    {
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            PropertyOwnerId = propertyOwnerId,
            CountryCode = country,
            ConfirmedAt = confirmed ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<OrgEntity> ReloadAsync(AppDbContext db, Guid orgId)
    {
        db.ChangeTracker.Clear();
        return await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }

    private static AppDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            dataProtectionProvider: new EphemeralDataProtectionProvider());
}

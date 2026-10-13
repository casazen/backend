using System.Net;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
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
            g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            .Setup(g => g.CreateExpressAccountAsync("owner@example.com", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("acct_replacement");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_replacement", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_replacement", false, false, false, ["business_type"]));

        var status2 = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_replacement", status2.ConnectedAccountId);
        Assert.False(status2.ChargesEnabled);
        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
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
            .Setup(g => g.CreateExpressAccountAsync("owner@example.com", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("acct_first");
        _gateway
            .Setup(g => g.GetAccountAsync("acct_first", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectAccountSnapshot("acct_first", false, false, false, []));

        var status = await Service(db).EnsureExpressAccountAsync(org.Id);

        Assert.Equal("acct_first", status.ConnectedAccountId);
        _gateway.Verify(g => g.CreateExpressAccountAsync(
            "owner@example.com",
            $"connect-account:{org.Id:N}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureExpressAccountAsync_CreationFailsTransiently_LinksNothing()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, accountId: null, chargesEnabled: false);
        _gateway
            .Setup(g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            .Setup(g => g.CreateExpressAccountAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

    /// <summary>The exception the real gateway throws for this Stripe answer.</summary>
    private static StripeConnectException StripeFailure(HttpStatusCode status, string? code) =>
        StripeConnectGateway.ToConnectException(StripeConnectGatewayTests.StripeError(status, code), "test");

    private ConnectOnboardingService Service(AppDbContext db) =>
        new(db, _gateway.Object, NullLogger<ConnectOnboardingService>.Instance);

    private static async Task<OrgEntity> SeedOrgAsync(AppDbContext db, string? accountId, bool chargesEnabled)
    {
        var org = new OrgEntity
        {
            Name = "Connect org",
            Slug = $"connect-{Guid.NewGuid():N}",
            DisplayName = "Connect org",
            ContactEmail = "owner@example.com",
            IsActive = true,
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

    private static async Task<OrgEntity> ReloadAsync(AppDbContext db, Guid orgId)
    {
        db.ChangeTracker.Clear();
        return await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}

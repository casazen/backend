using System.Reflection;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E40/F01/US02/T01 (NF-141): the state of the owner sign-in on an agency's domain. With <c>WhiteLabelHost</c> and <c>Auth0HostSync</c> on the registration runs at the
/// verification, at the save of the owner portal (from NotRequested or Failed) and on the agency's retry, and its answer decides the state; with
/// <c>Auth0HostSync</c> off Auth0 is never called and the request waits for CasaZen (Pending).
/// </summary>
public sealed class OwnerLoginHostSyncTests : IDisposable
{
    private const string Domain = "prenota.agenzia-esempio.it";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IAuth0HostRegistrar> _registrar = new(MockBehavior.Strict);
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly Mock<IPublicHostResolver> _resolver = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(Auth0HostRegistrationResult.Registered, OwnerLoginHostState.Ready)]
    [InlineData(Auth0HostRegistrationResult.AlreadyRegistered, OwnerLoginHostState.Ready)]
    [InlineData(Auth0HostRegistrationResult.Rejected, OwnerLoginHostState.Failed)]
    [InlineData(Auth0HostRegistrationResult.Unavailable, OwnerLoginHostState.Failed)]
    [InlineData(Auth0HostRegistrationResult.NotConfigured, OwnerLoginHostState.Pending)]
    public async Task DomainVerified_BothFlagsOn_TheAnswerOfTheRegistrarDecidesTheState(
        Auth0HostRegistrationResult registration,
        OwnerLoginHostState expected)
    {
        var org = await SeedAsync(OwnerLoginHostState.NotRequested);
        GivenTheRegistrarAnswers(registration);

        await Sync(FeatureFlags.WhiteLabelHost, FeatureFlags.Auth0HostSync).DomainVerifiedAsync(org, CancellationToken.None);

        Assert.Equal(expected, await StoredStateAsync(org.Id));
        _registrar.Verify(r => r.RegisterAsync(Domain, It.IsAny<CancellationToken>()), Times.Once);
        // The answer of resolve-host carries ownerLoginReady: the cached one is dropped.
        _resolver.Verify(r => r.InvalidateCacheForHost(Domain), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Auth0HostSyncOff_NoCallToAuth0IsEverMade_TheRequestWaitsForCasaZen()
    {
        var org = await SeedAsync(OwnerLoginHostState.Failed);
        var sync = Sync(FeatureFlags.WhiteLabelHost);

        await sync.DomainVerifiedAsync(org, CancellationToken.None);
        await sync.OwnerPortalSavedOnAsync(org, OwnerLoginHostState.Failed, CancellationToken.None);
        Assert.Equal(OwnerLoginHostState.Failed, await StoredStateAsync(org.Id));

        // The agency's retry asks CasaZen again: its platform administrators register the domain by hand.
        await sync.RetryAsync(org, CancellationToken.None);

        Assert.Equal(OwnerLoginHostState.Pending, await StoredStateAsync(org.Id));
        _registrar.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OwnerLoginHostState.NotRequested, true)]
    [InlineData(OwnerLoginHostState.Failed, true)]
    [InlineData(OwnerLoginHostState.Pending, false)]
    [InlineData(OwnerLoginHostState.Ready, false)]
    public async Task SetSurfaces_OwnerPortalOn_RegistersAVerifiedDomainOnlyFromNotRequestedOrFailed(OwnerLoginHostState before, bool registers)
    {
        var org = await SeedAsync(before);
        GivenTheRegistrarAnswers(Auth0HostRegistrationResult.Registered);

        var result = await DomainService(Sync(FeatureFlags.WhiteLabelHost, FeatureFlags.Auth0HostSync)).SetSurfacesAsync(
            org.Id, new OrgDomainSurfaceSettings(GuestAreaOnOwnDomain: true, OwnerPortalOnOwnDomain: true, UseOwnDomainForLinks: true));

        Assert.Equal(SetOrgDomainSurfacesOutcome.Success, result.Outcome);
        _registrar.Verify(r => r.RegisterAsync(Domain, It.IsAny<CancellationToken>()), registers ? Times.Once() : Times.Never());
        var expected = registers ? OwnerLoginHostState.Ready : before;
        Assert.Equal(expected, await StoredStateAsync(org.Id));
        Assert.Equal(expected, result.Config!.Surfaces!.OwnerLoginHostState);
    }

    [Fact]
    public async Task Retry_FailedThenRegistered_IsReady_AndARetryOfAReadyDomainAsksAuth0Nothing()
    {
        var org = await SeedAsync(OwnerLoginHostState.Failed);
        GivenTheRegistrarAnswers(Auth0HostRegistrationResult.AlreadyRegistered);
        var service = DomainService(Sync(FeatureFlags.WhiteLabelHost, FeatureFlags.Auth0HostSync));

        var first = await service.RetryOwnerLoginAsync(org.Id);
        var second = await service.RetryOwnerLoginAsync(org.Id);

        Assert.Equal(RetryOwnerLoginOutcome.Success, first.Outcome);
        Assert.Equal(OwnerLoginHostState.Ready, first.Config!.Surfaces!.OwnerLoginHostState);
        Assert.Equal(RetryOwnerLoginOutcome.Success, second.Outcome);
        Assert.Equal(OwnerLoginHostState.Ready, second.Config!.Surfaces!.OwnerLoginHostState);
        _registrar.Verify(r => r.RegisterAsync(Domain, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retry_DomainNotVerified_IsRefused_AndAuth0IsNotAsked()
    {
        var org = await SeedAsync(OwnerLoginHostState.Failed, DomainVerificationStatus.Pending);

        var result = await DomainService(Sync(FeatureFlags.WhiteLabelHost, FeatureFlags.Auth0HostSync)).RetryOwnerLoginAsync(org.Id);

        Assert.Equal(RetryOwnerLoginOutcome.DomainNotVerified, result.Outcome);
        Assert.Equal(OwnerLoginHostState.Failed, await StoredStateAsync(org.Id));
        _registrar.VerifyNoOtherCalls();
    }

    [Fact]
    public void Retry_IsRateLimitedPerSignedInPerson_FiveEveryTenMinutes()
    {
        // The retry reads and may write the Auth0 tenant that every host shares, under a lock that holds a database connection: a loop of retries must not reach it.
        var retry = typeof(OrgDomainController).GetMethod(nameof(OrgDomainController.RetryOwnerLogin))!;
        Assert.Equal(RateLimitPolicies.OwnerLoginRetry, retry.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        var policy = Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.OwnerLoginRetry);
        Assert.Equal((true, false), (policy.PartitionByUser, policy.PartitionByToken));
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(policy, new ConfigurationBuilder().Build());
        Assert.Equal((5, TimeSpan.FromMinutes(10), 0), (options.PermitLimit, options.Window, options.QueueLimit));
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private void GivenTheRegistrarAnswers(Auth0HostRegistrationResult result) =>
        _registrar.Setup(r => r.RegisterAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(result);

    private OwnerLoginHostSync Sync(params string[] flags) =>
        new(_db, _registrar.Object, new TestFeatureFlags(flags), _push.Object, _resolver.Object, _clock, NullLogger<OwnerLoginHostSync>.Instance);

    private OrgDomainService DomainService(OwnerLoginHostSync sync)
    {
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.CanUseCustomDomainAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<OrgEntity>())).Returns(PlanTier.Pro);
        var hostOptions = Options.Create(new PublicHostOptions { BaseDomain = "sites.example.test" });
        var links = EmailTestHelpers.Links();

        return new OrgDomainService(
            _db,
            entitlements.Object,
            Mock.Of<IDomainVerificationService>(),
            _resolver.Object,
            hostOptions,
            links,
            new PublicOrgSiteUrls(links, hostOptions, entitlements.Object),
            Mock.Of<IVercelDomainsClient>(),
            _clock,
            new TestFeatureFlags(FeatureFlags.WhiteLabelHost, FeatureFlags.Auth0HostSync),
            sync);
    }

    private async Task<OwnerLoginHostState> StoredStateAsync(Guid orgId) =>
        (await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId)).OwnerLoginHostState;

    private async Task<OrgEntity> SeedAsync(
        OwnerLoginHostState state,
        DomainVerificationStatus domainStatus = DomainVerificationStatus.Verified)
    {
        var org = new OrgEntity
        {
            Name = "Agenzia",
            DisplayName = "Agenzia",
            Slug = $"agenzia-{Guid.NewGuid():N}"[..20],
            ContactEmail = "info@agenzia.example",
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = Domain,
            DomainVerificationStatus = domainStatus,
            DomainVerificationToken = "verify-token",
            DomainVerifiedAt = domainStatus == DomainVerificationStatus.Verified ? _clock.GetUtcNow().UtcDateTime : null,
            PlanTier = PlanTier.Pro,
            OwnerPortalOnOwnDomain = true,
            OwnerLoginHostState = state,
        };
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        return org;
    }
}

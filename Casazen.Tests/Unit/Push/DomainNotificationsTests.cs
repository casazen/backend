using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>
/// E40/F01/US02/T01: the live notices of an agency's own domain (<c>domain-verified</c>, <c>owner-login-ready</c>, <c>owner-login-failed</c>) reach the bell of the
/// administrators of THAT org only (<see cref="PushAudienceKind.OrgAdmins"/>), through the same job that writes every bell entry, and nobody at all while
/// <c>WhiteLabelHost</c> is off. The check of the domain is the real one (DNS and Vercel are doubles), the push queue a recorder.
/// </summary>
public sealed class DomainNotificationsTests : IDisposable
{
    private const string Domain = "prenota.agenzia-esempio.it";
    private const string Token = "verify-token";
    private const string OwnerA = "auth0|agency-a-owner";
    private const string AdminA = "auth0|agency-a-admin";
    private const string CollaboratorA = "auth0|agency-a-collaborator";
    private const string FormerAdminA = "auth0|agency-a-former-admin";
    private const string OwnerB = "auth0|agency-b-owner";

    private static readonly DateTime Now = new(2026, 11, 20, 9, 30, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly List<(string Key, PushAudience Audience, PushNotificationPayload Payload)> _pushes = [];
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly Mock<IAuth0HostRegistrar> _registrar = new(MockBehavior.Strict);
    private readonly Mock<IDnsTxtLookup> _txt = new();
    private readonly Mock<IDnsRecordLookup> _dns = new();
    private readonly Mock<IVercelDomainsClient> _vercel = new();
    private readonly PublicHostOptions _options = new()
    {
        TxtRecordPrefix = "_casazen-challenge",
        DnsLookupTimeoutSeconds = 5,
        VercelCnameTarget = "cname.vercel-dns.com",
        AcceptedCnameSuffixes = [".vercel-dns.com"],
        VercelAddresses = ["76.76.21.21"],
        FailuresBeforeDemotion = 2,
    };

    public DomainNotificationsTests()
    {
        _push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Callback<string, PushAudience, PushNotificationPayload>((key, audience, payload) => _pushes.Add((key, audience, payload)))
            .Returns(false);

        // The agency did everything right: its TXT, its CNAME, and Vercel has the domain verified.
        _txt.Setup(t => t.LookupTxtAsync($"_casazen-challenge.{Domain}", It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)[Token]);
        _dns.Setup(d => d.LookupCnameAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)["cname.vercel-dns.com"]);
        _dns.Setup(d => d.LookupAddressesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)[]);
        _vercel.SetupGet(v => v.IsConfigured).Returns(true);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Ok, new VercelDomain(Domain, true, [])));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DomainVerified_NotifiesOnlyThatOrgsAdministrators()
    {
        var (agency, other) = await SeedAsync();
        var flags = new TestFeatureFlags(FeatureFlags.WhiteLabelHost);

        var result = await Verification(flags).VerifyAsync(agency);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        var (key, audience, payload) = Assert.Single(_pushes);
        Assert.Equal(PushDeliveryKeys.DomainVerified(agency.Id, Now), key);
        Assert.Equal(PushAudience.OrgAdmins(agency.Id), audience);
        Assert.Equal(PushTypes.DomainVerified, payload.Type);
        Assert.DoesNotContain(Domain, payload.Title + payload.Body, StringComparison.OrdinalIgnoreCase);

        // The bell: the job the decorator queues next to the push, with what the push carried.
        var publisher = new RecordingPublisher();
        await new InAppNotificationJob(_db, _clock, publisher, NullLogger<InAppNotificationJob>.Instance)
            .CreateAsync(key, QueuedInAppNotification.From(audience, payload, Now), CancellationToken.None);

        var rows = await _db.InAppNotifications.AsNoTracking().OrderBy(n => n.UserId).ToListAsync();
        Assert.Equal([AdminA, OwnerA], rows.Select(r => r.UserId));
        Assert.All(rows, row =>
        {
            Assert.Equal(agency.Id, row.OrgId);
            Assert.Equal(agency.Id, row.EntityId);
            Assert.Equal(PushTypes.DomainVerified, row.Type);
        });
        Assert.DoesNotContain(rows, row => row.OrgId == other.Id);
        Assert.Equal([AdminA, OwnerA], publisher.Users.Order(StringComparer.Ordinal));

        // A later check of the verified domain tells nobody again, and Auth0 is never asked while Auth0HostSync is off.
        _clock.Advance(TimeSpan.FromDays(1));
        await Verification(flags).VerifyAsync(agency);
        Assert.Single(_pushes);
        _registrar.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DomainVerified_WhiteLabelHostOff_TellsNobody_AndAsksAuth0Nothing()
    {
        var (agency, _) = await SeedAsync();

        // Auth0HostSync alone does nothing: the registration needs WhiteLabelHost too.
        var result = await Verification(new TestFeatureFlags(FeatureFlags.Auth0HostSync)).VerifyAsync(agency);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        Assert.Empty(_pushes);
        _registrar.VerifyNoOtherCalls();
        Assert.Equal(OwnerLoginHostState.NotRequested, (await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == agency.Id)).OwnerLoginHostState);
    }

    [Theory]
    [InlineData(Auth0HostRegistrationResult.Registered, OwnerLoginHostState.Ready, PushTypes.OwnerLoginReady)]
    [InlineData(Auth0HostRegistrationResult.Unavailable, OwnerLoginHostState.Failed, PushTypes.OwnerLoginFailed)]
    public async Task OwnerLoginOutcome_NotifiesOnlyThatOrgsAdministrators(
        Auth0HostRegistrationResult registration,
        OwnerLoginHostState expectedState,
        string expectedType)
    {
        var (agency, other) = await SeedAsync();
        _registrar.Setup(r => r.RegisterAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(registration);

        await Verification(new TestFeatureFlags(FeatureFlags.WhiteLabelHost, FeatureFlags.Auth0HostSync)).VerifyAsync(agency);

        Assert.Equal(expectedState, (await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == agency.Id)).OwnerLoginHostState);
        Assert.Equal([PushTypes.DomainVerified, expectedType], _pushes.Select(p => p.Payload.Type));
        var (key, audience, payload) = _pushes[1];
        Assert.Equal(PushAudience.OrgAdmins(agency.Id), audience);

        await new InAppNotificationJob(_db, _clock, new RecordingPublisher(), NullLogger<InAppNotificationJob>.Instance)
            .CreateAsync(key, QueuedInAppNotification.From(audience, payload, Now), CancellationToken.None);

        var rows = await _db.InAppNotifications.AsNoTracking().OrderBy(n => n.UserId).ToListAsync();
        Assert.Equal([AdminA, OwnerA], rows.Select(r => r.UserId));
        Assert.All(rows, row => Assert.Equal((agency.Id, (Guid?)agency.Id, expectedType), (row.OrgId, row.EntityId, row.Type)));
        Assert.DoesNotContain(rows, row => row.OrgId == other.Id);
        _registrar.Verify(r => r.RegisterAsync(Domain, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void DeliveryKeys_AreStablePerEvent_AndTheSameInEveryCulture()
    {
        var orgId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var instant = new DateTime(2026, 11, 20, 9, 30, 15, 123, DateTimeKind.Utc);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");

            Assert.Equal("domain-verified:0f8fad5bd9cb469fa16570867728950e:20261120093015123", PushDeliveryKeys.DomainVerified(orgId, instant));
            Assert.Equal("owner-login:0f8fad5bd9cb469fa16570867728950e:ready:20261120093015123", PushDeliveryKeys.OwnerLoginReady(orgId, instant));
            Assert.Equal("owner-login:0f8fad5bd9cb469fa16570867728950e:failed:20261120093015123", PushDeliveryKeys.OwnerLoginFailed(orgId, instant));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private DomainVerificationService Verification(TestFeatureFlags flags) =>
        new(
            _db,
            _txt.Object,
            _dns.Object,
            _vercel.Object,
            Mock.Of<IPublicHostResolver>(),
            Options.Create(_options),
            _clock,
            NullLogger<DomainVerificationService>.Instance,
            new OwnerLoginHostSync(
                _db,
                _registrar.Object,
                flags,
                _push.Object,
                Mock.Of<IPublicHostResolver>(),
                _clock,
                NullLogger<OwnerLoginHostSync>.Instance));

    /// <summary>
    /// Two agencies: A (the custom domain, pending) with an owner, an administrator, a collaborator and an administrator the org deactivated; B with its own
    /// owner. Only the owner and the administrator of A may open A's domain settings.
    /// </summary>
    private async Task<(OrgEntity Agency, OrgEntity Other)> SeedAsync()
    {
        var agency = new OrgEntity
        {
            Name = "Agenzia A",
            DisplayName = "Agenzia A",
            Slug = "agenzia-a",
            ContactEmail = "info@agenzia-a.example",
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = Domain,
            DomainVerificationToken = Token,
            DomainVerificationStatus = DomainVerificationStatus.Pending,
            PlanTier = PlanTier.Pro,
        };
        var other = new OrgEntity { Name = "Agenzia B", DisplayName = "Agenzia B", Slug = "agenzia-b", ContactEmail = "info@agenzia-b.example" };
        _db.Orgs.AddRange(agency, other);
        _db.Users.AddRange(
            NewUser(OwnerA, agency.Id, UserRole.PropertyOwner),
            NewUser(AdminA, agency.Id, UserRole.None),
            NewUser(CollaboratorA, agency.Id, UserRole.None),
            NewUser(FormerAdminA, agency.Id, UserRole.None),
            NewUser(OwnerB, other.Id, UserRole.PropertyOwner));
        _db.OrgMembers.AddRange(
            NewMember(OwnerA, agency.Id, OrgRole.Owner),
            NewMember(AdminA, agency.Id, OrgRole.Admin),
            NewMember(CollaboratorA, agency.Id, OrgRole.Collaborator),
            NewMember(FormerAdminA, agency.Id, OrgRole.Admin, OrgMemberStatus.Deactivated),
            NewMember(OwnerB, other.Id, OrgRole.Owner));
        await _db.SaveChangesAsync();
        return (agency, other);
    }

    private static User NewUser(string id, Guid orgId, UserRole role) =>
        new() { Id = id, Email = $"{id.Replace("|", "-")}@example.com", OrgId = orgId, Role = role, IsActive = true };

    private static OrgMember NewMember(string userId, Guid orgId, OrgRole role, OrgMemberStatus status = OrgMemberStatus.Active) =>
        new() { UserId = userId, OrgId = orgId, Role = role, Status = status };

    private sealed class RecordingPublisher : IInAppNotificationPublisher
    {
        public List<string> Users { get; } = [];

        public Task PublishAsync(string userId, string? type, Guid? entityId, CancellationToken cancellationToken = default)
        {
            Users.Add(userId);
            return Task.CompletedTask;
        }
    }
}

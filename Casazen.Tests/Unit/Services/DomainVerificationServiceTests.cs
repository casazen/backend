using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The end-to-end check of a custom domain (BK-17, A3-25): ownership TXT, DNS towards Vercel, the domain on the Vercel
/// project. The DNS lookups and the Vercel API are doubles: nothing here touches the network.
/// </summary>
public class DomainVerificationServiceTests
{
    private const string Domain = "www.example.it";
    private const string Token = "token-abc";
    private const string TxtHost = "_casazen-challenge.www.example.it";

    private readonly Mock<IDnsTxtLookup> _txt = new();
    private readonly Mock<IDnsRecordLookup> _dns = new();
    private readonly Mock<IVercelDomainsClient> _vercel = new();
    private readonly Mock<IPublicHostResolver> _resolver = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly PublicHostOptions _options = new()
    {
        TxtRecordPrefix = "_casazen-challenge",
        DnsLookupTimeoutSeconds = 5,
        VercelCnameTarget = "cname.vercel-dns.com",
        AcceptedCnameSuffixes = [".vercel-dns.com"],
        VercelAddresses = ["76.76.21.21"],
        FailuresBeforeDemotion = 2,
    };

    public DomainVerificationServiceTests()
    {
        _vercel.SetupGet(v => v.IsConfigured).Returns(true);
        // By default the host did everything right and Vercel has the domain verified.
        GivenTxt(Token);
        GivenCname("cname.vercel-dns.com");
        _dns.Setup(d => d.LookupAddressesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        GivenVercelHasDomain(verified: true);
    }

    // ─── Verified: only when everything is in place ────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_TxtCnameAndVercelAllRight_SetsVerifiedAndInvalidatesTheHostCache()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        Assert.Null(result.Detail);
        var saved = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(DomainVerificationStatus.Verified, saved.DomainVerificationStatus);
        Assert.Null(saved.DomainStatusDetail);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, saved.DomainVerifiedAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, saved.DomainCheckedAt);
        Assert.NotNull(saved.DomainVercelAddedAt);
        _resolver.Verify(r => r.InvalidateCacheForHost(Domain), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_OnlyTheTxtIsRight_IsNotVerified()
    {
        // The old behavior: a TXT alone made the domain "Verified" while Vercel answered 404 (A3-25).
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenCname();

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(DomainIssues.DnsNotPointing, result.Detail);
        _vercel.Verify(v => v.AddDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_TxtMissing_IsPendingWithOwnershipCodeAndNothingElseIsChecked()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenTxt();

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(DomainIssues.OwnershipTxtMissing, result.Detail);
        _dns.Verify(d => d.LookupCnameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _vercel.Verify(v => v.GetDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _vercel.Verify(v => v.AddDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_TxtHasAnotherValue_IsPendingNotVerified()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenTxt("wrong-token");

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(DomainIssues.OwnershipTxtMissing, result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_TxtLookupTimesOut_IsAMissNotAnException()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _options.DnsLookupTimeoutSeconds = 1;
        _txt.Setup(t => t.LookupTxtAsync(TxtHost, It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return (IReadOnlyList<string>)[];
            });

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainIssues.OwnershipTxtMissing, result.Detail);
    }

    // ─── DNS towards Vercel ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_CnameUnderAnAcceptedSuffix_Points()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenCname("abc123.vercel-dns-017.com", "x.vercel-dns.com.");

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_CnameToALookAlike_DoesNotPoint()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenCname("cname.vercel-dns.com.evil.example", "evilvercel-dns.com", "notvercel-dns.com");

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainIssues.DnsNotPointing, result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_ARecordOfVercel_PointsForARootDomain()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenCname();
        _dns.Setup(d => d.LookupAddressesAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(["76.76.21.21"]);

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ARecordOfAnotherHost_DoesNotPoint()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        GivenCname();
        _dns.Setup(d => d.LookupAddressesAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(["203.0.113.7"]);

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainIssues.DnsNotPointing, result.Detail);
    }

    [Theory]
    [InlineData("cname.vercel-dns.com", true)]
    [InlineData("CNAME.Vercel-DNS.com.", true)]
    [InlineData("a1b2.vercel-dns.com", true)]
    [InlineData("vercel-dns.com", false)]
    [InlineData("evilvercel-dns.com", false)]
    [InlineData("vercel-dns.com.evil.example", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsVercelTarget_Matches_OnlyTheTargetOrADomainUnderTheSuffix(string target, bool expected)
    {
        Assert.Equal(expected, DomainVerificationService.IsVercelTarget(target, _options));
    }

    // ─── The Vercel project ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_VercelNotConfigured_IsPendingNotVerifiedAndMakesNoCall()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _vercel.SetupGet(v => v.IsConfigured).Returns(false);

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(DomainIssues.VercelNotConfigured, result.Detail);
        _vercel.Verify(v => v.GetDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_DomainNotOnTheProject_AddsItOnce()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.NotFound));
        _vercel.Setup(v => v.AddDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(verified: true));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        _vercel.Verify(v => v.AddDomainAsync(Domain, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_DomainAlreadyOnTheProjectAndAddAnswers400_ReadsItInsteadOfFailing()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _vercel.SetupSequence(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.NotFound))
            .ReturnsAsync(Ok(verified: true));
        _vercel.Setup(v => v.AddDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Rejected, HttpStatus: 400));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_VercelRefusesTheDomain_IsFailedWithRejectedCode()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.NotFound));
        _vercel.Setup(v => v.AddDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Rejected, HttpStatus: 400));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Failed, result.Status);
        Assert.Equal(DomainIssues.VercelRejected, result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_DomainBelongsToAnotherVercelProject_IsFailedInUse()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.NotFound));
        _vercel.Setup(v => v.AddDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Conflict, HttpStatus: 409));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Failed, result.Status);
        Assert.Equal(DomainIssues.VercelDomainInUse, result.Detail);
    }

    [Theory]
    [InlineData(VercelCallStatus.Unauthorized, DomainIssues.VercelUnauthorized)]
    [InlineData(VercelCallStatus.RateLimited, DomainIssues.VercelUnavailable)]
    [InlineData(VercelCallStatus.Unavailable, DomainIssues.VercelUnavailable)]
    public async Task VerifyAsync_VercelProblemOfThePlatform_IsPendingNeverTheHostsFault(VercelCallStatus call, string expected)
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(call));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(expected, result.Detail);
    }

    [Fact]
    public async Task VerifyAsync_VercelAsksItsOwnTxt_IsPendingWithTheRecordToAdd()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        var challenge = new VercelVerificationChallenge("TXT", "_vercel.example.it", "vc-domain-verify=www.example.it,abc", "pending_domain_verification");
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok(verified: false));
        _vercel.Setup(v => v.VerifyDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(
                VercelCallStatus.Ok,
                new VercelDomain(Domain, false, [challenge])));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(DomainIssues.VercelVerificationPending, result.Detail);
        Assert.Equal("_vercel.example.it", result.VercelTxtHost);
        Assert.Equal("vc-domain-verify=www.example.it,abc", result.VercelTxtValue);
        var saved = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal("_vercel.example.it", saved.DomainVercelTxtHost);
        Assert.NotNull(saved.DomainVercelAddedAt);
    }

    [Fact]
    public async Task VerifyAsync_VerifyCallAnswers400_StillPendingWithTheChallengeFromTheDomain()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        var challenge = new VercelVerificationChallenge("TXT", "_vercel.example.it", "vc-domain-verify=1", null);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Ok, new VercelDomain(Domain, false, [challenge])));
        _vercel.Setup(v => v.VerifyDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Rejected, HttpStatus: 400));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Pending, result.Status);
        Assert.Equal(DomainIssues.VercelVerificationPending, result.Detail);
        Assert.Equal("_vercel.example.it", result.VercelTxtHost);
        Assert.Equal("vc-domain-verify=1", result.VercelTxtValue);
    }

    [Fact]
    public async Task VerifyAsync_VercelChallengeGetsSatisfied_VerifiesAndClearsTheStoredChallenge()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db);
        org.DomainVercelTxtHost = "_vercel.example.it";
        org.DomainVercelTxtValue = "vc-domain-verify=1";
        await db.SaveChangesAsync();
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(verified: false));
        _vercel.Setup(v => v.VerifyDomainAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(verified: true));

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        var saved = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Null(saved.DomainVercelTxtHost);
        Assert.Null(saved.DomainVercelTxtValue);
    }

    // ─── Another org, and a verified domain that stops being right ─────────────────────────────────

    [Fact]
    public async Task VerifyAsync_AnotherOrgHasTheDomainVerified_IsFailedDomainTaken()
    {
        await using var db = CreateDb();
        var other = await SeedOrgAsync(db, slug: "other-org", status: DomainVerificationStatus.Verified);
        var org = await SeedOrgAsync(db, slug: "this-org");
        Assert.Equal(other.CustomDomain, org.CustomDomain);

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Failed, result.Status);
        Assert.Equal(DomainIssues.DomainTaken, result.Detail);
        _vercel.Verify(v => v.AddDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_VerifiedDomainLosesItsRecords_StaysVerifiedOnceThenIsDemoted()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, status: DomainVerificationStatus.Verified);
        org.DomainVerifiedAt = _clock.GetUtcNow().UtcDateTime.AddDays(-3);
        await db.SaveChangesAsync();
        GivenTxt(); // The host deleted the ownership record.
        var service = CreateService(db);

        var first = await service.VerifyAsync(org);
        Assert.Equal(DomainVerificationStatus.Verified, first.Status);
        Assert.Equal(1, org.DomainCheckFailures);
        _resolver.Verify(r => r.InvalidateCacheForHost(It.IsAny<string>()), Times.Never);

        _clock.Advance(TimeSpan.FromHours(24));
        var second = await service.VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Failed, second.Status);
        Assert.Equal(DomainIssues.OwnershipTxtMissing, second.Detail);
        var saved = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(DomainVerificationStatus.Failed, saved.DomainVerificationStatus);
        Assert.Null(saved.DomainVerifiedAt);
        // The host is no longer served: the cached answers of the host are dropped.
        _resolver.Verify(r => r.InvalidateCacheForHost(Domain), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_VerifiedDomainOneBadCheckThenGood_ResetsTheFailureCounter()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, status: DomainVerificationStatus.Verified);
        var service = CreateService(db);
        GivenCname();
        await service.VerifyAsync(org);
        Assert.Equal(1, org.DomainCheckFailures);

        GivenCname("cname.vercel-dns.com");
        var result = await service.VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        Assert.Equal(0, org.DomainCheckFailures);
    }

    [Fact]
    public async Task VerifyAsync_VerifiedDomainWhileVercelIsDown_StaysVerifiedAndCountsNoFailure()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, status: DomainVerificationStatus.Verified);
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<VercelDomain>(VercelCallStatus.Unavailable));
        var service = CreateService(db);

        for (var i = 0; i < 5; i++)
            await service.VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, org.DomainVerificationStatus);
        Assert.Equal(0, org.DomainCheckFailures);
    }

    [Fact]
    public async Task VerifyAsync_FailedDomainThatIsFixed_BecomesVerified()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, status: DomainVerificationStatus.Failed);
        org.DomainStatusDetail = DomainIssues.OwnershipTxtMissing;
        await db.SaveChangesAsync();

        var result = await CreateService(db).VerifyAsync(org);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        Assert.Null(org.DomainStatusDetail);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private void GivenTxt(params string[] values) =>
        _txt.Setup(t => t.LookupTxtAsync(TxtHost, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)values);

    private void GivenCname(params string[] targets) =>
        _dns.Setup(d => d.LookupCnameAsync(Domain, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)targets);

    private void GivenVercelHasDomain(bool verified) =>
        _vercel.Setup(v => v.GetDomainAsync(Domain, It.IsAny<CancellationToken>())).ReturnsAsync(Ok(verified));

    private static VercelCallResult<VercelDomain> Ok(bool verified) =>
        new(VercelCallStatus.Ok, new VercelDomain(Domain, verified, []));

    private DomainVerificationService CreateService(AppDbContext db) =>
        new(db, _txt.Object, _dns.Object, _vercel.Object, _resolver.Object, Options.Create(_options), _clock,
            NullLogger<DomainVerificationService>.Instance);

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<OrgEntity> SeedOrgAsync(
        AppDbContext db,
        DomainVerificationStatus status = DomainVerificationStatus.Pending,
        string slug = "test-org",
        string customDomain = Domain)
    {
        var org = new OrgEntity
        {
            Name = "Test Org",
            DisplayName = "Test Org",
            Slug = slug,
            ContactEmail = "test@example.com",
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = customDomain,
            DomainVerificationToken = Token,
            DomainVerificationStatus = status,
            PlanTier = PlanTier.Pro,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }
}

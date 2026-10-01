using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The periodic job of the custom domains (BK-17, A3-25): who is due, who is skipped, isolation, removals.</summary>
public class DomainRecheckServiceTests
{
    private readonly Mock<IDomainVerificationService> _verification = new();
    private readonly Mock<IEntitlementService> _entitlements = new();
    private readonly Mock<IVercelDomainsClient> _vercel = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly PublicHostOptions _options = new()
    {
        RecheckPendingMinutes = 30,
        RecheckVerifiedHours = 24,
        MaxPendingDays = 14,
        RecheckBatchSize = 50,
    };

    public DomainRecheckServiceTests()
    {
        _entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<Org>())).Returns(PlanTier.Pro);
        _vercel.SetupGet(v => v.IsConfigured).Returns(true);
        _verification.Setup(v => v.VerifyAsync(It.IsAny<Org>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Org org, CancellationToken _) => new DomainVerificationResult(
                org.DomainVerificationStatus, org.CustomDomain!, _clock.GetUtcNow().UtcDateTime, org.DomainStatusDetail));
    }

    // ─── Who is due ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PendingDomainNeverChecked_IsChecked()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "a.example.it", DomainVerificationStatus.Pending, checkedAt: null);

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Checked);
        _verification.Verify(v => v.VerifyAsync(It.Is<Org>(o => o.CustomDomain == "a.example.it"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(29, false)]
    [InlineData(31, true)]
    [InlineData(600, true)]
    public async Task RunAsync_PendingDomain_IsCheckedEveryRecheckPendingMinutes(int minutesSinceLastCheck, bool due)
    {
        await using var db = CreateDb();
        await SeedAsync(db, "a.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(minutes: minutesSinceLastCheck));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(due ? 1 : 0, summary.Checked);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(25, true)]
    public async Task RunAsync_VerifiedDomain_IsCheckedEveryRecheckVerifiedHours(int hoursSinceLastCheck, bool due)
    {
        await using var db = CreateDb();
        await SeedAsync(db, "a.example.it", DomainVerificationStatus.Verified, checkedAt: Ago(hours: hoursSinceLastCheck));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(due ? 1 : 0, summary.Checked);
    }

    [Fact]
    public async Task RunAsync_FailedDomain_IsCheckedAgainLikeAPendingOne()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "a.example.it", DomainVerificationStatus.Failed, checkedAt: Ago(hours: 2));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Checked);
    }

    [Fact]
    public async Task RunAsync_PendingDomainConfiguredLongAgo_IsGivenUpOn()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "old.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(hours: 5), configuredAt: Ago(days: 15));
        await SeedAsync(db, "new.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(hours: 5), configuredAt: Ago(days: 13));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Checked);
        _verification.Verify(v => v.VerifyAsync(It.Is<Org>(o => o.CustomDomain == "old.example.it"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_VerifiedDomainConfiguredLongAgo_IsStillChecked()
    {
        // The give-up applies to a domain that never worked, not to one that is live.
        await using var db = CreateDb();
        await SeedAsync(db, "live.example.it", DomainVerificationStatus.Verified, checkedAt: Ago(hours: 30), configuredAt: Ago(days: 200));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Checked);
    }

    [Fact]
    public async Task RunAsync_InactiveNotCustomDomainOrWithoutToken_AreSkipped()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "inactive.example.it", DomainVerificationStatus.Pending, isActive: false);
        await SeedAsync(db, "path.example.it", DomainVerificationStatus.Pending, mode: PublicHostMode.CasazenPath);
        await SeedAsync(db, "notoken.example.it", DomainVerificationStatus.Pending, token: null);

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(0, summary.Checked);
        _verification.Verify(v => v.VerifyAsync(It.IsAny<Org>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_OrgWhosePlanLapsed_IsNotChecked()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "lapsed.example.it", DomainVerificationStatus.Verified, checkedAt: Ago(hours: 30));
        _entitlements.Setup(e => e.ResolveEffectiveTier(It.IsAny<Org>())).Returns(PlanTier.Starter);

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(0, summary.Checked);
        _verification.Verify(v => v.VerifyAsync(It.IsAny<Org>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_MoreDueThanTheBatch_ChecksTheLongestUncheckedFirst()
    {
        await using var db = CreateDb();
        _options.RecheckBatchSize = 2;
        await SeedAsync(db, "c.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(hours: 2));
        await SeedAsync(db, "a.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(hours: 9));
        await SeedAsync(db, "b.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(hours: 5));
        await SeedAsync(db, "never.example.it", DomainVerificationStatus.Pending, checkedAt: null);

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(2, summary.Checked);
        // Null (never checked) sorts first, then the oldest.
        _verification.Verify(v => v.VerifyAsync(It.Is<Org>(o => o.CustomDomain == "never.example.it"), It.IsAny<CancellationToken>()), Times.Once);
        _verification.Verify(v => v.VerifyAsync(It.Is<Org>(o => o.CustomDomain == "a.example.it"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Outcomes and isolation ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CountsNewlyVerifiedAndNoLongerVerified()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "up.example.it", DomainVerificationStatus.Pending);
        await SeedAsync(db, "down.example.it", DomainVerificationStatus.Verified, checkedAt: Ago(hours: 30));
        await SeedAsync(db, "same.example.it", DomainVerificationStatus.Pending);
        _verification.Setup(v => v.VerifyAsync(It.IsAny<Org>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Org org, CancellationToken _) => new DomainVerificationResult(
                org.CustomDomain switch
                {
                    "up.example.it" => DomainVerificationStatus.Verified,
                    "down.example.it" => DomainVerificationStatus.Failed,
                    _ => DomainVerificationStatus.Pending,
                },
                org.CustomDomain!,
                _clock.GetUtcNow().UtcDateTime,
                null));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(3, summary.Checked);
        Assert.Equal(1, summary.NowVerified);
        Assert.Equal(1, summary.NoLongerVerified);
    }

    [Fact]
    public async Task RunAsync_OneOrgThrows_TheOthersAreStillChecked()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "boom.example.it", DomainVerificationStatus.Pending, checkedAt: null);
        await SeedAsync(db, "fine.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(hours: 4));
        _verification.Setup(v => v.VerifyAsync(It.Is<Org>(o => o.CustomDomain == "boom.example.it"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dns exploded"));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Checked);
        _verification.Verify(v => v.VerifyAsync(It.Is<Org>(o => o.CustomDomain == "fine.example.it"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_CallerCancels_Propagates()
    {
        await using var db = CreateDb();
        await SeedAsync(db, "a.example.it", DomainVerificationStatus.Pending);
        _verification.Setup(v => v.VerifyAsync(It.IsAny<Org>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(db).RunAsync());
    }

    // ─── Removals from the Vercel project ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(VercelCallStatus.Ok)]
    [InlineData(VercelCallStatus.NotFound)]
    public async Task RunAsync_PendingRemoval_RemovesTheDomainAndTheRow(VercelCallStatus answer)
    {
        await using var db = CreateDb();
        db.PendingDomainRemovals.Add(new PendingDomainRemoval { Domain = "gone.example.it", RequestedAt = Ago(hours: 1) });
        await db.SaveChangesAsync();
        _vercel.Setup(v => v.RemoveDomainAsync("gone.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<bool>(answer, answer == VercelCallStatus.Ok));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Removed);
        Assert.Empty(await db.PendingDomainRemovals.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_RemovalFails_KeepsTheRowWithAttemptsAndAStableCode()
    {
        await using var db = CreateDb();
        db.PendingDomainRemovals.Add(new PendingDomainRemoval { Domain = "gone.example.it", RequestedAt = Ago(hours: 1) });
        await db.SaveChangesAsync();
        _vercel.Setup(v => v.RemoveDomainAsync("gone.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<bool>(VercelCallStatus.Unavailable));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(0, summary.Removed);
        Assert.Equal(1, summary.RemovalsFailed);
        var row = await db.PendingDomainRemovals.SingleAsync();
        Assert.Equal(1, row.Attempts);
        Assert.Equal("Unavailable", row.LastError);
        Assert.NotNull(row.LastAttemptAt);
    }

    [Fact]
    public async Task RunAsync_RemovalOfADomainAnotherOrgNowUses_DropsTheRowWithoutCallingVercel()
    {
        await using var db = CreateDb();
        db.PendingDomainRemovals.Add(new PendingDomainRemoval { Domain = "shared.example.it", RequestedAt = Ago(hours: 1) });
        await SeedAsync(db, "shared.example.it", DomainVerificationStatus.Pending, checkedAt: Ago(minutes: 1));
        await db.SaveChangesAsync();

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(0, summary.Removed);
        Assert.Empty(await db.PendingDomainRemovals.ToListAsync());
        _vercel.Verify(v => v.RemoveDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_RemovalOfTheWebAppsOwnDomain_IsDroppedAndNeverSentToVercel()
    {
        // Whatever ends up in the queue, the app is never taken off its own Vercel project.
        await using var db = CreateDb();
        db.PendingDomainRemovals.Add(new PendingDomainRemoval { Domain = "public.example.test", RequestedAt = Ago(hours: 1) });
        await db.SaveChangesAsync();

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(0, summary.Removed);
        Assert.Empty(await db.PendingDomainRemovals.ToListAsync());
        _vercel.Verify(v => v.RemoveDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_VercelNotConfigured_MakesNoRemovalCallAndKeepsTheRows()
    {
        await using var db = CreateDb();
        db.PendingDomainRemovals.Add(new PendingDomainRemoval { Domain = "gone.example.it", RequestedAt = Ago(hours: 1) });
        await db.SaveChangesAsync();
        _vercel.SetupGet(v => v.IsConfigured).Returns(false);

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(0, summary.Removed);
        Assert.Single(await db.PendingDomainRemovals.ToListAsync());
        _vercel.Verify(v => v.RemoveDomainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_DeactivatedOrgThatWasOnVercel_LeavesTheProject()
    {
        await using var db = CreateDb();
        var org = await SeedAsync(db, "left.example.it", DomainVerificationStatus.Verified, isActive: false, checkedAt: Ago(minutes: 1));
        org.DomainVercelAddedAt = Ago(days: 3);
        await db.SaveChangesAsync();
        _vercel.Setup(v => v.RemoveDomainAsync("left.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VercelCallResult<bool>(VercelCallStatus.Ok, true));

        var summary = await CreateService(db).RunAsync();

        Assert.Equal(1, summary.Removed);
        Assert.Null((await db.Orgs.SingleAsync(o => o.Id == org.Id)).DomainVercelAddedAt);
    }

    [Fact]
    public async Task EnqueueAsync_SameDomainTwice_QueuesItOnce()
    {
        await using var db = CreateDb();

        await DomainRemovalQueue.EnqueueAsync(db, "gone.example.it", _clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await db.SaveChangesAsync();
        await DomainRemovalQueue.EnqueueAsync(db, "gone.example.it", _clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Single(await db.PendingDomainRemovals.ToListAsync());
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private DateTime Ago(int days = 0, int hours = 0, int minutes = 0) =>
        _clock.GetUtcNow().UtcDateTime - new TimeSpan(days, hours, minutes, 0);

    private DomainRecheckService CreateService(AppDbContext db) =>
        new(db, _verification.Object, _entitlements.Object, _vercel.Object,
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://public.example.test" })),
            Options.Create(_options), _clock, NullLogger<DomainRecheckService>.Instance);

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private async Task<OrgEntity> SeedAsync(
        AppDbContext db,
        string domain,
        DomainVerificationStatus status,
        DateTime? checkedAt = null,
        DateTime? configuredAt = null,
        bool isActive = true,
        PublicHostMode mode = PublicHostMode.CustomDomain,
        string? token = "token")
    {
        var org = new OrgEntity
        {
            Name = domain,
            DisplayName = domain,
            Slug = domain.Replace('.', '-'),
            ContactEmail = "test@example.com",
            PublicHostMode = mode,
            CustomDomain = domain,
            DomainVerificationToken = token,
            DomainVerificationStatus = status,
            DomainCheckedAt = checkedAt,
            DomainConfiguredAt = configuredAt ?? Ago(days: 1),
            PlanTier = PlanTier.Pro,
            IsActive = isActive,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }
}

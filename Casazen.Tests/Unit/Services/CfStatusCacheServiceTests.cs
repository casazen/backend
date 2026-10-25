using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-008-R7 through ADR-008-R10: CF status cache service with tenant isolation, request coalescing, and stale retention.
/// CF-FIX2: only the lease of the caller's org is read (404 before the cache and before ConcordatoFacile), a stale snapshot is not
/// refreshed before 5 minutes, and the lookup of a practice is kept 15 minutes.
/// </summary>
public class CfStatusCacheServiceTests
{
    private const string PracticeId = "P-0123456789abcdef";

    private readonly Mock<ConcordatoFacileApiClient> _apiClientMock;
    private readonly IMemoryCache _cache;
    private readonly Mock<IHostEnvironment> _environmentMock;

    public CfStatusCacheServiceTests()
    {
        _apiClientMock = new Mock<ConcordatoFacileApiClient>(null!, null!);

        // The key is configured unless a test says it is not (CF-BE).
        _apiClientMock.SetupGet(c => c.IsConfigured).Returns(true);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _environmentMock = new Mock<IHostEnvironment>();
        _environmentMock.Setup(e => e.EnvironmentName).Returns("Testing");
    }

    /// <summary>
    /// CF-BE, the PROOF test of the status without a key (the case of the test and production environments until ConcordatoFacile delivers
    /// the key): the answer is not an error but "not available" (no practices, stale, never checked, the next read in five minutes),
    /// ConcordatoFacile is not asked, not even at the following opening of the page, and the log has ONE warning, not an error, that names
    /// the setting and no value.
    /// </summary>
    [Fact]
    public async Task GetPractices_NoStatusApiKey_IsNotAvailable_CfIsNeverAsked_AndTheLogHasOneWarning()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        _apiClientMock.SetupGet(c => c.IsConfigured).Returns(false);
        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, orgId, leaseId);
        var log = new CapturingLogger<ConcordatoFacileStatusCacheService>();
        var service = CreateService(db, log);

        var first = await service.GetPracticesAsync(leaseId, orgId);
        var second = await service.GetPracticesAsync(leaseId, orgId);

        foreach (var result in new[] { first, second })
        {
            Assert.False(result.IsAvailable);
            Assert.True(result.IsStale);
            Assert.Empty(result.Practices);
            Assert.Null(result.LastCheckedAt);
            Assert.InRange(result.NextUpdateAt - DateTime.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        }

        _apiClientMock.Verify(c => c.GetPracticesByLeaseIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("ConcordatoFacile:StatusApiKey not configured", warning.Message);
    }

    /// <summary>
    /// CF-BE (closes CF-FIX2-FU1): a first read that fails, with no snapshot to fall back on, is not a 500 and is not tried again at every
    /// opening: the outcome "not available" is kept five minutes under the same key, so ConcordatoFacile (and its three attempts) is asked once.
    /// </summary>
    [Fact]
    public async Task GetPractices_FirstReadFailsWithNoSnapshot_IsNotAvailable_AndCfIsAskedOnceInFiveMinutes()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, orgId, leaseId);
        _apiClientMock.Setup(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("CF API unavailable"));
        var service = CreateService(db);

        var first = await service.GetPracticesAsync(leaseId, orgId);
        var second = await service.GetPracticesAsync(leaseId, orgId);

        Assert.False(first.IsAvailable);
        Assert.False(second.IsAvailable);
        Assert.True(second.IsStale);
        Assert.Empty(second.Practices);
        Assert.Null(second.LastCheckedAt);
        _apiClientMock.Verify(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()), Times.Once);

        // Five minutes later the memory is gone and ConcordatoFacile is asked again, and a good answer is available again.
        _cache.Set($"Testing:{orgId}:{leaseId}", ExpiredNotAvailable(orgId), new MemoryCacheEntryOptions());
        _apiClientMock.Setup(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PracticeDto>
            {
                new() { PracticeId = PracticeId, Status = CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
            });

        var recovered = await service.GetPracticesAsync(leaseId, orgId);

        Assert.True(recovered.IsAvailable);
        Assert.False(recovered.IsStale);
        Assert.Single(recovered.Practices);
        Assert.NotNull(recovered.LastCheckedAt);
    }

    [Fact]
    public async Task GetPractice_NoStatusApiKey_IsNull_AndCfIsNotAsked()
    {
        _apiClientMock.SetupGet(c => c.IsConfigured).Returns(false);
        await using var db = await CreateInMemoryDbAsync();
        var service = CreateService(db);

        var practice = await service.GetPracticeAsync(PracticeId);

        Assert.Null(practice);
        _apiClientMock.Verify(c => c.GetPracticeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// ADR-008-R10 and ADR-008-R11, the PROOF test of the data rule (tenant isolation): a lease that is another org's, or does not exist,
    /// is a 404 <c>lease_not_found</c> before the cache is read and before ConcordatoFacile is asked, even when the owner's snapshot of
    /// that very lease is warm in the cache.
    /// </summary>
    [Fact]
    public async Task GetPractices_LeaseOfAnotherOrgOrMissing_Is404_AndCfIsNotAsked()
    {
        var leaseId = Guid.NewGuid();
        var ownerOrgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var practices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, ownerOrgId, leaseId);
        _apiClientMock.Setup(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(practices);

        var service = CreateService(db);

        // The owner reads its lease: the snapshot is now in the cache
        await service.GetPracticesAsync(leaseId, ownerOrgId);
        _apiClientMock.Invocations.Clear();

        var otherOrg = await Assert.ThrowsAsync<NotFoundException>(() => service.GetPracticesAsync(leaseId, otherOrgId));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => service.GetPracticesAsync(Guid.NewGuid(), ownerOrgId));

        Assert.Equal("lease_not_found", otherOrg.Code);
        Assert.Equal("lease_not_found", missing.Code);
        _apiClientMock.Verify(c => c.GetPracticesByLeaseIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPractices_ValidCache_ReturnsFromCache()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var practices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, orgId, leaseId);
        _apiClientMock.Setup(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(practices);

        var service = CreateService(db);

        // First call caches
        await service.GetPracticesAsync(leaseId, orgId);

        // Second call should use cache
        _apiClientMock.Invocations.Clear();
        var result = await service.GetPracticesAsync(leaseId, orgId);

        // Verify the API was NOT called again (cache was used)
        _apiClientMock.Verify(c => c.GetPracticesByLeaseIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(result.Practices);
        Assert.False(result.IsStale);
    }

    [Fact]
    public async Task GetPractices_ApiError_RetainsStaleSnapshot()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var practices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, orgId, leaseId);
        _apiClientMock.SetupSequence(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(practices)
            .ThrowsAsync(new HttpRequestException("CF API unavailable"));

        var service = CreateService(db);

        // First call succeeds and caches
        var firstResult = await service.GetPracticesAsync(leaseId, orgId);
        Assert.False(firstResult.IsStale);

        // Manually insert expired snapshot to trigger refresh
        _cache.Set($"Testing:{orgId}:{leaseId}", ExpiredSnapshot(orgId, practices), new MemoryCacheEntryOptions());

        // Second call: cache entry exists but expired, API fails
        var secondResult = await service.GetPracticesAsync(leaseId, orgId);

        Assert.True(secondResult.IsStale);
        Assert.Single(secondResult.Practices);
        Assert.NotNull(secondResult.LastError);
        Assert.Contains("CF API unavailable", secondResult.LastError);
    }

    /// <summary>
    /// CF-FIX2: after a failed read the stale snapshot is served for 5 minutes (<c>NextUpdateAt</c> says when the next read is), instead of
    /// asking ConcordatoFacile again at every opening of the page because its old expiry is in the past.
    /// </summary>
    [Fact]
    public async Task GetPractices_AfterAnError_DoesNotAskCfAgainBeforeFiveMinutes()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var practices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, orgId, leaseId);
        _apiClientMock.Setup(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("CF API unavailable"));
        _cache.Set($"Testing:{orgId}:{leaseId}", ExpiredSnapshot(orgId, practices), new MemoryCacheEntryOptions());

        var service = CreateService(db);

        var first = await service.GetPracticesAsync(leaseId, orgId);
        var second = await service.GetPracticesAsync(leaseId, orgId);

        Assert.True(first.IsStale);
        Assert.True(second.IsStale);
        Assert.Single(second.Practices);
        _apiClientMock.Verify(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.InRange(second.NextUpdateAt - DateTime.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task GetPractices_OlderStatus_KeepsCachedData()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var olderTime = DateTime.UtcNow.AddHours(-2);
        var newerTime = DateTime.UtcNow.AddHours(-1);

        var newerPractices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = CfPracticeStatus.InProgress, StatusChangedAt = newerTime }
        };
        var olderPractices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = CfPracticeStatus.Received, StatusChangedAt = olderTime }
        };

        await using var db = await CreateInMemoryDbAsync();
        await SeedLeaseAsync(db, orgId, leaseId);
        _apiClientMock.SetupSequence(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newerPractices)
            .ReturnsAsync(olderPractices);

        var service = CreateService(db);

        // First call caches newer data
        var firstResult = await service.GetPracticesAsync(leaseId, orgId);
        Assert.Equal(CfPracticeStatus.InProgress, firstResult.Practices[0].Status);

        // Manually insert expired snapshot with newer data to trigger refresh
        _cache.Set($"Testing:{orgId}:{leaseId}", ExpiredSnapshot(orgId, newerPractices), new MemoryCacheEntryOptions());

        // Second call: cache expired, API returns older data, but keeps cached newer data
        var secondResult = await service.GetPracticesAsync(leaseId, orgId);
        Assert.Equal(CfPracticeStatus.InProgress, secondResult.Practices[0].Status);
    }

    /// <summary>
    /// CF-FIX2: the lookup of a practice for the upload page is read from ConcordatoFacile once in 15 minutes (the README promises it),
    /// not at every opening. What is cached is ConcordatoFacile's own answer; the controller checks the org of the lease each time.
    /// </summary>
    [Fact]
    public async Task GetPractice_SecondOpening_IsServedFromTheCache_NotReadFromCfAgain()
    {
        var practice = new PracticeDto
        {
            PracticeId = PracticeId,
            LeaseId = Guid.NewGuid(),
            Status = CfPracticeStatus.InProgress,
            StatusChangedAt = DateTime.UtcNow
        };
        _apiClientMock.Setup(c => c.GetPracticeAsync(PracticeId, It.IsAny<CancellationToken>())).ReturnsAsync(practice);

        await using var db = await CreateInMemoryDbAsync();
        var service = CreateService(db);

        var first = await service.GetPracticeAsync(PracticeId);
        var second = await service.GetPracticeAsync(PracticeId);

        Assert.Same(practice, first);
        Assert.Same(practice, second);
        _apiClientMock.Verify(c => c.GetPracticeAsync(PracticeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private ConcordatoFacileStatusCacheService CreateService(
        AppDbContext db,
        ILogger<ConcordatoFacileStatusCacheService>? logger = null)
    {
        return new ConcordatoFacileStatusCacheService(
            _cache,
            _apiClientMock.Object,
            db,
            _environmentMock.Object,
            logger ?? NullLogger<ConcordatoFacileStatusCacheService>.Instance);
    }

    /// <summary>The memory of a failed read whose five minutes are over.</summary>
    private static CfStatusSnapshot ExpiredNotAvailable(Guid orgId) => new()
    {
        OrgId = orgId,
        Practices = [],
        FetchedAt = null,
        ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        IsStale = true,
        IsAvailable = false,
        LastError = "CF API unavailable",
        LastAttemptAt = DateTime.UtcNow.AddMinutes(-6)
    };

    /// <summary>A snapshot whose time has come, as it is 15 minutes after it was read.</summary>
    private static CfStatusSnapshot ExpiredSnapshot(Guid orgId, List<PracticeDto> practices) => new()
    {
        OrgId = orgId,
        Practices = practices,
        FetchedAt = DateTime.UtcNow.AddMinutes(-20),
        ExpiresAt = DateTime.UtcNow.AddMinutes(-1), // Expired
        IsStale = false,
        LastError = null,
        LastAttemptAt = null
    };

    private static async Task SeedLeaseAsync(AppDbContext db, Guid orgId, Guid leaseId)
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = "Test Property",
            Address = "Test Address",
            City = "Test City",
            PostalCode = "12345",
        };
        db.Properties.Add(property);
        db.LeaseContracts.Add(new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = property.Id,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CanoneConcordato,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1000m,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<AppDbContext> CreateInMemoryDbAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}

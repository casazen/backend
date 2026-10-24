using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-008-R7 through ADR-008-R10: CF status cache service with tenant isolation, request coalescing, and stale retention.
/// </summary>
public class CfStatusCacheServiceTests
{
    private readonly Mock<ConcordatoFacileApiClient> _apiClientMock;
    private readonly IMemoryCache _cache;
    private readonly Mock<IHostEnvironment> _environmentMock;

    public CfStatusCacheServiceTests()
    {
        _apiClientMock = new Mock<ConcordatoFacileApiClient>(null!, null!);
        _cache = new MemoryCache(new MemoryCacheOptions());
        _environmentMock = new Mock<IHostEnvironment>();
        _environmentMock.Setup(e => e.EnvironmentName).Returns("Testing");
    }

    /// <summary>
    /// ADR-008-R10: Tenant isolation - cached snapshot for one org is not returned to another org.
    /// This is the PROOF test for the data rule: tenant isolation.
    /// </summary>
    [Fact]
    public async Task GetPractices_OtherOrgCache_NotReturned()
    {
        var leaseId = Guid.NewGuid();
        var orgId1 = Guid.NewGuid();
        var orgId2 = Guid.NewGuid();
        var practices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = Core.Entities.Enums.CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
        _apiClientMock.Setup(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(practices);

        var service = CreateService(db);

        // First org caches the practices
        await service.GetPracticesAsync(leaseId, orgId1);

        // Second org should NOT see the cached snapshot - it should call the API
        _apiClientMock.Invocations.Clear();
        var result = await service.GetPracticesAsync(leaseId, orgId2);

        // Verify the API was called again (cache was not used)
        _apiClientMock.Verify(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(result.Practices);
    }

    [Fact]
    public async Task GetPractices_ValidCache_ReturnsFromCache()
    {
        var leaseId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var practices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = Core.Entities.Enums.CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
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
            new() { PracticeId = "P001", Status = Core.Entities.Enums.CfPracticeStatus.Received, StatusChangedAt = DateTime.UtcNow }
        };

        await using var db = await CreateInMemoryDbAsync();
        _apiClientMock.SetupSequence(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(practices)
            .ThrowsAsync(new HttpRequestException("CF API unavailable"));

        var service = CreateService(db);

        // First call succeeds and caches
        var firstResult = await service.GetPracticesAsync(leaseId, orgId);
        Assert.False(firstResult.IsStale);

        // Wait for cache to expire (simulate by clearing and forcing refresh)
        _cache.Remove($"Testing:{orgId}:{leaseId}");

        // Second call fails but returns stale snapshot
        var secondResult = await service.GetPracticesAsync(leaseId, orgId);

        Assert.True(secondResult.IsStale);
        Assert.Single(secondResult.Practices);
        Assert.NotNull(secondResult.LastError);
        Assert.Contains("CF API unavailable", secondResult.LastError);
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
            new() { PracticeId = "P001", Status = Core.Entities.Enums.CfPracticeStatus.InProgress, StatusChangedAt = newerTime }
        };
        var olderPractices = new List<PracticeDto>
        {
            new() { PracticeId = "P001", Status = Core.Entities.Enums.CfPracticeStatus.Received, StatusChangedAt = olderTime }
        };

        await using var db = await CreateInMemoryDbAsync();
        _apiClientMock.SetupSequence(c => c.GetPracticesByLeaseIdAsync(leaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newerPractices)
            .ReturnsAsync(olderPractices);

        var service = CreateService(db);

        // First call caches newer data
        var firstResult = await service.GetPracticesAsync(leaseId, orgId);
        Assert.Equal(Core.Entities.Enums.CfPracticeStatus.InProgress, firstResult.Practices[0].Status);

        // Clear cache to force refresh
        _cache.Remove($"Testing:{orgId}:{leaseId}");

        // Second call gets older data but keeps cached newer data
        var secondResult = await service.GetPracticesAsync(leaseId, orgId);
        Assert.Equal(Core.Entities.Enums.CfPracticeStatus.InProgress, secondResult.Practices[0].Status);
    }

    private ConcordatoFacileStatusCacheService CreateService(AppDbContext db)
    {
        return new ConcordatoFacileStatusCacheService(
            _cache,
            _apiClientMock.Object,
            db,
            _environmentMock.Object,
            NullLogger<ConcordatoFacileStatusCacheService>.Instance);
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

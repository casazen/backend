using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class DomainVerificationServiceTests
{
    private readonly Mock<IDnsTxtLookup> _dnsLookup = new();

    [Fact]
    public async Task VerifyAsync_WhenTxtMatches_SetsVerified()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "token-abc");
        _dnsLookup.Setup(d => d.LookupTxtAsync("_casazen-challenge.www.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["token-abc"]);

        var service = CreateService(db);
        var result = await service.VerifyAsync(org, CancellationToken.None);

        Assert.Equal(DomainVerificationStatus.Verified, result.Status);
        var updated = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(DomainVerificationStatus.Verified, updated.DomainVerificationStatus);
    }

    [Fact]
    public async Task VerifyAsync_WhenTxtMismatch_SetsFailed()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, "token-abc");
        _dnsLookup.Setup(d => d.LookupTxtAsync("_casazen-challenge.www.example.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["wrong-token"]);

        var service = CreateService(db);
        var result = await service.VerifyAsync(org, CancellationToken.None);

        Assert.Equal(DomainVerificationStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
    }

    private DomainVerificationService CreateService(AppDbContext db) =>
        new(db, _dnsLookup.Object, Options.Create(new PublicHostOptions
        {
            TxtRecordPrefix = "_casazen-challenge",
            DnsLookupTimeoutSeconds = 5,
        }));

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<OrgEntity> SeedOrgAsync(AppDbContext db, string token)
    {
        var org = new OrgEntity
        {
            Name = "Test Org",
            DisplayName = "Test Org",
            Slug = "test-org",
            ContactEmail = "test@example.com",
            PublicHostMode = PublicHostMode.CustomDomain,
            CustomDomain = "www.example.it",
            DomainVerificationToken = token,
            DomainVerificationStatus = DomainVerificationStatus.Pending,
            PlanTier = PlanTier.Pro,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }
}

using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// ADR-004-R2: ConcordatoFacileGift unique OrgId constraint — an org can have at most one gift record.
/// A second insert with the same OrgId throws <see cref="DbUpdateException"/>.
/// </summary>
public class ConcordatoFacileGiftTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task UniqueOrgConstraint_SecondGiftForSameOrg_ThrowsDbException()
    {
        // Arrange: One org with one gift
        var orgId = Guid.NewGuid();

        await using (var db = _database!.CreateContext())
        {
            await db.Database.MigrateAsync();

            db.Orgs.Add(new Org
            {
                Id = orgId,
                Name = "TestOrg",
                Slug = "test-org",
                CreatedAt = DateTime.UtcNow
            });

            db.ConcordatoFacileGifts.Add(new ConcordatoFacileGift
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                GrantedBySubscriptionId = "sub_test_first",
                GrantedAt = DateTime.UtcNow,
                Status = ConcordatoFacileGiftStatus.Available
            });

            await db.SaveChangesAsync();
        }

        // Act & Assert: Attempting to insert a second gift for the same org throws DbUpdateException
        await using (var db = _database.CreateContext())
        {
            db.ConcordatoFacileGifts.Add(new ConcordatoFacileGift
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                GrantedBySubscriptionId = "sub_test_second",
                GrantedAt = DateTime.UtcNow,
                Status = ConcordatoFacileGiftStatus.Available
            });

            var exception = await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
            Assert.Contains("duplicate key", exception.InnerException?.Message ?? exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Verify: The first gift still exists and only one gift is in the database
        await using (var db = _database.CreateContext())
        {
            var gifts = await db.ConcordatoFacileGifts
                .AsNoTracking()
                .Where(g => g.OrgId == orgId)
                .ToListAsync();

            Assert.Single(gifts);
            Assert.Equal("sub_test_first", gifts[0].GrantedBySubscriptionId);
        }
    }
}

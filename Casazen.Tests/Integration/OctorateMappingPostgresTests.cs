using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E02/F01/US02/T02 on a real PostgreSQL database: a CasaZen property is confirmed to one listing of the channel manager only. The
/// partial unique index on the confirmed rows refuses a second confirmed mapping of the same property, while proposals for it are
/// allowed (the matcher may propose a property to several listings until a person confirms one).
/// </summary>
public class OctorateMappingPostgresTests
{
    [PostgresFact]
    public async Task OctorateMapping_OneConfirmedPerProperty_Test()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("octmap");
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await using (var seed = database.CreateContext())
        {
            seed.Orgs.Add(new OrgEntity
            {
                Id = orgId,
                Name = "Org",
                Slug = "org-" + orgId.ToString("N")[..8],
                DisplayName = "Org",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Starter,
            });
            seed.Properties.Add(new Property
            {
                Id = propertyId,
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
            });
            seed.OctorateConnections.Add(new OctorateConnection
            {
                Id = connectionId,
                OrgId = orgId,
                Environment = OctorateEnvironment.Test,
                Status = OctorateConnectionStatus.Connected,
            });
            seed.OctorateListingMappings.Add(Mapping(orgId, connectionId, "1001", propertyId, OctorateMappingStatus.Confirmed));
            // A proposal of the same property for another listing is allowed: only the confirmed rows are unique.
            seed.OctorateListingMappings.Add(Mapping(orgId, connectionId, "1002", propertyId, OctorateMappingStatus.Proposed));
            await seed.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            db.OctorateListingMappings.Add(Mapping(orgId, connectionId, "1003", propertyId, OctorateMappingStatus.Confirmed));
            AssertRefusedByTheConfirmedIndex(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }

        // Confirming the proposal of the other listing is refused the same way.
        await using (var db = database.CreateContext())
        {
            var proposal = await db.OctorateListingMappings.SingleAsync(m => m.OctorateAccommodationId == "1002");
            proposal.Status = OctorateMappingStatus.Confirmed;
            AssertRefusedByTheConfirmedIndex(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        }

        await using (var check = database.CreateContext())
        {
            Assert.Equal(
                1,
                await check.OctorateListingMappings.CountAsync(m => m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed));
        }
    }

    private static void AssertRefusedByTheConfirmedIndex(DbUpdateException refused)
    {
        var postgres = Assert.IsType<PostgresException>(refused.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(OctorateListingMapping.ConfirmedPropertyIndexName, postgres.ConstraintName);
    }

    private static OctorateListingMapping Mapping(
        Guid orgId,
        Guid connectionId,
        string accommodationId,
        Guid propertyId,
        OctorateMappingStatus status)
    {
        return new OctorateListingMapping
        {
            OrgId = orgId,
            ConnectionId = connectionId,
            OctorateAccommodationId = accommodationId,
            SnapshotName = "Listing " + accommodationId,
            PropertyId = propertyId,
            Status = status,
            MatchScore = 90,
            ConfirmedByUserId = status == OctorateMappingStatus.Confirmed ? "auth0|owner" : null,
            ConfirmedAt = status == OctorateMappingStatus.Confirmed ? DateTime.UtcNow : null,
        };
    }
}

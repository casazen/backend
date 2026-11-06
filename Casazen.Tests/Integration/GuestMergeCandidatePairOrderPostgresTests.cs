using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-88 (E07/F01/US01/T01) on a real PostgreSQL database, where it counts: a CRM merge candidate stores its pair of profiles in one order only,
/// the smaller id first (<c>Guid.CompareTo</c>), and the database refuses the other way round. Without that (A, B) and (B, A) could both exist,
/// the unique index would not give one row for each pair, and a pair written in the wrong order would go unnoticed.
/// </summary>
public class GuestMergeCandidatePairOrderPostgresTests
{
    // Two ids whose order depends on the order used. By the canonical one (Guid.CompareTo; a PostgreSQL uuid compares the same way, byte by byte
    // from the left of its text) Low comes first. By the byte order of a .NET Guid in memory, or by the order of a SQL Server uniqueidentifier,
    // High does.
    private static readonly Guid Low = Guid.Parse("00000001-0000-0000-0000-000000000002");
    private static readonly Guid High = Guid.Parse("01000000-0000-0000-0000-000000000001");

    [PostgresFact]
    public async Task GuestMergeCandidate_PairOutOfOrder_IsRefusedByTheDatabase()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmpair");
        var at = new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc);

        // What the code that inserts a pair does to put it in order.
        Assert.True(Low.CompareTo(High) < 0);

        Guid orgId;
        await using (var db = database.CreateContext())
        {
            var org = new OrgEntity
            {
                Name = "Casa CRM",
                Slug = $"crm-pair-{Guid.NewGuid():N}",
                DisplayName = "Casa CRM",
                ContactEmail = "crm-pair@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            };
            db.Orgs.Add(org);
            db.GuestCrmProfiles.AddRange(NewProfile(org.Id, Low, 'a', at), NewProfile(org.Id, High, 'b', at));
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        // (A, B), the smaller id first: accepted.
        await using (var db = database.CreateContext())
        {
            db.GuestMergeCandidates.Add(NewCandidate(orgId, Low, High));
            await db.SaveChangesAsync();
        }

        // (B, A) next to it: the unique index would let it in, the order check refuses it. A profile paired with itself is no pair either.
        await AssertRefusedByTheOrderCheckAsync(database, NewCandidate(orgId, High, Low));
        await AssertRefusedByTheOrderCheckAsync(database, NewCandidate(orgId, Low, Low));

        // Only the pair in order is in the table.
        await using var reader = database.CreateContext();
        var rows = await reader.GuestMergeCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal((Low, High), (row.ProfileAId, row.ProfileBId));
    }

    private static async Task AssertRefusedByTheOrderCheckAsync(PostgresTestDatabase database, GuestMergeCandidate candidate)
    {
        await using var db = database.CreateContext();
        db.GuestMergeCandidates.Add(candidate);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_GuestMergeCandidates_ProfileOrder", postgres.ConstraintName);
    }

    private static GuestCrmProfile NewProfile(Guid orgId, Guid id, char hash, DateTime at)
    {
        return new GuestCrmProfile
        {
            Id = id,
            OrgId = orgId,
            CompositeKeyHash = new string(hash, GuestCrmProfile.KeyHashLength),
            FirstStayAt = at,
            LastStayAt = at,
        };
    }

    private static GuestMergeCandidate NewCandidate(Guid orgId, Guid profileA, Guid profileB)
    {
        return new GuestMergeCandidate
        {
            OrgId = orgId,
            ProfileAId = profileA,
            ProfileBId = profileB,
            Score = 90,
        };
    }
}

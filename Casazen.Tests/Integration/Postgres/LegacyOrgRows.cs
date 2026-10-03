using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Org rows for migration tests that seed an older schema. The current <c>Org</c> entity has columns that later
/// migrations add (e.g. PL-04 <c>ContactEmailPublic</c>), so the model cannot write them there; the columns of the
/// first <c>Orgs</c> table are enough, every later non-null column has a database default.
/// </summary>
internal static class LegacyOrgRows
{
    public static async Task InsertAsync(AppDbContext db, OrgEntity org)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Orgs" (
                "Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({org.Id}, {org.Name}, {org.Slug}, {(int)org.PlanTier}, {org.DisplayName}, {org.ContactEmail},
                {org.IsActive}, now(), now());
            """);
    }
}

using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration;

/// <summary>
/// Writes a supplier profile with plain SQL, for the migration tests that stop at an intermediate migration and seed a supplier
/// there. Saving the entity would write every column of the <b>current</b> model, including the ones a later migration adds
/// (<c>SupplierProfiles.CommissionPercentOverride</c> of SP-15a was the first), and PostgreSQL refuses a column the schema of
/// that migration does not have yet. The columns below are the ones every release has had; the others are nullable or have a
/// default.
/// </summary>
internal static class SupplierProfileSql
{
    public static async Task InsertAsync(AppDbContext db, SupplierProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "SupplierProfiles"
                ("OrgId", "Email", "LegalName", "Phone", "Status", "CategoriesJson", "ComuniJson", "PhotoUrlsJson",
                 "CalendarSyncStatus", "CalendarSyncType", "CreatedAt", "UpdatedAt")
            VALUES
                ({profile.OrgId}, {profile.Email}, {profile.LegalName}, {profile.Phone}, {(int)profile.Status},
                 CAST({profile.CategoriesJson} AS jsonb), CAST({profile.ComuniJson} AS jsonb), CAST({profile.PhotoUrlsJson} AS jsonb),
                 {(int)profile.CalendarSyncStatus}, {(int)profile.CalendarSyncType}, {profile.CreatedAt}, {profile.UpdatedAt});
            """);
    }
}

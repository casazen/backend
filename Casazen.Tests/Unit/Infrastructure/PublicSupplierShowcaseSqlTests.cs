using System.Text.RegularExpressions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-09: the statements of the anonymous reads of a supplier's showcase become the SQL they must be on the PostgreSQL provider
/// (<c>ToQueryString</c> compiles them without a server): the supplier is found only when its status is <c>Active</c>; the
/// public services carry the supplier org, the not-deleted and <c>Active</c> predicates <b>and</b> the join on the active
/// profile in one statement; the sample of the response time is the supplier's own, taken, latest first and capped. The rows
/// themselves are checked by the in-memory tests and, on PostgreSQL, by <c>PublicSupplierShowcasePostgresTests</c>.
/// </summary>
public class PublicSupplierShowcaseSqlTests
{
    private static readonly Guid SupplierOrg = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void ActiveSupplierBySlug_LooksAtTheSlugAndTheActiveStatusInOneStatement()
    {
        using var db = NewNpgsqlContext();

        var sql = PublicSupplierShowcaseService.ActiveBySlugOf(db, "pulizie-citta").ToQueryString();

        Assert.Contains("\"ShowcaseSlug\" = @", sql);
        Assert.Matches("\"Status\" = 1", sql); // Active
        // Nothing else of the profile table is asked of: no join with the owner, the users or the requests.
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicServices_CarryTheSupplierOrgTheNotDeletedAndActivePredicates_AndTheActiveSupplierInTheSameStatement()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierServiceCatalogService.PublicListingsOf(db, SupplierOrg).AsNoTracking().ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
        Assert.Contains("\"DeletedAt\" IS NULL", sql);
        // The status of the service (Active = 1) and the status of the supplier's profile (Active = 1): two predicates,
        // the second on the profile of the same org, joined.
        Assert.Matches("(?s)JOIN \"SupplierProfiles\"", sql);
        Assert.Equal(2, Regex.Matches(sql, "\"Status\" = 1").Count);
    }

    [Fact]
    public void PublicServices_ASlugLookup_StaysInsideThePublicQuery()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierServiceCatalogService.PublicListingsOf(db, SupplierOrg)
            .AsNoTracking()
            .Where(l => l.Slug == "pulizia")
            .ToQueryString();

        Assert.Contains("\"Slug\" = ", sql);
        Assert.Matches("\"OrgId\" = @", sql);
        Assert.Equal(2, Regex.Matches(sql, "\"Status\" = 1").Count);
    }

    [Fact]
    public void ResponseTimeSample_IsTheSuppliersOwnLatestTakenRequestsOfTheWindow_Capped()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierKpiService.RecentAnswersOf(db, SupplierOrg, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), 500).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Contains("\"TakenAt\" IS NOT NULL", sql);
        Assert.Contains("\"TakenAt\" >= @", sql);
        Assert.Matches("ORDER BY .*\"TakenAt\" DESC", sql);
        Assert.Matches("LIMIT @|LIMIT \\d+", sql);
        // Only the two instants leave the table: nothing of the property, the guest or the notes.
        Assert.DoesNotContain("\"Notes\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GuestId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PropertyId\"", sql, StringComparison.Ordinal);
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);
}

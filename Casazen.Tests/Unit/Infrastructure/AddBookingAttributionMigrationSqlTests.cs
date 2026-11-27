using Casazen.Core.Bookings;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E27/F01/US01/T01 (NF-123): migration <c>AddBookingAttribution</c>. The real PostgreSQL script from the Npgsql provider (no connection): four nullable columns, and
/// one correction of the data that exists that gives the token of the booking engine of the agency to the bookings of the booking site that have a reservation on the
/// channel manager and no row of the mirror (a reservation CasaZen did not make), so the host reads them as the site of the agency. Nothing else is written.
/// </summary>
public class AddBookingAttributionMigrationSqlTests
{
    [Fact]
    public void Migration_AddsTheFourColumns_AndAttributesTheBookingEngineReservationsThatExist_AndNothingElse()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var index = keys.FindIndex(k => k.EndsWith("AddBookingAttribution", StringComparison.Ordinal));
        Assert.True(index > 0);
        Assert.True(index > keys.FindIndex(k => k.EndsWith("AddPropertyCleaningSetup", StringComparison.Ordinal)));

        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(fromMigration: keys[index - 1], toMigration: keys[index]);
        var down = migrator.GenerateScript(fromMigration: keys[index], toMigration: keys[index - 1]);

        Assert.Contains("ALTER TABLE \"Bookings\" ADD \"AttributionCampaign\" character varying(80);", up);
        Assert.Contains("ALTER TABLE \"Bookings\" ADD \"AttributionMedium\" character varying(40);", up);
        Assert.Contains("ALTER TABLE \"Bookings\" ADD \"AttributionReferrerHost\" character varying(255);", up);
        Assert.Contains("ALTER TABLE \"Bookings\" ADD \"AttributionSource\" character varying(40);", up);

        Assert.Contains("UPDATE \"Bookings\" AS b", up);
        Assert.Contains($"SET \"AttributionSource\" = '{BookingAttribution.OctorateBookingEngineSource}'", up);
        Assert.Contains($"b.\"Source\" = {(int)BookingSource.Direct}", up);
        Assert.Contains("b.\"OctorateReservationId\" IS NOT NULL", up);
        Assert.Contains("btrim(b.\"OctorateReservationId\") <> ''", up);
        Assert.Contains("b.\"AttributionSource\" IS NULL", up);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM \"OctorateBookingSyncs\" AS s WHERE s.\"BookingId\" = b.\"Id\")", up);
        Assert.True(
            up.IndexOf("ADD \"AttributionSource\"", StringComparison.Ordinal) < up.IndexOf("UPDATE \"Bookings\"", StringComparison.Ordinal),
            "The correction runs after the column it writes exists.");

        Assert.Equal("octorate-booking-engine", BookingAttribution.OctorateBookingEngineSource);
        Assert.Equal(0, (int)BookingSource.Direct);

        Assert.Equal(1, up.Split("UPDATE ", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("\"UpdatedAt\"", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", up, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATE", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", up, StringComparison.Ordinal);

        foreach (var column in new[] { "AttributionCampaign", "AttributionMedium", "AttributionReferrerHost", "AttributionSource" })
            Assert.Contains($"ALTER TABLE \"Bookings\" DROP COLUMN \"{column}\";", down);
        Assert.DoesNotContain("UPDATE ", down, StringComparison.Ordinal);
    }

    [Fact]
    public void BackfillSql_IsTheOneTheMigrationRuns()
    {
        Assert.Contains("UPDATE \"Bookings\" AS b", AddBookingAttribution.BackfillSql);
        Assert.Contains($"'{BookingAttribution.OctorateBookingEngineSource}'", AddBookingAttribution.BackfillSql);
        Assert.EndsWith(";", AddBookingAttribution.BackfillSql.TrimEnd(), StringComparison.Ordinal);
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
}

using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using Xunit;

namespace Casazen.Tests.Integration;

public class BackfillDefaultOrgsPostgresIntegrationTests : IAsyncLifetime
{
    private const string OwnerA = "auth0|owner-a";
    private const string OwnerB = "auth0|owner-b";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                _postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task BackfillDefaultOrgs_AssignsOrgPerOwnerAndWalksRelationships()
    {
        await using var db = NewContext();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("20260609100314_AddOrgIdNullable");

        var propertyA = Guid.NewGuid();
        var propertyB = Guid.NewGuid();
        var guestA = Guid.NewGuid();
        var guestB = Guid.NewGuid();
        var bookingA = Guid.NewGuid();
        var bookingB = Guid.NewGuid();
        var paymentA = Guid.NewGuid();

        // Raw SQL matches the schema at AddOrgIdNullable (pre-Alloggiati columns).
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Users" ("Id", "Email", "FirstName", "LastName", "PhoneNumber", "Role", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({OwnerA}, 'a@example.com', 'A', 'Owner', '', 1, true, now(), now()),
              ({OwnerB}, 'b@example.com', 'B', 'Owner', '', 1, true, now(), now());

            INSERT INTO "Guests" (
                "Id", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress",
                "Notes", "ConsentVersion", "MarketingConsent", "ErasureRequested", "DataRetentionUntil",
                "DataProcessingPurpose", "IsDeleted", "DeletionReason", "CreatedAt", "UpdatedAt")
            VALUES
              ({guestA}, 'Guest', 'A', 'guest-a@example.com', '', '', '', '', '', '', '', '', '', '', '', '', false, false, now() + interval '10 years', 'booking', false, '', now(), now()),
              ({guestB}, 'Guest', 'B', 'guest-b@example.com', '', '', '', '', '', '', '', '', '', '', '', '', false, false, now() + interval '10 years', 'booking', false, '', now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({propertyA}, {OwnerA}, NULL, 'A1', 'Test property A', 'Via A', 'Rome', '00100', 0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now()),
              ({propertyB}, {OwnerB}, NULL, 'B1', 'Test property B', 'Via B', 'Milan', '20100', 0, 0, 1, 1, 2, 120, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "Bookings" (
                "Id", "PropertyId", "GuestId", "OrgId", "CheckInDate", "CheckOutDate",
                "NumberOfGuests", "Status", "Source", "ExternalId", "BasePrice", "TouristTax", "TotalPrice",
                "TouristTaxAmount", "NumberOfAdults", "NumberOfChildren", "SpecialRequests", "CreatedAt", "UpdatedAt")
            VALUES
              ({bookingA}, {propertyA}, {guestA}, NULL, now(), now() + interval '3 days', 2, 1, 0, '', 0, 0, 300, 0, 2, 0, '', now(), now()),
              ({bookingB}, {propertyB}, {guestB}, NULL, now(), now() + interval '2 days', 2, 1, 0, '', 0, 0, 250, 0, 2, 0, '', now(), now());

            INSERT INTO "Payments" (
                "Id", "BookingId", "OrgId", "Amount", "RefundedAmount", "Status", "Method",
                "TransactionId", "Description", "CreatedAt", "UpdatedAt")
            VALUES
              ({paymentA}, {bookingA}, NULL, 300, 0, 0, 0, '', '', now(), now());
            """);

        migrator.Migrate("20260609100413_BackfillDefaultOrgs");

        static async Task<Guid?> ReadOrgIdByUuidAsync(AppDbContext context, string table, Guid id)
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"""SELECT "OrgId" FROM "{table}" WHERE "Id" = @id""";
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });

            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await context.Database.OpenConnectionAsync();

            var scalar = await command.ExecuteScalarAsync();
            return scalar is null or DBNull ? null : (Guid?)scalar;
        }

        static async Task<Guid?> ReadOrgIdByTextIdAsync(AppDbContext context, string table, string id)
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"""SELECT "OrgId" FROM "{table}" WHERE "Id" = @id""";
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = id });

            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await context.Database.OpenConnectionAsync();

            var scalar = await command.ExecuteScalarAsync();
            return scalar is null or DBNull ? null : (Guid?)scalar;
        }

        var propertyOrgA = await ReadOrgIdByUuidAsync(db, "Properties", propertyA);
        var propertyOrgB = await ReadOrgIdByUuidAsync(db, "Properties", propertyB);
        var bookingOrgA = await ReadOrgIdByUuidAsync(db, "Bookings", bookingA);
        var bookingOrgB = await ReadOrgIdByUuidAsync(db, "Bookings", bookingB);
        var paymentOrgA = await ReadOrgIdByUuidAsync(db, "Payments", paymentA);
        var userOrgA = await ReadOrgIdByTextIdAsync(db, "Users", OwnerA);
        var userOrgB = await ReadOrgIdByTextIdAsync(db, "Users", OwnerB);

        Assert.NotNull(propertyOrgA);
        Assert.NotNull(propertyOrgB);
        Assert.NotEqual(propertyOrgA, propertyOrgB);

        Assert.Equal(propertyOrgA, bookingOrgA);
        Assert.Equal(propertyOrgB, bookingOrgB);
        Assert.Equal(propertyOrgA, paymentOrgA);

        Assert.Equal(propertyOrgA, userOrgA);
        Assert.Equal(propertyOrgB, userOrgB);
    }
}

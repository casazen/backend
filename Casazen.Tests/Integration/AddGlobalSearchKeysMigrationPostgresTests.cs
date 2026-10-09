using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// UI-13a on a real PostgreSQL database: the <c>AddGlobalSearchKeys</c> migration applied to a database that already has rows,
/// the case of every real environment. Each of the five searchable tables gets its <c>SearchKey</c> computed for the rows that
/// were there (the words of the texts, folded, written here by hand), the full-text indexes and the index of the beginning of the
/// booking code exist with the expression the queries use, the key follows an update, and the revert drops the columns and the
/// indexes and keeps every row, and the migration can be applied again. The rows are written by plain SQL at the schema right
/// before the migration (the model also writes the generated column, which does not exist there yet).
/// </summary>
public class AddGlobalSearchKeysMigrationPostgresTests : IAsyncLifetime
{
    private static readonly string[] FullTextIndexes =
    [
        "IX_Properties_SearchKey_Fts",
        "IX_Guests_SearchKey_Fts",
        "IX_Parties_SearchKey_Fts",
        "IX_ServiceRequests_SearchKey_Fts",
        "IX_SupplierProfiles_SearchKey_Fts",
    ];

    private static readonly string[] SearchableTables = ["Properties", "Guests", "Parties", "ServiceRequests", "SupplierProfiles"];

    private const string BookingPrefixIndex = "IX_Bookings_BookingCode_Prefix";

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Up_EveryRowThatWasThereGetsItsKey_TheIndexesExist_AndTheKeyFollowsAnUpdate()
    {
        var s = new Seed();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await SeedAsync(db, s);
            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        await AssertKeysAsync(after, s);

        // One generated column in each of the five tables, and nowhere else.
        var columns = await ColumnTablesAsync(after);
        Assert.Equal(SearchableTables.Order(), columns.Order());

        // The indexes: the full-text ones over the expression the queries repeat, the booking prefix one with the pattern operator class.
        var definitions = await IndexDefinitionsAsync(after);
        foreach (var name in FullTextIndexes)
        {
            var definition = DefinitionOf(definitions, name);
            Assert.Contains("USING gin", definition, StringComparison.Ordinal);
            Assert.Contains("to_tsvector('simple'::regconfig", definition, StringComparison.Ordinal);
            Assert.Contains("SearchKey", definition, StringComparison.Ordinal);
        }

        var prefix = DefinitionOf(definitions, BookingPrefixIndex);
        Assert.Contains("pattern_ops", prefix, StringComparison.Ordinal);
        Assert.Contains("BookingCode", prefix, StringComparison.Ordinal);

        // The key follows a change of the row: the database recomputes it, nobody writes it.
        await after.Database.ExecuteSqlAsync($"""
            UPDATE "Guests" SET "LastName" = 'Rossi', "Email" = 'g@example.com' WHERE "Id" = {s.Guest}
            """);
        Assert.Equal("rossi jose g example com", await KeyAsync(after, "Guests", "Id", s.Guest));

        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Down_DropsTheColumnsAndTheIndexes_KeepsTheRows_AndUpComputesTheKeysAgain()
    {
        var s = new Seed();
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        await SeedAsync(db, s);
        await db.Database.MigrateAsync();

        // Back to the schema before: no column, no index of the search, and every row still there.
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        Assert.Empty(await ColumnTablesAsync(db));
        var definitions = await IndexDefinitionsAsync(db);
        Assert.All(FullTextIndexes, name => Assert.DoesNotContain(name, definitions.Keys));
        Assert.DoesNotContain(BookingPrefixIndex, definitions.Keys);
        foreach (var (table, key, id) in s.Rows)
        {
            var sql = $"""SELECT count(*) AS "Value" FROM "{table}" WHERE "{key}" = '{id}'""";
            var count = await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
            Assert.True(count == 1, $"{table} {id}: {count} row(s) after the revert");
        }

        // Up again, on the data that is there: the same keys.
        await db.Database.MigrateAsync();
        await AssertKeysAsync(db, s);
    }

    // --- The rows and what they must give ---------------------------------------------------------------------

    private sealed class Seed
    {
        public Guid HostOrg { get; } = Guid.NewGuid();
        public Guid SupplierOrg { get; } = Guid.NewGuid();
        public Guid Property { get; } = Guid.NewGuid();
        public Guid Guest { get; } = Guid.NewGuid();
        public Guid Lease { get; } = Guid.NewGuid();
        public Guid Party { get; } = Guid.NewGuid();
        public Guid RequestWithName { get; } = Guid.NewGuid();
        public Guid RequestWithoutName { get; } = Guid.NewGuid();

        /// <summary>Table, key column and id of each row written (the supplier profile is keyed by its org).</summary>
        public IEnumerable<(string Table, string Key, Guid Id)> Rows =>
        [
            ("Properties", "Id", Property),
            ("Guests", "Id", Guest),
            ("Parties", "Id", Party),
            ("ServiceRequests", "Id", RequestWithName),
            ("ServiceRequests", "Id", RequestWithoutName),
            ("SupplierProfiles", "OrgId", SupplierOrg),
        ];
    }

    /// <summary>
    /// A host with a property, a guest, a lease with a tenant and two interventions asked of a supplier, and the supplier with its
    /// profile, at the schema before the migration. Texts with accents, an apostrophe, a sharp s and an ampersand, so that the folding
    /// has work to do; the columns of the key that a legacy helper does not write are set by an UPDATE.
    /// </summary>
    private static async Task SeedAsync(AppDbContext db, Seed s)
    {
        await LegacyOrgRows.InsertAsync(db, NewOrg(s.HostOrg, "ui13a-host"));
        await LegacyOrgRows.InsertAsync(db, NewOrg(s.SupplierOrg, "ui13a-supplier"));

        await LegacyPropertyRows.InsertAsync(db, new Property
        {
            Id = s.Property,
            OrgId = s.HostOrg,
            OwnerId = "auth0|ui13a",
            Name = "Villa Dell'\u00C8lite",
            Description = "UI-13a",
            Address = "Via UI-13a " + s.Property.ToString("N"),
            City = "Forl\u00EC",
            PostalCode = "47121",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
        });
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "Properties" SET "CinCode" = 'IT058091C27G5FFZDZ' WHERE "Id" = {s.Property}
            """);

        await LegacyGuestRows.InsertAsync(db, new Guest
        {
            Id = s.Guest,
            OrgId = s.HostOrg,
            FirstName = "Jos\u00E9",
            LastName = "M\u00FCller-Wei\u00DF",
            Email = "Jose.Muller@Example.COM",
        });

        await LegacyLeaseRows.InsertAsync(db, new LeaseContract
        {
            Id = s.Lease,
            OrgId = s.HostOrg,
            PropertyId = s.Property,
            Status = LeaseStatus.Draft,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
        });
        await LegacyPartyRows.InsertAsync(db, new Party
        {
            Id = s.Party,
            LeaseContractId = s.Lease,
            Role = PartyRole.Tenant,
            Position = 0,
            FirstName = "Niccol\u00F2",
            LastName = "D'Ambrosio",
            FiscalCode = "DMBNCL80A01H501Z",
            Citizenship = "IT",
            ContactEmail = "niccolo@example.com",
        });

        await LegacyServiceRequestRows.InsertAsync(db, NewRequest(s, s.RequestWithName, "plumbing"));
        await LegacyServiceRequestRows.InsertAsync(db, NewRequest(s, s.RequestWithoutName, "cleaning"));
        var serviceName = "Riparazione perdita d'acqua";
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "ServiceRequests" SET "ServiceNameSnapshot" = {serviceName} WHERE "Id" = {s.RequestWithName}
            """);

        await SupplierProfileSql.InsertAsync(db, new SupplierProfile
        {
            OrgId = s.SupplierOrg,
            Email = "ui13a-supplier@example.com",
            LegalName = "Pulizie Dell'Alba & Figli S.r.l.",
            Phone = "+39 06 131313",
            Status = SupplierStatus.Active,
            CategoriesJson = "[]",
            ComuniJson = "[]",
        });
    }

    private static OrgEntity NewOrg(Guid id, string slug) => new()
    {
        Id = id,
        Name = slug,
        Slug = $"{slug}-{id:N}",
        DisplayName = slug,
        ContactEmail = $"{slug}@example.com",
        PlanTier = PlanTier.Starter,
        IsActive = true,
    };

    private static ServiceRequest NewRequest(Seed s, Guid id, string category) => new()
    {
        Id = id,
        OrgId = s.HostOrg,
        PropertyId = s.Property,
        SupplierOrgId = s.SupplierOrg,
        RentalContext = ServiceRequestRentalContext.ShortRent,
        Category = category,
        Status = ServiceRequestStatus.Richiesto,
    };

    /// <summary>The key of each row, written by hand: what the migration must have computed for the rows it found.</summary>
    private static async Task AssertKeysAsync(AppDbContext db, Seed s)
    {
        Assert.Equal("villa dell elite forli it058091c27g5ffzdz", await KeyAsync(db, "Properties", "Id", s.Property));
        Assert.Equal("muller weiss jose jose muller example com", await KeyAsync(db, "Guests", "Id", s.Guest));
        Assert.Equal("d ambrosio niccolo", await KeyAsync(db, "Parties", "Id", s.Party));
        Assert.Equal("riparazione perdita d acqua plumbing", await KeyAsync(db, "ServiceRequests", "Id", s.RequestWithName));
        Assert.Equal("cleaning", await KeyAsync(db, "ServiceRequests", "Id", s.RequestWithoutName));
        Assert.Equal("pulizie dell alba figli s r l", await KeyAsync(db, "SupplierProfiles", "OrgId", s.SupplierOrg));
    }

    // --- The server ---------------------------------------------------------------------------------------------

    // The names are those of the tables and columns above: never user input.
    private static async Task<string?> KeyAsync(AppDbContext db, string table, string keyColumn, Guid id)
    {
        var sql = $"""SELECT "SearchKey" AS "Value" FROM "{table}" WHERE "{keyColumn}" = '{id}'""";
        return await db.Database.SqlQueryRaw<string>(sql).SingleAsync();
    }

    private static async Task<List<string>> ColumnTablesAsync(AppDbContext db) =>
        await db.Database
            .SqlQueryRaw<string>("""
                SELECT table_name::text AS "Value" FROM information_schema.columns
                WHERE column_name = 'SearchKey' AND table_schema = current_schema()
                """)
            .ToListAsync();

    private static string DefinitionOf(Dictionary<string, string> definitions, string name)
    {
        Assert.True(definitions.TryGetValue(name, out var definition), $"{name} does not exist");
        return definition!;
    }

    /// <summary>Name and definition of the indexes of the search that exist, as PostgreSQL prints them.</summary>
    private static async Task<Dictionary<string, string>> IndexDefinitionsAsync(AppDbContext db)
    {
        var rows = await db.Database
            .SqlQueryRaw<string>("""
                SELECT indexname::text || '|' || indexdef AS "Value" FROM pg_indexes
                WHERE schemaname = current_schema() AND (indexname LIKE 'IX%SearchKey%Fts' OR indexname = 'IX_Bookings_BookingCode_Prefix')
                """)
            .ToListAsync();
        return rows.ToDictionary(row => row[..row.IndexOf('|')], row => row[(row.IndexOf('|') + 1)..]);
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_" + nameof(AddGlobalSearchKeys), StringComparison.Ordinal));
        Assert.True(index > 0, "AddGlobalSearchKeys migration not found.");
        return all[index - 1];
    }
}

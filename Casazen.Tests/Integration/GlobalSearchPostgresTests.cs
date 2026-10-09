using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Search;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Search;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Search;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// UI-13a on a real PostgreSQL database with every migration applied, the part that no in-memory test can prove. The questions of
/// <see cref="SearchCases"/> are answered with the real full-text index and the real SQL of the scope, with the same ids as on the
/// in-memory provider; the generated key of each of the five tables is what <see cref="SearchText.Fold"/> says for every row, for
/// the scenario and for texts that are hard for the folding (the words are written by hand in <see cref="SearchFoldingSamples"/>);
/// the key cannot be written by hand and follows the row when it is renamed or the guest is erased; the planner uses the
/// full-text index of each table for the expression of the queries and the index of the beginning of the booking code; and a host
/// with thousands of guests is searched through the index, never by reading the table.
/// </summary>
/// <remarks>
/// Skipped where there is no PostgreSQL server (a local run without Docker); on CI they always run. Each test has a database of its
/// own, migrated from nothing, and seeds the world it needs: the tests share nothing.
/// </remarks>
public class GlobalSearchPostgresTests : IAsyncLifetime
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Guests added to each of the two orgs by the volume test: a realistic host has thousands, not millions.</summary>
    private const int GuestsPerOrg = 10_000;

    private static readonly string[] FirstNames =
    [
        "Mario", "Maria", "Marco", "Marta", "Luca", "Lucia", "Paolo", "Paola", "Anna", "Andrea", "Giulia", "Giorgio", "Elena",
        "Enrico", "Sara", "Stefano", "Chiara", "Carlo", "Laura", "Luigi", "Franca", "Franco", "Irene", "Ivan", "Silvia",
    ];

    private static readonly string[] LastNames =
    [
        "Rossi", "Russo", "Ferrari", "Esposito", "Bianchi", "Romano", "Colombo", "Ricci", "Marino", "Greco", "Bruno", "Gallo",
        "Conti", "De Luca", "Costa", "Giordano", "Mancini", "Rizzo", "Lombardi", "Moretti", "Barbieri", "Fontana", "Santoro",
        "Mariani", "Rinaldi", "Caruso", "Ferrara", "Galli", "Martini", "Leone", "Longo", "Gentile", "Martinelli", "Vitale",
        "Lombardo", "Serra", "Coppola", "De Santis", "D'Angelo", "Marchetti",
    ];

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // --- The questions of the unit tests, on the real SQL ---------------------------------------------------

    [PostgresFact]
    public async Task Cases_EveryQuestionIsAnsweredAsOnTheInMemoryProvider()
    {
        await using var db = _database!.CreateContext();
        var world = await SearchScenario.SeedAsync(db, computeKeys: false);

        // Every case is run and the differences are reported together, so one run of CI tells everything that is wrong.
        var failures = new List<string>();
        foreach (var searchCase in SearchCases.All)
        {
            try
            {
                if (await SearchCases.RunAsync(db, world, searchCase) is { } failure)
                    failures.Add(failure);
            }
            catch (Exception exception)
            {
                failures.Add($"{searchCase.Name} ('{searchCase.Term}' as {searchCase.Caller}): {exception.GetType().Name}: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // --- The generated key ----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Keys_TheDatabaseComputesTheWordsOfTheFolding_ForEveryRowOfTheFiveTables()
    {
        await using var db = _database!.CreateContext();
        var world = await SearchScenario.SeedAsync(db, computeKeys: false);

        // Texts that are hard for the folding, with the words they must give written by hand.
        db.Guests.AddRange(SearchFoldingSamples.Guests.Select(sample => new Guest
        {
            OrgId = world.OrgId,
            FirstName = sample.FirstName,
            LastName = sample.LastName,
            Email = sample.Email,
        }));
        foreach (var sample in SearchFoldingSamples.Properties)
        {
            var property = HostScopeScenario.NewProperty(world.OrgId, world.OwnerId, sample.Name);
            property.City = sample.City;
            property.CinCode = sample.CinCode;
            db.Properties.Add(property);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Every row of the five tables: the key of the database against the folding of the text it is made of.
        var failures = new List<string>();
        var rows = new Dictionary<string, int>();

        foreach (var row in await db.Properties.AsNoTracking().IgnoreQueryFilters()
                     .Select(p => new { Row = p, Key = EF.Property<string>(p, SearchKeyModel.KeyProperty) }).ToListAsync())
            Compare(failures, rows, "Properties", row.Row.Id, SearchKeysForTests.PropertyText(row.Row), row.Key);

        foreach (var row in await db.Guests.AsNoTracking().IgnoreQueryFilters()
                     .Select(g => new { Row = g, Key = EF.Property<string>(g, SearchKeyModel.KeyProperty) }).ToListAsync())
            Compare(failures, rows, "Guests", row.Row.Id, SearchKeysForTests.GuestText(row.Row), row.Key);

        foreach (var row in await db.Parties.AsNoTracking().IgnoreQueryFilters()
                     .Select(p => new { Row = p, Key = EF.Property<string>(p, SearchKeyModel.KeyProperty) }).ToListAsync())
            Compare(failures, rows, "Parties", row.Row.Id, SearchKeysForTests.PartyText(row.Row), row.Key);

        foreach (var row in await db.ServiceRequests.AsNoTracking().IgnoreQueryFilters()
                     .Select(r => new { Row = r, Key = EF.Property<string>(r, SearchKeyModel.KeyProperty) }).ToListAsync())
            Compare(failures, rows, "ServiceRequests", row.Row.Id, SearchKeysForTests.ServiceRequestText(row.Row), row.Key);

        foreach (var row in await db.SupplierProfiles.AsNoTracking().IgnoreQueryFilters()
                     .Select(s => new { Row = s, Key = EF.Property<string>(s, SearchKeyModel.KeyProperty) }).ToListAsync())
            Compare(failures, rows, "SupplierProfiles", row.Row.OrgId, SearchKeysForTests.SupplierProfileText(row.Row), row.Key);

        // The hard texts against the words written by hand (neither side is compared with itself).
        var guestKeys = await db.Guests.AsNoTracking().IgnoreQueryFilters()
            .Where(g => g.OrgId == world.OrgId)
            .Select(g => new { g.Email, Key = EF.Property<string>(g, SearchKeyModel.KeyProperty) })
            .ToListAsync();
        foreach (var sample in SearchFoldingSamples.Guests)
        {
            var key = guestKeys.Single(g => g.Email == sample.Email).Key;
            if (key != sample.Key)
                failures.Add($"Guests {sample.Email}: the database computed '{key}', the words expected are '{sample.Key}'");
        }

        var propertyKeys = await db.Properties.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.OrgId == world.OrgId)
            .Select(p => new { p.Name, Key = EF.Property<string>(p, SearchKeyModel.KeyProperty) })
            .ToListAsync();
        foreach (var sample in SearchFoldingSamples.Properties)
        {
            var key = propertyKeys.Single(p => p.Name == sample.Name).Key;
            if (key != sample.Key)
                failures.Add($"Properties {sample.Name}: the database computed '{key}', the words expected are '{sample.Key}'");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // A test that looks at no row proves nothing: every table had rows.
        foreach (var (_, table, _, _) in SearchKeyModel.Keys)
            Assert.True(rows.GetValueOrDefault(table) > 0, $"No row of {table} was checked");
    }

    private static void Compare(List<string> failures, Dictionary<string, int> rows, string table, Guid id, string text, string? key)
    {
        rows[table] = rows.GetValueOrDefault(table) + 1;
        var expected = SearchText.Fold(text);
        if (key != expected)
            failures.Add($"{table} {id}: the database computed '{key}', the folding of '{text}' is '{expected}'");
    }

    [PostgresFact]
    public async Task Key_CannotBeWrittenByHand_SoNoWriterCanLeaveItStale()
    {
        await using var db = _database!.CreateContext();
        var world = await SearchScenario.SeedAsync(db, computeKeys: false);
        var guestId = world.Jose.Id;

        // GENERATED ALWAYS ... STORED: the database refuses every value but the default (428C9, generated_always).
        var error = await Assert.ThrowsAnyAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"""UPDATE "Guests" SET "SearchKey" = 'zzz' WHERE "Id" = {guestId}"""));

        Assert.Equal("428C9", error.SqlState);
    }

    [PostgresFact]
    public async Task Key_FollowsTheRow_WhenItIsRenamedAndWhenTheGuestIsErased()
    {
        await using var db = _database!.CreateContext();
        var world = await SearchScenario.SeedAsync(db, computeKeys: false);
        var service = new GlobalSearchService(db, NullLogger<GlobalSearchService>.Instance);

        // The application renames a guest and a property: the database recomputes the keys.
        var mario = await db.Guests.SingleAsync(g => g.Id == world.MarioRossi.Id);
        mario.LastName = "Bianchi-\u00D6stlund";
        mario.Email = "mario.ostlund@example.com";
        var trullo = await db.Properties.SingleAsync(p => p.Id == world.Trullo.Id);
        trullo.Name = "Masseria Rosa";
        trullo.City = "Ostuni";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal("bianchi ostlund mario mario ostlund example com", await GuestKeyAsync(db, world.MarioRossi.Id));
        Assert.Equal("masseria rosa ostuni it072003c2abcd12", await PropertyKeyAsync(db, world.Trullo.Id));
        Assert.Equal(new[] { world.MarioRossi.Id }, Ids(await SearchAsync(service, world, "ostlund"), SearchTypes.Guest));
        Assert.DoesNotContain(world.MarioRossi.Id, Ids(await SearchAsync(service, world, "rossi"), SearchTypes.Guest));
        Assert.Equal(new[] { world.Trullo.Id }, Ids(await SearchAsync(service, world, "masseria"), SearchTypes.Property));
        Assert.Empty(Ids(await SearchAsync(service, world, "trullo"), SearchTypes.Property));

        // The erasure of the guest (GuestDataEraser): the names and the e-mail address become the placeholders, the key follows, and
        // the person is found no more, neither as a guest nor through the stays.
        var guest = await db.Guests.SingleAsync(g => g.Id == world.MarioRossi.Id);
        guest.FirstName = GuestDataEraser.AnonymizedName;
        guest.LastName = GuestDataEraser.AnonymizedName;
        guest.Email = GuestDataEraser.AnonymizedEmail(guest.Id);
        guest.DataAnonymizedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var erasedKey = await GuestKeyAsync(db, world.MarioRossi.Id);
        Assert.Equal(SearchText.Fold($"{GuestDataEraser.AnonymizedName} {GuestDataEraser.AnonymizedName} {GuestDataEraser.AnonymizedEmail(world.MarioRossi.Id)}"), erasedKey);
        Assert.DoesNotContain("ostlund", erasedKey, StringComparison.Ordinal);

        var afterTheErasure = await SearchAsync(service, world, "ostlund");
        Assert.Empty(Ids(afterTheErasure, SearchTypes.Guest));
        Assert.Empty(Ids(afterTheErasure, SearchTypes.Booking));
    }

    private static Task<string?> GuestKeyAsync(AppDbContext db, Guid id) =>
        db.Guests.AsNoTracking().IgnoreQueryFilters().Where(g => g.Id == id)
            .Select(g => EF.Property<string?>(g, SearchKeyModel.KeyProperty)).SingleAsync();

    private static Task<string?> PropertyKeyAsync(AppDbContext db, Guid id) =>
        db.Properties.AsNoTracking().IgnoreQueryFilters().Where(p => p.Id == id)
            .Select(p => EF.Property<string?>(p, SearchKeyModel.KeyProperty)).SingleAsync();

    private static Task<GlobalSearchResult> SearchAsync(GlobalSearchService service, SearchWorld world, string term, int limit = SearchLimits.MaxLimit) =>
        service.SearchAsync(new GlobalSearchRequest(SearchText.Parse(term), limit, world.Everything(), null, Italian));

    private static Guid[] Ids(GlobalSearchResult result, string type) =>
        result.Groups.Where(g => g.Type == type).SelectMany(g => g.Items).Select(i => i.Id).ToArray();

    // --- The plans ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Plans_TheQueriesUseTheIndexesOfTheMigration()
    {
        await using (var db = _database!.CreateContext())
            await SearchScenario.SeedAsync(db, computeKeys: false);

        var failures = new List<string>();

        // The expression of the query is the one of the index of its table. With the sequential scan off the planner has only the
        // index to answer with, and it can use it only if the two expressions are the same.
        foreach (var (_, table, _, indexName) in SearchKeyModel.Keys)
        {
            var plan = await ExplainAsync($$"""
                SELECT 1 FROM "{{table}}"
                WHERE to_tsvector('{{SearchKeyModel.TextSearchConfig}}', coalesce("SearchKey", '')) @@ to_tsquery('{{SearchKeyModel.TextSearchConfig}}', 'ros:* & mar:*')
                """, "SET enable_seqscan = off");

            if (!plan.Any(node => node.Index == indexName))
                failures.Add($"{table}: the full-text query does not use {indexName}. Plan: {Describe(plan)}");
        }

        // The beginning of a booking code is a range of its own index (in a collation that is not C the default index cannot do it).
        var bookingPlan = await ExplainAsync("""SELECT 1 FROM "Bookings" WHERE "BookingCode" LIKE '7K3M%'""", "SET enable_seqscan = off");
        var collation = await ScalarAsync("SELECT datcollate FROM pg_database WHERE datname = current_database()");
        if (bookingPlan.Any(node => node.NodeType == "Seq Scan"))
            failures.Add($"Bookings: the beginning of a code is read with a sequential scan. Plan: {Describe(bookingPlan)}");
        else if (collation is not ("C" or "POSIX") && !bookingPlan.Any(node => node.Index == SearchKeyModel.BookingCodePrefixIndex))
            failures.Add($"Bookings: in the collation '{collation}' the beginning of a code needs {SearchKeyModel.BookingCodePrefixIndex}. Plan: {Describe(bookingPlan)}");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [PostgresFact]
    public async Task Volume_ForHostsWithThousandsOfGuests_TheSearchUsesTheIndexAndAnswersWithinTheLimit()
    {
        await using var db = _database!.CreateContext();
        var world = await SearchScenario.SeedAsync(db, computeKeys: false);
        await AddGuestsAsync(db, world.OrgId, GuestsPerOrg);
        await AddGuestsAsync(db, world.OtherOrgId, GuestsPerOrg);
        await db.Database.ExecuteSqlRawAsync("""
            ANALYZE "Guests"
            """);

        // The query of the guests of an org, for a person's name (a few rows among thousands): the planner takes the full-text index
        // and never reads the table. It chooses by itself, as it does for the application: nothing is switched off here.
        var tsQuery = SearchText.Parse("muller jose").ToTsQuery();
        var plan = await ExplainAsync($$"""
            SELECT g."Id" FROM "Guests" AS g
            WHERE g."OrgId" = '{{world.OrgId}}' AND NOT g."IsDeleted" AND g."DataAnonymizedDate" IS NULL
              AND to_tsvector('{{SearchKeyModel.TextSearchConfig}}', coalesce(g."SearchKey", '')) @@ to_tsquery('{{SearchKeyModel.TextSearchConfig}}', '{{tsQuery}}')
            ORDER BY g."SearchKey", g."Id"
            LIMIT 6
            """);
        Assert.True(!plan.Any(node => node.NodeType == "Seq Scan" && node.Relation == "Guests"), "Sequential scan of Guests. Plan: " + Describe(plan));
        Assert.True(plan.Any(node => node.Index == "IX_Guests_SearchKey_Fts"), "IX_Guests_SearchKey_Fts not used. Plan: " + Describe(plan));

        // What the application answers on that volume: the one person, and a common surname cut to the limit with "more" set.
        var service = new GlobalSearchService(db, NullLogger<GlobalSearchService>.Instance);
        Assert.Equal(new[] { world.Jose.Id }, Ids(await SearchAsync(service, world, "muller jose"), SearchTypes.Guest));

        var common = await SearchAsync(service, world, "rossi", limit: 5);
        var guests = Assert.Single(common.Groups, g => g.Type == SearchTypes.Guest);
        Assert.Equal(5, guests.Items.Count);
        Assert.True(guests.HasMore);

        // Whatever the volume, only the guests of the org of the caller.
        var found = guests.Items.Select(i => i.Id).ToList();
        var orgs = await db.Guests.AsNoTracking().IgnoreQueryFilters().Where(g => found.Contains(g.Id)).Select(g => g.OrgId).Distinct().ToListAsync();
        Assert.Equal(new[] { world.OrgId }, orgs);
    }

    /// <summary>Adds guests with ordinary Italian names and an e-mail address of their own, in chunks (one save for all would hold them in memory).</summary>
    private static async Task AddGuestsAsync(AppDbContext db, Guid orgId, int count)
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            for (var start = 0; start < count; start += 1000)
            {
                for (var i = start; i < Math.Min(start + 1000, count); i++)
                {
                    db.Guests.Add(new Guest
                    {
                        OrgId = orgId,
                        FirstName = FirstNames[i % FirstNames.Length],
                        LastName = LastNames[i / FirstNames.Length % LastNames.Length],
                        Email = $"ospite{i}@example.com",
                    });
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }

    // --- The server -----------------------------------------------------------------------------------------

    private sealed record PlanNode(string NodeType, string? Relation, string? Index);

    /// <summary>The nodes of the plan the server chooses for <paramref name="sql"/>, with the settings applied to the session first.</summary>
    private async Task<List<PlanNode>> ExplainAsync(string sql, params string[] settings)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        foreach (var setting in settings)
        {
            await using var set = connection.CreateCommand();
            set.CommandText = setting;
            await set.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
        var json = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;

        using var document = JsonDocument.Parse(json);
        var nodes = new List<PlanNode>();
        Collect(document.RootElement[0].GetProperty("Plan"), nodes);
        return nodes;
    }

    private static void Collect(JsonElement plan, List<PlanNode> nodes)
    {
        nodes.Add(new PlanNode(
            plan.GetProperty("Node Type").GetString()!,
            plan.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
            plan.TryGetProperty("Index Name", out var index) ? index.GetString() : null));

        if (plan.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
                Collect(child, nodes);
        }
    }

    private static string Describe(IEnumerable<PlanNode> plan) => string.Join(
        " > ",
        plan.Select(node => node.Index is not null ? $"{node.NodeType} [{node.Index}]" : node.Relation is not null ? $"{node.NodeType} on {node.Relation}" : node.NodeType));

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

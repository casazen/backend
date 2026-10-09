using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CI-FIX-PM: proof, WITHOUT a database, that the rows the migration tests write at an older migration point fit the schema
/// of that point. The PostgreSQL tests that migrate to a point and then saved entities through the model
/// (<c>FieldEncryptionPostgresTests</c>, <c>LeaseMultiplePartiesMigrationPostgresTests</c>) broke each time a column was added to
/// <c>Properties</c> (PC-03, PC-05, SU-04, PC-06, PM-01…): the model writes the columns of the latest schema. They now write by
/// SQL, with the <c>Legacy*Rows</c> helpers, naming only columns that exist at the point. This class replays the migrations
/// (<see cref="MigrationSchemaReplay"/>) and checks, for every point where a test uses a helper, that each column written
/// exists, that every NOT NULL column without a default is written, and that the number of values equals the number of
/// columns. A new column on an entity can no longer break those tests, and a mistake in a statement shows up here, in a
/// fast local test, instead of in the CI run with PostgreSQL.
/// </summary>
public class LegacyRowsSchemaTests
{
    private const string Co14 = "EncryptGuestDocumentAndQuesturaCredentials";
    private const string Lt14 = "LeaseMultipleParties";
    private const string Pm01 = "AddPropertyRentalMode";
    private const string Sp10 = "AddShowcaseBooking";
    private const string Db03 = "AddDirectBookingPublicData";
    private const string Latest = "*";

    private static readonly Regex InsertShape = new(
        "^\\s*INSERT\\s+INTO\\s+\"(?<table>[^\"]+)\"\\s*\\((?<columns>[^)]*)\\)\\s*VALUES\\s*\\((?<values>.*)\\)\\s*;?\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Lazy<IReadOnlyList<SchemaStep>> LazySteps = new(() =>
    {
        using var db = NewContext();
        return MigrationSchemaReplay.Run(db);
    });

    private static readonly Dictionary<string, FormattableString> Statements = new(StringComparer.Ordinal)
    {
        [nameof(LegacyOrgRows)] = LegacyOrgRows.Statement(new OrgEntity()),
        [nameof(LegacyPropertyRows)] = LegacyPropertyRows.Statement(new Property()),
        [nameof(LegacyLeaseRows)] = LegacyLeaseRows.Statement(new LeaseContract()),
        [nameof(LegacyBookingRows)] = LegacyBookingRows.Statement(new Booking()),
        [nameof(LegacyGuestRows)] = LegacyGuestRows.Statement(new Guest()),
    };

    private static IReadOnlyList<SchemaStep> Steps => LazySteps.Value;

    /// <summary>
    /// Every use of a helper in the PostgreSQL tests: the helper and the migration under test (the rows are written at the
    /// schema right before it); <c>*</c> = a fully migrated database (the "old writer" check of the rental mode tests).
    /// </summary>
    public static TheoryData<string, string> Uses => new()
    {
        { nameof(LegacyOrgRows), "AddStayGuestsAndAlloggiatiCodeTables" },
        { nameof(LegacyOrgRows), "AddDl145SafetyChecklist" },
        { nameof(LegacyOrgRows), Co14 },
        { nameof(LegacyOrgRows), Lt14 },
        { nameof(LegacyOrgRows), Pm01 },
        { nameof(LegacyOrgRows), Db03 },
        { nameof(LegacyPropertyRows), Co14 },
        { nameof(LegacyPropertyRows), Lt14 },
        { nameof(LegacyPropertyRows), Pm01 },
        { nameof(LegacyPropertyRows), Sp10 },
        { nameof(LegacyPropertyRows), Db03 },
        { nameof(LegacyPropertyRows), Latest },
        { nameof(LegacyLeaseRows), Lt14 },
        { nameof(LegacyLeaseRows), Pm01 },
        { nameof(LegacyBookingRows), Co14 },
        { nameof(LegacyBookingRows), Pm01 },
        { nameof(LegacyGuestRows), Pm01 },
    };

    /// <summary>The first migration after which each helper is valid (see the summary of each helper).</summary>
    public static TheoryData<string, string> FirstValidAfter => new()
    {
        { nameof(LegacyPropertyRows), "AddOrgIdNullable" },
        { nameof(LegacyLeaseRows), "LeasePartyRetention" },
        { nameof(LegacyBookingRows), "AddBookingCode" },
        { nameof(LegacyGuestRows), "GuestPrivacyConsentsAndRetention" },
    };

    [Theory]
    [MemberData(nameof(Uses))]
    public void Statement_AtTheMigrationPointWhereATestUsesIt_FitsTheSchema(string helper, string migrationUnderTest)
    {
        var step = migrationUnderTest == Latest ? Steps[^1] : MigrationSchemaReplay.StepBefore(Steps, migrationUnderTest);

        var problems = Problems(step, Statements[helper]);

        Assert.True(
            problems.Count == 0,
            $"{helper} does not fit the schema after {step.MigrationId}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Theory]
    [MemberData(nameof(FirstValidAfter))]
    public void Statement_AtTheStatedFirstMigration_IsValidAndTheOneBeforeIsNot(string helper, string first)
    {
        // The summary of each helper states the point it applies from: valid right after it, refused right before it. A
        // checker that cannot fail would prove nothing, this is also its sensitivity test.
        Assert.Empty(Problems(MigrationSchemaReplay.Step(Steps, first), Statements[helper]));
        Assert.NotEmpty(Problems(MigrationSchemaReplay.StepBefore(Steps, first), Statements[helper]));
    }

    [Fact]
    public void Problems_ColumnThatDoesNotExistRequiredColumnLeftOutAndValueCountMismatch_AreAllReported()
    {
        var step = MigrationSchemaReplay.StepBefore(Steps, Pm01);
        FormattableString noRentalModeYet = $"""INSERT INTO "Properties" ("Id", "RentalMode") VALUES ({Guid.NewGuid()}, 0)""";
        FormattableString tooManyColumns = $"""INSERT INTO "Orgs" ("Id", "Name") VALUES ({Guid.NewGuid()})""";
        FormattableString unknownTable = $"""INSERT INTO "NoSuchTable" ("Id") VALUES ({Guid.NewGuid()})""";

        var missing = Problems(step, noRentalModeYet);
        Assert.Contains(missing, p => p.Contains("RentalMode", StringComparison.Ordinal) && p.Contains("does not exist", StringComparison.Ordinal));
        Assert.Contains(missing, p => p.Contains("OwnerId", StringComparison.Ordinal) && p.Contains("NOT NULL", StringComparison.Ordinal));
        Assert.Contains(Problems(step, tooManyColumns), p => p.Contains("1 value(s) for 2 column(s)", StringComparison.Ordinal));
        Assert.Contains(Problems(step, unknownTable), p => p.Contains("NoSuchTable", StringComparison.Ordinal));
    }

    [Fact]
    public void Replay_AtTheLastMigration_MatchesTheModel()
    {
        // The replay is only worth what it reproduces: after the last migration it must give back the columns (and their
        // nullability) that the model maps, table by table. A raw SQL migration that changes a table in a way the replay does
        // not understand shows up here, as a difference, instead of being skipped.
        using var db = NewContext();
        var model = db.GetService<IDesignTimeModel>().Model;
        var replayed = Steps[^1].Tables;
        var differences = new List<string>();

        var expected = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not { } table)
                continue;

            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            if (!expected.TryGetValue(table, out var columns))
                expected[table] = columns = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName(store) is { } column && column != "xmin")
                    columns[column] = columns.TryGetValue(column, out var nullable) ? nullable && property.IsColumnNullable(store) : property.IsColumnNullable(store);
            }
        }

        foreach (var (table, columns) in expected)
        {
            if (!replayed.TryGetValue(table, out var actual))
            {
                differences.Add($"table {table} is in the model and not in the replay");
                continue;
            }

            foreach (var (column, nullable) in columns)
            {
                if (!actual.TryGetValue(column, out var replayedColumn))
                    differences.Add($"{table}.{column} is in the model and not in the replay");
                else if (replayedColumn.IsNullable != nullable)
                    differences.Add($"{table}.{column}: nullable in the model = {nullable}, in the replay = {replayedColumn.IsNullable}");
            }

            differences.AddRange(actual.Keys.Where(c => !columns.ContainsKey(c)).Select(c => $"{table}.{c} is in the replay and not in the model"));
        }

        differences.AddRange(replayed.Keys.Where(t => !expected.ContainsKey(t)).Select(t => $"table {t} is in the replay and not in the model"));
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    /// <summary>What is wrong with an INSERT written at the schema of one migration point (empty = it fits).</summary>
    private static List<string> Problems(SchemaStep step, FormattableString statement)
    {
        var problems = new List<string>();
        var match = InsertShape.Match(statement.Format);
        if (!match.Success)
        {
            problems.Add("not a single INSERT INTO \"table\" (columns) VALUES (values) statement");
            return problems;
        }

        var tableName = match.Groups["table"].Value;
        var columns = match.Groups["columns"].Value.Split(',').Select(c => c.Trim().Trim('"')).ToList();
        var valueCount = SplitTopLevel(match.Groups["values"].Value).Count;
        if (!step.Tables.TryGetValue(tableName, out var table))
        {
            problems.Add($"table {tableName} does not exist");
            return problems;
        }

        problems.AddRange(columns.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"column {g.Key} is written twice"));
        problems.AddRange(columns.Where(c => !table.ContainsKey(c)).Select(c => $"column {tableName}.{c} does not exist"));
        problems.AddRange(
            table.Values
                .Where(c => c.IsRequired && !columns.Contains(c.Name, StringComparer.Ordinal))
                .Select(c => $"column {tableName}.{c.Name} is NOT NULL without a default and is not written"));
        if (valueCount != columns.Count)
            problems.Add($"{valueCount} value(s) for {columns.Count} column(s)");

        return problems;
    }

    /// <summary>Splits a VALUES list at the commas that are not inside parentheses, brackets or quotes.</summary>
    private static List<string> SplitTopLevel(string values)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var ch = values[i];
            if (ch == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                if (ch is '(' or '[')
                    depth++;
                else if (ch is ')' or ']')
                    depth--;
                else if (ch == ',' && depth == 0)
                {
                    parts.Add(values[start..i].Trim());
                    start = i + 1;
                }
            }
        }

        parts.Add(values[start..].Trim());
        return parts;
    }

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);
}

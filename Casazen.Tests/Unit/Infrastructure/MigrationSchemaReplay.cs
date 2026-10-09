using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>A column of a table at one point of the migration history.</summary>
/// <param name="Name">Column name.</param>
/// <param name="IsNullable">False = the database refuses a row without a value.</param>
/// <param name="HasDefault">A default, a generated value or an identity: an INSERT may leave the column out.</param>
internal sealed record SchemaColumn(string Name, bool IsNullable, bool HasDefault)
{
    /// <summary>The INSERT must name this column (NOT NULL and nothing fills it in).</summary>
    public bool IsRequired => !IsNullable && !HasDefault;
}

/// <summary>The tables of the database right after one migration was applied.</summary>
internal sealed record SchemaStep(string MigrationId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SchemaColumn>> Tables);

/// <summary>
/// The schema at every point of the migration history, computed WITHOUT a database: the <c>Up</c> operations of every
/// migration are replayed in order (create / drop / rename table, add / drop / rename / alter column, and the few raw
/// <c>ALTER COLUMN … SET NOT NULL</c> of the early backfill migrations). It answers "which columns does <c>Properties</c> have
/// after migration X, and which of them must an INSERT name?", which is what a test that migrates to X and then writes rows
/// by SQL needs to know (<see cref="Casazen.Tests.Integration.Postgres.LegacyPropertyRows"/> and its siblings). A raw SQL
/// migration that changes the shape of a table in a way this class does not understand fails the replay (see
/// <c>LegacyRowsSchemaTests.Replay_AtTheLastMigration_MatchesTheModel</c>) instead of being skipped.
/// </summary>
internal static class MigrationSchemaReplay
{
    private static readonly Regex AlterNotNull = new(
        "ALTER\\s+TABLE\\s+\"(?<table>[^\"]+)\"\\s+ALTER\\s+COLUMN\\s+\"(?<column>[^\"]+)\"\\s+(?<action>SET|DROP)\\s+NOT\\s+NULL",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// PostgreSQL's own row version: a concurrency token maps to it and the migrations record an <c>AddColumn</c> for it,
    /// but the provider emits no DDL and the table has no such user column.
    /// </summary>
    private const string SystemColumn = "xmin";

    /// <summary>One step per migration, oldest first.</summary>
    public static IReadOnlyList<SchemaStep> Run(DbContext db)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var provider = db.GetService<IDatabaseProvider>().Name;
        var tables = new Dictionary<string, Dictionary<string, SchemaColumn>>(StringComparer.Ordinal);
        var steps = new List<SchemaStep>();
        foreach (var (id, type) in assembly.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            foreach (var operation in assembly.CreateMigration(type, provider).UpOperations)
                Apply(tables, operation);

            steps.Add(new SchemaStep(
                id,
                tables.ToDictionary(
                    t => t.Key,
                    t => (IReadOnlyDictionary<string, SchemaColumn>)new Dictionary<string, SchemaColumn>(t.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal)));
        }

        return steps;
    }

    /// <summary>The step of the migration whose id ends with <c>_name</c>.</summary>
    public static SchemaStep Step(IReadOnlyList<SchemaStep> steps, string migrationName) =>
        steps.Single(s => s.MigrationId.EndsWith("_" + migrationName, StringComparison.Ordinal));

    /// <summary>The step right before the migration whose id ends with <c>_name</c> (what <c>PreviousMigration</c> of a test migrates to).</summary>
    public static SchemaStep StepBefore(IReadOnlyList<SchemaStep> steps, string migrationName)
    {
        var index = steps.ToList().FindIndex(s => s.MigrationId.EndsWith("_" + migrationName, StringComparison.Ordinal));
        if (index < 1)
            throw new InvalidOperationException($"Migration {migrationName} not found, or it is the first one.");

        return steps[index - 1];
    }

    private static void Apply(Dictionary<string, Dictionary<string, SchemaColumn>> tables, MigrationOperation operation)
    {
        switch (operation)
        {
            case CreateTableOperation create:
                tables[create.Name] = create.Columns
                    .Where(c => c.Name != SystemColumn)
                    .ToDictionary(c => c.Name, ToColumn, StringComparer.Ordinal);
                break;
            case DropTableOperation drop:
                tables.Remove(drop.Name);
                break;
            case RenameTableOperation rename:
                if (tables.Remove(rename.Name, out var renamed))
                    tables[rename.NewName ?? rename.Name] = renamed;
                break;
            case AddColumnOperation add:
                if (add.Name != SystemColumn)
                    tables[add.Table][add.Name] = ToColumn(add);
                break;
            case AlterColumnOperation alter:
                tables[alter.Table][alter.Name] = ToColumn(alter);
                break;
            case DropColumnOperation dropColumn:
                if (dropColumn.Name != SystemColumn)
                    tables[dropColumn.Table].Remove(dropColumn.Name);
                break;
            case RenameColumnOperation renameColumn:
                {
                    var columns = tables[renameColumn.Table];
                    var column = columns[renameColumn.Name];
                    columns.Remove(renameColumn.Name);
                    columns[renameColumn.NewName] = column with { Name = renameColumn.NewName };
                    break;
                }

            case SqlOperation sql:
                foreach (Match match in AlterNotNull.Matches(sql.Sql))
                {
                    var column = tables[match.Groups["table"].Value][match.Groups["column"].Value];
                    tables[match.Groups["table"].Value][column.Name] = column with { IsNullable = match.Groups["action"].Value.Equals("DROP", StringComparison.OrdinalIgnoreCase) };
                }

                break;
        }
    }

    private static SchemaColumn ToColumn(ColumnOperation column) => new(
        column.Name,
        column.IsNullable,
        column.DefaultValue is not null
            || column.DefaultValueSql is not null
            || column.ComputedColumnSql is not null
            || column.GetAnnotations().Any(a => a.Name.EndsWith("ValueGenerationStrategy", StringComparison.Ordinal)
                && a.Value is not null
                && !string.Equals(a.Value.ToString(), "None", StringComparison.Ordinal)));
}

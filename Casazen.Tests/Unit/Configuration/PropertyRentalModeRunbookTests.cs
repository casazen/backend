using System.Text.RegularExpressions;
using Casazen.Infrastructure.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// PM-01: the dry run of the backfill (decision D19) is a SQL query documented in the runbook, to be run on the test
/// database before the merge. The runbook must hold exactly the queries of the migration, otherwise the owner would approve
/// the count of a query that is not the one that migrates; the runbook must also be in the index.
/// </summary>
public class PropertyRentalModeRunbookTests
{
    [Theory]
    [InlineData(nameof(AddPropertyRentalMode.DryRunSummarySql))]
    [InlineData(nameof(AddPropertyRentalMode.DryRunDetailSql))]
    [InlineData(nameof(AddPropertyRentalMode.RollbackCheckSql))]
    public void Runbook_HoldsTheQueriesOfTheMigrationVerbatim(string constantName)
    {
        var sql = (string)typeof(AddPropertyRentalMode).GetField(constantName)!.GetRawConstantValue()!;

        Assert.Contains(Normalize(sql), Normalize(File.ReadAllText(Path.Combine(RunbooksFolder(), "property-rental-mode.md"))));
    }

    [Fact]
    public void Runbook_IsInTheIndex()
    {
        Assert.Contains("(property-rental-mode.md)", File.ReadAllText(Path.Combine(RunbooksFolder(), "index.md")));
    }

    [Fact]
    public void DryRunQueries_ReadNothingTheMigrationCreates()
    {
        // They run BEFORE the migration, on the database as it is: the new column must not be in them.
        foreach (var sql in new[]
                 {
                     AddPropertyRentalMode.SignalsSql,
                     AddPropertyRentalMode.DryRunSummarySql,
                     AddPropertyRentalMode.DryRunDetailSql,
                 })
        {
            Assert.DoesNotContain("RentalMode", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DryRunQueries_ShareTheSignalsOfTheBackfill()
    {
        // One subquery for the three: the list the owner approves cannot drift from the statement that migrates.
        foreach (var sql in new[]
                 {
                     AddPropertyRentalMode.BackfillSql,
                     AddPropertyRentalMode.DryRunSummarySql,
                     AddPropertyRentalMode.DryRunDetailSql,
                 })
        {
            Assert.Contains(Normalize(AddPropertyRentalMode.SignalsSql), Normalize(sql));
        }
    }

    [Fact]
    public void SignalsSql_UsesTheStoredValuesOfTheEnumsItReads()
    {
        // The migration holds the integers of LeaseStatus.Draft and Rejected: keep them in step with the enum.
        Assert.Equal(0, (int)Core.Entities.Enums.LeaseStatus.Draft);
        Assert.Equal(7, (int)Core.Entities.Enums.LeaseStatus.Rejected);
        Assert.Contains("NOT IN (0, 7)", AddPropertyRentalMode.SignalsSql);
        Assert.Contains("\"RentalMode\" = 1", AddPropertyRentalMode.BackfillSql);
        Assert.Equal(1, (int)Core.Entities.Enums.RentalMode.Long);
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string RunbooksFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder."),
            "docs",
            "runbooks");
    }
}

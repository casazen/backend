using Casazen.Infrastructure.Data.Configurations;
using Casazen.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The rules the database holds for the ISTAT region table (E21/F01/US01/T01), the parts of them that need no database to be proven. What a real PostgreSQL
/// refuses (the rows themselves) is in <c>IstatRegionRulesPostgresTests</c>.
/// </summary>
public class IstatRegionRuleSchemaTests
{
    /// <summary>
    /// The migration <c>FixIstatDeadlineCheck</c> puts the text of the model into the table: a monthly deadline must have its day, written as <c>IS NOT NULL</c>
    /// because a check passes when its condition is NULL (<c>NULL BETWEEN 1 AND 31</c> is NULL, so a monthly row with no day was stored). Its Down puts the
    /// old text back, and the model, the snapshot and this migration agree (the pending-model-changes check of the build covers the snapshot).
    /// </summary>
    [Fact]
    public void FixIstatDeadlineCheck_RefusesAMonthlyDeadlineWithNoDay_AsTheModelSays()
    {
        var migration = new FixIstatDeadlineCheck();

        var drop = Assert.Single(migration.UpOperations.OfType<DropCheckConstraintOperation>());
        var add = Assert.Single(migration.UpOperations.OfType<AddCheckConstraintOperation>());
        Assert.Equal((IstatRegionRuleConfiguration.DeadlineCheckName, "IstatRegionRules"), (drop.Name, drop.Table));
        Assert.Equal((IstatRegionRuleConfiguration.DeadlineCheckName, "IstatRegionRules"), (add.Name, add.Table));
        Assert.Equal(IstatRegionRuleConfiguration.DeadlineCheckSql, add.Sql);

        // The monthly branch names the null day itself, and the other branch still demands none.
        Assert.Contains("(\"DeadlineKind\" = 2 AND \"DeadlineDay\" IS NOT NULL AND \"DeadlineDay\" BETWEEN 1 AND 31)", add.Sql, StringComparison.Ordinal);
        Assert.Contains("(\"DeadlineKind\" <> 2 AND \"DeadlineDay\" IS NULL)", add.Sql, StringComparison.Ordinal);

        var back = Assert.Single(migration.DownOperations.OfType<AddCheckConstraintOperation>());
        Assert.Equal(IstatRegionRuleConfiguration.DeadlineCheckName, back.Name);
        Assert.DoesNotContain("IS NOT NULL", back.Sql, StringComparison.Ordinal);
        Assert.Single(migration.DownOperations.OfType<DropCheckConstraintOperation>());
    }
}

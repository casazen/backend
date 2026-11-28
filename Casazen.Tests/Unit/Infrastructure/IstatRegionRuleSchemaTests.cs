using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
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

        var repair = Assert.IsType<SqlOperation>(migration.UpOperations[0]);
        Assert.Contains("SET \"DeadlineKind\" = 0 WHERE \"DeadlineKind\" = 2 AND \"DeadlineDay\" IS NULL", repair.Sql, StringComparison.Ordinal);

        var drop = Assert.Single(migration.UpOperations.OfType<DropCheckConstraintOperation>());
        var add = Assert.Single(migration.UpOperations.OfType<AddCheckConstraintOperation>());
        Assert.Equal((IstatRegionRuleConfiguration.DeadlineCheckName, "IstatRegionRules"), (drop.Name, drop.Table));
        Assert.Equal((IstatRegionRuleConfiguration.DeadlineCheckName, "IstatRegionRules"), (add.Name, add.Table));
        Assert.Equal(IstatRegionRuleConfiguration.DeadlineCheckSql, add.Sql);

        Assert.Contains("(\"DeadlineKind\" = 2 AND \"DeadlineDay\" IS NOT NULL AND \"DeadlineDay\" BETWEEN 1 AND 31)", add.Sql, StringComparison.Ordinal);
        Assert.Contains("(\"DeadlineKind\" <> 2 AND \"DeadlineDay\" IS NULL)", add.Sql, StringComparison.Ordinal);

        var back = Assert.Single(migration.DownOperations.OfType<AddCheckConstraintOperation>());
        Assert.Equal(IstatRegionRuleConfiguration.DeadlineCheckName, back.Name);
        Assert.DoesNotContain("IS NOT NULL", back.Sql, StringComparison.Ordinal);
        Assert.Single(migration.DownOperations.OfType<DropCheckConstraintOperation>());
    }

    [Fact]
    public void RequiredGuestFields_TheCheckTheResolverAndTheGuestFieldLists_NameTheSameFields()
    {
        var groups = Regex.Matches(IstatRegionRuleConfiguration.RequiredFieldsCheckSql, @"\(([A-Za-z]+(?:\|[A-Za-z]+)+)\)")
            .Select(match => match.Groups[1].Value.Split('|'))
            .ToList();

        Assert.Equal(2, groups.Count);
        var expected = IstatGuestFieldLists.ByField.Keys.Order(StringComparer.Ordinal).ToList();
        Assert.All(groups, names => Assert.Equal(expected, names.Order(StringComparer.Ordinal)));
        Assert.Equal(expected, IstatRegionResolver.FieldNames.Order(StringComparer.Ordinal));
        Assert.Equal(IstatRegionResolver.FieldNames.Count, IstatRegionResolver.FieldNames.Distinct(StringComparer.Ordinal).Count());

        var all = IstatRegionResolver.FormatFields(IstatRegionResolver.FieldNames.ToList());
        Assert.Equal(string.Join(',', IstatRegionResolver.FieldNames), all);
        Assert.True(all.Length <= IstatRegionRule.RequiredGuestFieldsMaxLength, "The three names do not fit RequiredGuestFields.");
        var pattern = new Regex(@"'(\^.*\$)'").Match(IstatRegionRuleConfiguration.RequiredFieldsCheckSql).Groups[1].Value;
        Assert.Matches(pattern, all);
        Assert.Matches(pattern, string.Empty);
        Assert.DoesNotMatch(pattern, "TourismType,Hobby");
        Assert.DoesNotMatch(pattern, "tourismtype");
    }
}

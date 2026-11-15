using System.Data;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The reads of the wire page (FIX/RES-PAYOUTS) on the PostgreSQL provider with no server (see <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET
/// does and accepts what Npgsql cannot turn into SQL: the list of the payable payouts is a join of three tables with a <c>NOT EXISTS</c> on the payouts of the batches and a
/// comparison on the trimmed upper-case currency, and the payouts of a batch a join of four. A query that has no translation fails here, before the CI, and not as a 500 of the
/// wire page; and the SQL is read for the org on every table, which InMemory (one org per test) cannot tell.
/// </summary>
public class WirePayableReadsNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid BatchId = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    // The org as a parameter of the statement (EF names it @orgId or @__orgId_0 by its version), never a literal and never the global filter alone.
    private static readonly Regex OrgPredicate = new(@"""OrgId"" = @(__)?orgId", RegexOptions.Compiled);

    /// <summary>The batch the first statement of the read selects: one row, with no state of download or payment.</summary>
    private static DataTable? OneBatch(string sql)
    {
        if (!sql.Contains("FROM \"WireBatches\"", StringComparison.Ordinal))
            return null;

        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Currency", typeof(string));
        table.Columns.Add("Status", typeof(int));
        table.Columns.Add("TransferCount", typeof(int));
        table.Columns.Add("TotalAmount", typeof(decimal));
        table.Columns.Add("ExecutionDate", typeof(DateTime));
        table.Columns.Add("MessageFormat", typeof(string));
        table.Columns.Add("CreatedAt", typeof(DateTime));
        table.Columns.Add("DownloadedAt", typeof(DateTime));
        table.Columns.Add("PaidAt", typeof(DateTime));
        table.Rows.Add(BatchId, "CHF", 0, 0, 0m, DBNull.Value, DBNull.Value, new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), DBNull.Value, DBNull.Value);
        return table;
    }

    [Fact]
    public async Task PayablePayouts_AreTranslatedToSqlByNpgsql_WithTheOrgOnEveryTable_Test()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListPayablePayoutsAsync(OrgId, "CHF"));

        var sql = Assert.Single(statements);
        Assert.Contains("NOT EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"WireBatchPayouts\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"PublishedAt\" IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);

        // The payouts, their versions, their owners and the payouts of the batches: four tables, the org on each of them.
        Assert.True(OrgPredicate.Matches(sql).Count >= 4, $"the org is not asked of every table:{Environment.NewLine}{sql}");
    }

    [Fact]
    public async Task BatchPayouts_AreTranslatedToSqlByNpgsql_WithTheOrgOnEveryTable_Test()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: OneBatch);
        var service = new WireTransferService(db, NullLogger<WireTransferService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.GetBatchPayoutsAsync(OrgId, BatchId));

        // The batch, then its payouts: the probe must have got as far as the second statement, or it proved nothing about it.
        Assert.True(statements.Count == 2, $"{statements.Count} of the 2 reads were reached:{Environment.NewLine}{string.Join(Environment.NewLine, statements)}");
        Assert.True(OrgPredicate.IsMatch(statements[0]), $"the batch is not read for its org:{Environment.NewLine}{statements[0]}");
        Assert.DoesNotContain("XmlContent", statements[0], StringComparison.Ordinal);
        Assert.DoesNotContain("DebitAccount", statements[0], StringComparison.Ordinal);
        Assert.True(OrgPredicate.Matches(statements[1]).Count >= 4, $"the org is not asked of every table:{Environment.NewLine}{statements[1]}");
    }
}

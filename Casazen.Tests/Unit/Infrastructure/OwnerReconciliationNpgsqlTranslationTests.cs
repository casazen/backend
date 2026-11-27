using System.Data;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E13/F01/US03/T02: the reads of the reconciliation of a month, run on the PostgreSQL provider without a server (see <see cref="NpgsqlTranslationProbe"/>). The proofs of the
/// reconciliation run on InMemory, which evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL; this fails here, before the CI, when one of its
/// queries (the lines of the month, their reversals, the bookings and the payments they come from, the stays of the month, their posted revenue, the published versions) has no
/// translation. The probe answers the lines of the month and the stays with one row each, so that the reads that need an id of theirs are reached.
/// </summary>
public class OwnerReconciliationNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("3f0c8d41-6b7e-4a52-9d1c-5e2a7b8c9d10");

    [Fact]
    public async Task Reconciliation_EveryReadIsTranslatedToSqlByNpgsql_Test()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: Answer);
        var service = new OwnerReconciliationService(db, NullLogger<OwnerReconciliationService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.GetAsync(OrgId, 2026, 10));

        // The lines, their reversals, the bookings and the payments they come from, the stays of the month, the revenue posted for them and the published versions.
        Assert.True(statements.Count >= 7, $"only {statements.Count} of the 7 reads were reached:{Environment.NewLine}{string.Join(Environment.NewLine, statements)}");
    }

    /// <summary>One line of the owner ledger and one stay of the month, which let the reads that follow them be sent.</summary>
    private static DataTable? Answer(string sql)
    {
        if (sql.Contains("FROM \"OwnerLedgerEntries\"", StringComparison.Ordinal) && sql.Contains("\"RuleVersion\"", StringComparison.Ordinal))
        {
            var line = new DataTable();
            line.Columns.Add("Id", typeof(Guid));
            line.Columns.Add("PropertyOwnerId", typeof(Guid));
            line.Columns.Add("EntryType", typeof(int));
            line.Columns.Add("SourceType", typeof(int));
            line.Columns.Add("SourceId", typeof(Guid));
            line.Columns.Add("SourceReference", typeof(string));
            line.Columns.Add("OriginalCurrency", typeof(string));
            line.Columns.Add("OriginalAmount", typeof(decimal));
            line.Columns.Add("StatementCurrency", typeof(string));
            line.Columns.Add("Amount", typeof(decimal));
            line.Columns.Add("RuleVersion", typeof(string));
            line.Columns.Add("ReversesEntryId", typeof(Guid));
            line.Columns.Add("PostedAt", typeof(DateTime));
            line.Rows.Add(
                Guid.NewGuid(), Guid.NewGuid(), (int)OwnerLedgerEntryType.BookingRevenue, (int)OwnerLedgerSourceType.Booking, Guid.NewGuid(), DBNull.Value, "CHF", 100m, "CHF", 100m,
                DBNull.Value, DBNull.Value, DateTime.UtcNow);
            return line;
        }

        if (sql.Contains("FROM \"Bookings\"", StringComparison.Ordinal) && sql.Contains("\"CheckOutDate\"", StringComparison.Ordinal) && sql.Contains("\"BasePrice\"", StringComparison.Ordinal))
        {
            var stay = new DataTable();
            stay.Columns.Add("Id", typeof(Guid));
            stay.Columns.Add("Source", typeof(int));
            stay.Columns.Add("Currency", typeof(string));
            stay.Columns.Add("BasePrice", typeof(decimal));
            stay.Columns.Add("TotalPrice", typeof(decimal));
            stay.Columns.Add("TouristTax", typeof(decimal));
            stay.Rows.Add(Guid.NewGuid(), (int)BookingSource.Direct, "CHF", 100m, 100m, 0m);
            return stay;
        }

        return null;
    }
}

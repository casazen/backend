using System.Data;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The read of the completed cleanings (E24/F01/US01/T03) on the PostgreSQL provider with no server (see <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does
/// and accepts what Npgsql cannot turn into SQL, and the date a cost became firm is a <c>COALESCE</c> over a conditional (<c>FinalAmountConfirmedAt</c>, the payment of a request the host paid
/// with the confirmation still open, the completion), used in the two bounds of the period and in the projection: a query that has no translation fails here, before the CI, and not as a
/// 500 of the invoicing of the owners. The probe answers the properties of the org with one row, so that the requests and the staff work, which need its id, are reached.
/// </summary>
public class CleaningCostSourceNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>The first statement of the read (the properties of the org with their currency) answers one row; the others answer none.</summary>
    private static DataTable? OneProperty(string sql)
    {
        var isPropertiesRead = sql.Contains("FROM \"Properties\"", StringComparison.Ordinal)
            && !sql.Contains("\"ServiceRequests\"", StringComparison.Ordinal)
            && !sql.Contains("\"InternalActivities\"", StringComparison.Ordinal);
        if (!isPropertiesRead)
            return null;

        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Currency", typeof(string));
        table.Rows.Add(PropertyId, "EUR");
        return table;
    }

    /// <summary>With every property of the org and with a list of them: the properties, the requests and the staff work are three statements, and all three are translated.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCompleted_IsTranslatedToSqlByNpgsql(bool withProperties)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: OneProperty);
        var source = new CleaningCostSource(db);
        IReadOnlyCollection<Guid>? propertyIds = withProperties ? [PropertyId] : null;

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => source.GetCompletedAsync(OrgId, propertyIds, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), CancellationToken.None));

        // The probe must have got as far as the requests and the staff work that follow the properties, or it proved nothing about them.
        Assert.True(
            statements.Count >= 3,
            $"only {statements.Count} of the 3 reads were reached:{Environment.NewLine}{string.Join(Environment.NewLine, statements)}");
        Assert.Contains(
            statements,
            sql => sql.Contains("\"ServiceRequests\"", StringComparison.Ordinal) && sql.Contains("COALESCE", StringComparison.OrdinalIgnoreCase));
    }
}

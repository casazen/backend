using System.Data.Common;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-05: the statement that moves the windows of a duplicate supplier to the keeper (<c>ExecuteUpdate</c>, PostgreSQL only: the
/// in-memory provider cannot run it) is compiled on the Npgsql provider without a server. The interceptors skip the connection
/// and the execution and keep the SQL text, so a construct that does not translate fails here instead of in the repair on the
/// first merge. Its effect on real rows is proved by <c>SupplierAgendaRepairPostgresTests</c> (CI).
/// </summary>
public class SupplierMergeWindowsSqlTests
{
    [Fact]
    public async Task MovingTheWindows_IsOneUpdateOfTheDuplicatesRows_ThatSkipsTheFeedEngagementsTheKeeperHas()
    {
        var capture = new SqlCapture();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Port=1;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .AddInterceptors(capture)
                .Options);
        var keeper = Guid.NewGuid();
        var duplicate = Guid.NewGuid();

        await SupplierService.WindowsThatMove(db, keeper, duplicate)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.OrgId, keeper));

        var sql = Assert.Single(capture.Statements);
        Assert.StartsWith("UPDATE \"SupplierBusyWindows\"", sql, StringComparison.Ordinal);
        Assert.Contains("SET \"OrgId\" = ", sql, StringComparison.Ordinal);
        // Only the duplicate's rows, and of those the ones with no UID (set by hand) or no twin at the keeper.
        Assert.Matches("WHERE \\w+\\.\"OrgId\" = @", sql);
        Assert.Contains("\"ExternalUid\" IS NULL OR NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Matches("\\.\"ExternalUid\" = \\w+\\.\"ExternalUid\"", sql);
        Assert.Matches("\\.\"StartUtc\" = \\w+\\.\"StartUtc\"", sql);
    }

    /// <summary>Keeps the text of what would be sent, and sends nothing.</summary>
    private sealed class SqlCapture : DbCommandInterceptor, IDbConnectionInterceptor
    {
        private readonly List<string> _statements = [];

        public IReadOnlyList<string> Statements => _statements;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText);
            return new ValueTask<InterceptionResult<int>>(InterceptionResult<int>.SuppressWithResult(0));
        }

        InterceptionResult IDbConnectionInterceptor.ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result) =>
            InterceptionResult.Suppress();

        ValueTask<InterceptionResult> IDbConnectionInterceptor.ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken) =>
            new(InterceptionResult.Suppress());
    }
}

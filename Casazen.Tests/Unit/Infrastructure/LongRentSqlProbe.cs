using System.Data;
using System.Data.Common;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// A context on the real PostgreSQL provider (Npgsql) that never reaches a server: the connection is not opened and every command
/// is recorded and answered from a table the test gives (nothing, by default). What it proves is what EF InMemory cannot: that
/// each LINQ query of the code under test is <b>translated to SQL by Npgsql</b> (a query that cannot be translated fails when it
/// is compiled, before any command is sent), how many statements a call sends, and the shape of each. The rows themselves are
/// asserted elsewhere (InMemory, and PostgreSQL in CI).
/// </summary>
internal static class LongRentSqlProbe
{
    public static AppDbContext NewContext(List<string> statements, Func<string, DataTable?>? answer = null) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=1;Database=casazen_probe;Username=probe;Password=probe",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .AddInterceptors(new NoConnection(), new Recorder(statements, answer))
            .Options);

    /// <summary>A one-row table with the given columns, for the statements that need a row to let the code go on.</summary>
    public static DataTable Row(params (string Name, Type Type, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type, _) in columns)
            table.Columns.Add(name, type);
        table.Rows.Add(columns.Select(c => c.Value).ToArray());
        return table;
    }

    public static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class Recorder(List<string> statements, Func<string, DataTable?>? answer) : DbCommandInterceptor
    {
        private DbDataReader Answer(DbCommand command)
        {
            lock (statements)
                statements.Add(command.CommandText);
            return (answer?.Invoke(command.CommandText) ?? new DataTable()).CreateDataReader();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command)));
    }
}

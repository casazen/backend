using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// A context on the real PostgreSQL provider that never reaches a server: the connection is not opened and every command
/// answers with no rows, but for the few shapes that need one row to let the code go on (a count, an exists, a sum, the org
/// of a report). What it proves is the part InMemory cannot: that each LINQ query of the code under test is <b>translated to SQL
/// by Npgsql</b> (a query that cannot be translated fails when it is compiled, before any command is sent), including the ones
/// that follow the first one in a service method. Used by the tests that run the services of the host lists, whose result sets
/// are asserted elsewhere (InMemory, and PostgreSQL in CI).
/// </summary>
internal static class NpgsqlTranslationProbe
{
    public static AppDbContext NewContext(List<string>? statements = null) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=1;Database=casazen_probe;Username=probe;Password=probe",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .AddInterceptors(new NoConnection(), new FewRows(statements))
            .Options);

    /// <summary>
    /// Runs <paramref name="call"/> and fails when a query of it could not be translated. Anything else it throws is the
    /// empty result sets meeting code that expects data, which is not what is probed here.
    /// </summary>
    public static async Task AssertTranslatesAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception exception) when (!IsTranslationFailure(exception))
        {
            // Empty results (a single that is missing, an org that is not there): beyond the translation.
        }
    }

    private static bool IsTranslationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException && current.Message.Contains("could not be translated", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The one row some statements must answer with: 0 of a count, false of an exists, 0 of a sum, the org of a report.</summary>
    private static DataTable? RowFor(string sql)
    {
        var text = sql.TrimStart();
        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (Regex.IsMatch(text, @"^SELECT\s+COUNT\(\*\)::int\b", Ci))
            return Single(typeof(int), 0);
        if (Regex.IsMatch(text, @"^SELECT\s+COUNT\(", Ci))
            return Single(typeof(long), 0L);
        if (Regex.IsMatch(text, @"^SELECT\s+EXISTS\b", Ci))
            return Single(typeof(bool), false);
        if (Regex.IsMatch(text, @"^SELECT\s+(COALESCE\()?sum\(", Ci))
            return Single(typeof(decimal), 0m);
        if (Regex.IsMatch(text, @"^SELECT\s+o\.""Name"", o\.""DisplayName"", o\.""FiscalCode""\s+FROM ""Orgs"""))
        {
            var org = new DataTable();
            org.Columns.Add("Name", typeof(string));
            org.Columns.Add("DisplayName", typeof(string));
            org.Columns.Add("FiscalCode", typeof(string));
            org.Rows.Add("Org", "Org", DBNull.Value);
            return org;
        }

        return null;
    }

    private static DataTable Single(Type type, object value)
    {
        var table = new DataTable();
        table.Columns.Add("value", type);
        table.Rows.Add(value);
        return table;
    }

    private static DbDataReader ReaderFor(DbCommand command) =>
        (RowFor(command.CommandText) ?? new DataTable()).CreateDataReader();

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class FewRows(List<string>? statements) : DbCommandInterceptor
    {
        private DbDataReader Answer(DbCommand command)
        {
            statements?.Add(command.CommandText);

            return ReaderFor(command);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command)));

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result) =>
            InterceptionResult<object>.SuppressWithResult(0);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<object>.SuppressWithResult(0));
    }
}

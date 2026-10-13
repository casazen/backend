using Npgsql;

namespace Casazen.Web.Configuration;

/// <summary>
/// How many PostgreSQL connections this process may open, and whether the connection string can run the API at all
/// (HOSTING). The free Supabase <b>session pooler</b> grants about 15 session connections per user and database
/// (<b>not confirmed on the official documentation</b>: check Database settings, "Pool size", in the dashboard); before
/// this setting the API opened 26 at rest (Hangfire's 20 default workers + EF), so a deploy ended in
/// <c>EMAXCONNSESSION</c>.
/// <list type="bullet">
///   <item><c>Hangfire:WorkerCount</c>: Hangfire workers, default <see cref="DefaultWorkerCount"/> (Hangfire's own default is 20).</item>
///   <item><c>Database:MaxPoolSize</c> / <c>Database:MinPoolSize</c>: Npgsql pool of the EF Core contexts (requests, jobs),
///   default <see cref="DefaultMaxPoolSize"/> / 0. A <c>Maximum Pool Size</c> / <c>Minimum Pool Size</c> written in the
///   connection string wins over these settings (use it for a direct database).</item>
///   <item><c>Database:HangfireMaxPoolSize</c>: pool of Hangfire's storage. It has its own pool (<c>Application Name</c>
///   <see cref="HangfireApplicationName"/>), so the budget is the sum of the two and a busy queue cannot starve the requests.
///   Default <c>2 x WorkerCount</c>: a worker holds the connection of the job it fetched and, while a
///   <c>[DisableConcurrentExecution]</c> job runs, a second one for the distributed lock.</item>
///   <item><c>Database:ConnectionBudget</c>: connections the server grants to this user. Default: 15 on the Supabase session
///   pooler, 60 on the Supabase direct host, unchecked elsewhere (0 = unchecked).</item>
/// </list>
/// Advisory locks (<see cref="Casazen.Infrastructure.Data.PostgresAdvisoryLocks"/>) and Hangfire's locks need a
/// <b>session</b> connection: a transaction pooler (Supabase port 6543) silently breaks them, so the startup is refused
/// outside Development and Testing and the readiness check is <c>degraded</c> there. Runbook:
/// <c>docs/runbooks/free-hosting-analysis.md</c>, <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public sealed record DatabaseConnectionSettings(
    string ConnectionString,
    int WorkerCount,
    int MaxPoolSize,
    int MinPoolSize,
    int HangfireMaxPoolSize,
    int ConnectionBudget,
    bool HangfireRegistered)
{
    public const string WorkerCountKey = "Hangfire:WorkerCount";
    public const string MaxPoolSizeKey = "Database:MaxPoolSize";
    public const string MinPoolSizeKey = "Database:MinPoolSize";
    public const string HangfireMaxPoolSizeKey = "Database:HangfireMaxPoolSize";
    public const string ConnectionBudgetKey = "Database:ConnectionBudget";

    public const int DefaultWorkerCount = 4;
    public const int DefaultMaxPoolSize = 6;
    public const int SupabaseSessionPoolerBudget = 15;
    public const int SupabaseDirectBudget = 60;
    public const string HangfireApplicationName = "casazen-hangfire";
    public const string AppApplicationName = "casazen-api";

    /// <summary>Supavisor's transaction mode port. Session mode is 5432.</summary>
    public const int TransactionPoolerPort = 6543;

    private const int MaxWorkerCount = 50;
    private const int MaxPoolSizeLimit = 500;

    /// <summary>
    /// Connections the process may open at the same time at most: the EF pool plus the Hangfire pool
    /// (when Hangfire runs). One more is never taken: the startup migration uses the EF pool.
    /// </summary>
    public int TheoreticalMaxConnections => MaxPoolSizeEffective + (HangfireRegistered ? HangfireMaxPoolSizeEffective : 0);

    /// <summary>
    /// Effective pool of the EF contexts: the connection string's own <c>Maximum Pool Size</c> wins over <see cref="MaxPoolSize"/>.
    /// It then applies to each of the two pools.
    /// </summary>
    public int MaxPoolSizeEffective => ExplicitInString("Maximum Pool Size") ?? MaxPoolSize;

    public int HangfireMaxPoolSizeEffective => ExplicitInString("Maximum Pool Size") ?? HangfireMaxPoolSize;

    public bool IsTransactionPooler => IsTransactionPoolerEndpoint(ConnectionString);

    /// <summary>Connection string of the EF Core contexts (pool settings and application name applied).</summary>
    public string ForEntityFramework() => Build(MaxPoolSize, MinPoolSize, AppApplicationName);

    /// <summary>Connection string of Hangfire's storage: same database, its own pool.</summary>
    public string ForHangfire() => Build(HangfireMaxPoolSize, 0, HangfireApplicationName);

    public static DatabaseConnectionSettings Resolve(IConfiguration configuration, string connectionString, bool hangfireRegistered)
    {
        var workers = ReadInt(configuration, WorkerCountKey, DefaultWorkerCount, 1, MaxWorkerCount);
        var maxPool = ReadInt(configuration, MaxPoolSizeKey, DefaultMaxPoolSize, 1, MaxPoolSizeLimit);
        var minPool = ReadInt(configuration, MinPoolSizeKey, 0, 0, MaxPoolSizeLimit);
        var hangfirePool = ReadInt(configuration, HangfireMaxPoolSizeKey, workers * 2, 1, MaxPoolSizeLimit);
        if (minPool > maxPool)
        {
            throw new InvalidOperationException(
                $"{MinPoolSizeKey} ({minPool}) must not exceed {MaxPoolSizeKey} ({maxPool}).");
        }

        var budget = ReadInt(configuration, ConnectionBudgetKey, DetectBudget(connectionString), 0, 10_000);
        return new DatabaseConnectionSettings(connectionString, workers, maxPool, minPool, hangfirePool, budget, hangfireRegistered);
    }

    /// <summary>
    /// What is wrong with this configuration, naming settings and never values (no host, user or password).
    /// Empty when it is fine. <see cref="Severity.Error"/> items make the startup fail outside Development and Testing.
    /// </summary>
    public IReadOnlyList<Problem> Evaluate()
    {
        var problems = new List<Problem>();

        if (IsTransactionPooler)
        {
            problems.Add(new Problem(
                Severity.Error,
                $"The connection string points to the transaction pooler (port {TransactionPoolerPort}): PostgreSQL advisory " +
                "locks and Hangfire's locks need a session connection and silently stop working. Use the session pooler " +
                "(port 5432, host *.pooler.supabase.com, user postgres.<project-ref>) or the direct host. See docs/runbooks/free-hosting-analysis.md."));
        }

        if (HangfireRegistered && HangfireMaxPoolSizeEffective < WorkerCount + 2)
        {
            problems.Add(new Problem(
                Severity.Warning,
                $"The Hangfire connection pool ({HangfireMaxPoolSizeKey}={HangfireMaxPoolSizeEffective}) is smaller than " +
                $"{WorkerCountKey}+2 ({WorkerCount + 2}): workers would wait for a connection and jobs would time out."));
        }

        if (ConnectionBudget > 0 && TheoreticalMaxConnections > ConnectionBudget)
        {
            problems.Add(new Problem(
                Severity.Warning,
                $"The API may open {TheoreticalMaxConnections} connections ({MaxPoolSizeKey} + {HangfireMaxPoolSizeKey}) but the " +
                $"database grants about {ConnectionBudget} ({ConnectionBudgetKey}): the server would answer EMAXCONNSESSION. " +
                $"Lower {WorkerCountKey}, {MaxPoolSizeKey} or {HangfireMaxPoolSizeKey}, or raise the pool size in the Supabase dashboard."));
        }
        else if (ConnectionBudget > 0 && TheoreticalMaxConnections > ConnectionBudget - 1)
        {
            problems.Add(new Problem(
                Severity.Warning,
                $"The API may use all {ConnectionBudget} connections the database grants: a deploy overlap, psql or the " +
                "migration tool would be refused. Keep at least one connection free."));
        }

        return problems;
    }

    /// <summary>Throws when <see cref="Evaluate"/> finds an error (called outside Development and Testing).</summary>
    public void EnsureUsable()
    {
        var errors = Evaluate().Where(p => p.Severity == Severity.Error).Select(p => p.Message).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException("Database connection configuration refused: " + string.Join(" ", errors));
    }

    /// <summary>True for a Supavisor/PgBouncer transaction-mode endpoint (port 6543).</summary>
    public static bool IsTransactionPoolerEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Port == TransactionPoolerPort;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int DetectBudget(string connectionString)
    {
        try
        {
            var host = new NpgsqlConnectionStringBuilder(connectionString).Host ?? string.Empty;
            if (host.Contains(".pooler.supabase.", StringComparison.OrdinalIgnoreCase))
                return SupabaseSessionPoolerBudget;
            if (host.StartsWith("db.", StringComparison.OrdinalIgnoreCase) && host.Contains(".supabase.", StringComparison.OrdinalIgnoreCase))
                return SupabaseDirectBudget;
        }
        catch (ArgumentException)
        {
            // An unparsable connection string fails elsewhere, with its own message.
        }

        return 0;
    }

    private int? ExplicitInString(string key)
    {
        if (!IsSetInString(ConnectionString, key))
            return null;

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            return key == "Maximum Pool Size" ? builder.MaxPoolSize : builder.MinPoolSize;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when the key (or one of its Npgsql synonyms) is written in the connection string, whatever its value.</summary>
    private static bool IsSetInString(string connectionString, string key)
    {
        string[] names = key == "Maximum Pool Size"
            ? ["Maximum Pool Size", "MaxPoolSize"]
            : ["Minimum Pool Size", "MinPoolSize"];
        try
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
            return names.Any(builder.ContainsKey);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string Build(int maxPoolSize, int minPoolSize, string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (!IsSetInString(ConnectionString, "Maximum Pool Size"))
            builder.MaxPoolSize = maxPoolSize;
        if (!IsSetInString(ConnectionString, "Minimum Pool Size"))
            builder.MinPoolSize = Math.Min(minPoolSize, builder.MaxPoolSize);
        // Always distinct: the two pools are separate (Npgsql pools by connection string) and show up by name in pg_stat_activity.
        builder.ApplicationName = applicationName;
        return builder.ConnectionString;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"{key} '{raw}' must be a whole number between {min} and {max}.");
        }

        return value;
    }

    public enum Severity
    {
        Warning,
        Error,
    }

    public sealed record Problem(Severity Severity, string Message);
}

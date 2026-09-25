using System.Text.RegularExpressions;
using Hangfire.PostgreSql;
using Npgsql;

namespace Casazen.Web.Configuration;

/// <summary>
/// Where Hangfire keeps its queue, recurring jobs, servers and locks (FD-11, A9-03).
/// <para>
/// Test and production share one Supabase database and differ only by the EF <c>SearchPath</c>
/// (<c>casazen_test</c> / <c>casazen_prod</c>). A fixed Hangfire schema would give both environments the same
/// queue, the same recurring jobs and the same servers, so every environment gets its own schema:
/// </para>
/// <list type="number">
///   <item><c>Hangfire:Schema</c> when set (never the shared <c>hangfire</c> schema when a SearchPath is set);</item>
///   <item>otherwise <c>hangfire_&lt;first SearchPath schema&gt;</c>, e.g. <c>hangfire_casazen_prod</c>;</item>
///   <item>otherwise, outside Production, <c>hangfire_&lt;environment&gt;</c>, e.g. <c>hangfire_development</c>;</item>
///   <item>otherwise (Production with neither) startup fails: the schema would be ambiguous.</item>
/// </list>
/// Runbook: <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public sealed partial record HangfireStorageSettings(string Schema, TimeSpan DistributedLockTimeout)
{
    public const string SchemaKey = "Hangfire:Schema";
    public const string DistributedLockTimeoutKey = "Hangfire:DistributedLockTimeoutMinutes";

    /// <summary>The schema every environment used before FD-11: shared by test and production.</summary>
    public const string SharedLegacySchema = "hangfire";

    public const string DerivedSchemaPrefix = "hangfire_";

    /// <summary>
    /// How long a distributed lock (e.g. <c>[DisableConcurrentExecution]</c>) may be held before the storage
    /// considers it abandoned and lets another run take it. It must exceed the longest expected job run, and it
    /// is also the longest a lock survives a crashed worker. Hangfire.PostgreSql's own default is 10 minutes,
    /// shorter than a slow iCal sync.
    /// </summary>
    public static readonly TimeSpan DefaultDistributedLockTimeout = TimeSpan.FromMinutes(30);

    private const int MaxIdentifierLength = 63;

    /// <summary>Resolves the settings for this environment; throws <see cref="InvalidOperationException"/> on ambiguous or invalid config.</summary>
    public static HangfireStorageSettings Resolve(IConfiguration configuration, string connectionString, IHostEnvironment environment) =>
        new(
            ResolveSchema(configuration[SchemaKey], connectionString, environment.EnvironmentName),
            ResolveDistributedLockTimeout(configuration[DistributedLockTimeoutKey]));

    public static string ResolveSchema(string? configuredSchema, string? connectionString, string environmentName)
    {
        var searchPathSchema = GetSearchPathSchema(connectionString);

        if (!string.IsNullOrWhiteSpace(configuredSchema))
        {
            var schema = configuredSchema.Trim();
            if (!IsValidSchemaName(schema))
            {
                throw new InvalidOperationException(
                    $"{SchemaKey} '{schema}' is not a valid schema name: use lowercase letters, digits and '_', " +
                    $"not starting with a digit, at most {MaxIdentifierLength} characters.");
            }

            if (searchPathSchema is not null && schema == SharedLegacySchema)
            {
                throw new InvalidOperationException(
                    $"{SchemaKey} '{SharedLegacySchema}' is the Hangfire schema shared by every environment of this database " +
                    $"(SearchPath={searchPathSchema}): test and production would consume the same queue. " +
                    $"Use a schema dedicated to this environment, e.g. '{DerivedSchemaPrefix}{searchPathSchema}'.");
            }

            return schema;
        }

        if (searchPathSchema is not null)
        {
            var derived = DerivedSchemaPrefix + searchPathSchema;
            if (!IsValidSchemaName(derived))
            {
                throw new InvalidOperationException(
                    $"Cannot derive the Hangfire schema from SearchPath '{searchPathSchema}': set {SchemaKey} explicitly.");
            }

            return derived;
        }

        if (string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Hangfire schema is ambiguous: the environment is Production, the connection string has no SearchPath " +
                $"and {SchemaKey} is not set. Set Hangfire__Schema (e.g. 'hangfire_casazen_prod'), different for test " +
                "and production. See docs/runbooks/hangfire.md.");
        }

        var fromEnvironment = DerivedSchemaPrefix + NonIdentifierChars().Replace(environmentName.Trim().ToLowerInvariant(), "_");
        if (!IsValidSchemaName(fromEnvironment))
        {
            throw new InvalidOperationException(
                $"Cannot derive the Hangfire schema from the environment name '{environmentName}': set {SchemaKey} explicitly.");
        }

        return fromEnvironment;
    }

    public static TimeSpan ResolveDistributedLockTimeout(string? configuredMinutes)
    {
        if (string.IsNullOrWhiteSpace(configuredMinutes))
            return DefaultDistributedLockTimeout;

        if (!int.TryParse(configuredMinutes.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes < 1)
        {
            throw new InvalidOperationException(
                $"{DistributedLockTimeoutKey} '{configuredMinutes}' must be a whole number of minutes greater than zero.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// First schema of the connection string's search path (<c>SearchPath</c>, or <c>-c search_path=…</c> in
    /// <c>Options</c>), lowercased like PostgreSQL folds unquoted identifiers; <c>null</c> when none is set.
    /// </summary>
    public static string? GetSearchPathSchema(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var searchPath = builder.SearchPath;
        if (string.IsNullOrWhiteSpace(searchPath) && !string.IsNullOrWhiteSpace(builder.Options))
        {
            var match = OptionsSearchPath().Match(builder.Options);
            searchPath = match.Success ? match.Groups[1].Value : null;
        }

        if (string.IsNullOrWhiteSpace(searchPath))
            return null;

        return searchPath
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('"').Trim().ToLowerInvariant())
            .FirstOrDefault(s => s.Length > 0 && s != "$user");
    }

    /// <summary>Storage options for <c>UsePostgreSqlStorage</c>.</summary>
    public PostgreSqlStorageOptions CreateStorageOptions() => new()
    {
        SchemaName = Schema,
        DistributedLockTimeout = DistributedLockTimeout,
    };

    private static bool IsValidSchemaName(string schema) =>
        schema.Length <= MaxIdentifierLength && SchemaName().IsMatch(schema);

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();

    [GeneratedRegex("[^a-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifierChars();

    [GeneratedRegex(@"search_path\s*=\s*([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OptionsSearchPath();
}

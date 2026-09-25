using DotNet.Testcontainers.Configurations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Resolves the PostgreSQL server used by integration tests (FD-04, A9-11):
/// <list type="number">
///   <item><c>TEST_POSTGRES_CONNECTION</c> (local cluster or the CI <c>postgres:16</c> service);</item>
///   <item>otherwise a Testcontainers <c>postgres:16-alpine</c> container shared by the whole test run, when Docker is reachable;</item>
///   <item>otherwise none: the web factory falls back to EF InMemory (local runs only, with a warning) and
///         PostgreSQL-only tests are skipped with an explicit reason. On CI a missing server is an error.</item>
/// </list>
/// Each consumer creates its own throw-away database on this server (see <see cref="PostgresTestDatabase"/>).
/// </summary>
public static class PostgresTestServer
{
    public const string ConnectionVariable = "TEST_POSTGRES_CONNECTION";

    private const string ContainerImage = "postgres:16-alpine";

    private static readonly Lazy<string?> UnavailableReasonLazy = new(ResolveUnavailableReason);
    private static readonly Lazy<string> ServerConnectionStringLazy =
        new(ResolveServerConnectionString, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary><c>null</c> when a PostgreSQL server is configured or can be started; otherwise the reason why not.</summary>
    public static string? UnavailableReason => UnavailableReasonLazy.Value;

    public static bool IsAvailable => UnavailableReason is null;

    /// <summary>True on CI runners, where falling back to InMemory or skipping PostgreSQL tests is not allowed.</summary>
    public static bool IsContinuousIntegration =>
        IsTrue(Environment.GetEnvironmentVariable("CI")) || IsTrue(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    /// <summary>
    /// Connection string of the server's maintenance database (<c>postgres</c>). Starts the shared
    /// Testcontainers instance on first use when <c>TEST_POSTGRES_CONNECTION</c> is not set.
    /// </summary>
    public static string ServerConnectionString => ServerConnectionStringLazy.Value;

    private static string? ResolveUnavailableReason()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            return null;

        try
        {
            if (TestcontainersSettings.OS.DockerEndpointAuthConfig is not null)
                return null;
        }
        catch (Exception ex)
        {
            return $"{ConnectionVariable} is not set and Docker could not be probed for Testcontainers ({ex.GetType().Name}: {ex.Message})";
        }

        return $"{ConnectionVariable} is not set and Docker is not reachable for Testcontainers";
    }

    private static string ResolveServerConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        string connectionString;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            connectionString = configured;
        }
        else
        {
            if (UnavailableReason is { } reason)
                throw new InvalidOperationException($"No PostgreSQL server for integration tests: {reason}.");

            var container = new PostgreSqlBuilder(ContainerImage).Build();
            // Start on the thread pool: blocking on xUnit's synchronization context could deadlock.
            Task.Run(() => container.StartAsync()).GetAwaiter().GetResult();
            // The Testcontainers resource reaper (Ryuk) removes the container when the test process exits.
            connectionString = container.GetConnectionString();
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
}

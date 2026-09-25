using Xunit;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// A fact that needs a real PostgreSQL server (<see cref="PostgresTestServer"/>). When no server is
/// available on a local run it is reported as skipped with the reason; on CI it always runs (and fails
/// if PostgreSQL is missing), so the skip can never hide a regression in the pipeline.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestServer.IsContinuousIntegration && PostgresTestServer.UnavailableReason is { } reason)
            Skip = $"No PostgreSQL available: {reason}. Set {PostgresTestServer.ConnectionVariable} or start Docker.";
    }
}

using Xunit;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// The theory counterpart of <see cref="PostgresFactAttribute"/>: skipped with the reason on a local run without
/// PostgreSQL, always run on CI.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresTestServer.IsContinuousIntegration && PostgresTestServer.UnavailableReason is { } reason)
            Skip = $"No PostgreSQL available: {reason}. Set {PostgresTestServer.ConnectionVariable} or start Docker.";
    }
}

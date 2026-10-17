using Casazen.Infrastructure.Services.LeaseDrafts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-12: what the database says when a change of a draft cannot take its row lock, or loses a race, is a draft that is busy (409 for a
/// person, skip for the retention job), never a 500. The lock itself and the wait are proved on PostgreSQL (<c>LeaseDraftPostgresTests</c>).
/// </summary>
public class LeaseDraftLockingTests
{
    private static PostgresException Postgres(string sqlState) => new("the database said no", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData(PostgresErrorCodes.LockNotAvailable)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    public void TheLockTimeout_TheLostRace_AndTheDeadlock_AreContention_HoweverTheyAreWrapped(string sqlState)
    {
        Assert.True(LeaseDraftLocking.IsContention(Postgres(sqlState)));
        Assert.True(LeaseDraftLocking.IsContention(new DbUpdateException("save failed", Postgres(sqlState))));
        // EF reports a transient failure as an InvalidOperationException around it.
        Assert.True(LeaseDraftLocking.IsContention(new InvalidOperationException("transient failure", Postgres(sqlState))));
    }

    [Fact]
    public void ACommandThatTimedOutWhileItWaited_IsContention()
    {
        Assert.True(LeaseDraftLocking.IsContention(new NpgsqlException("Exception while reading from stream", new TimeoutException())));
        Assert.True(LeaseDraftLocking.IsContention(new TimeoutException()));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData(PostgresErrorCodes.UndefinedTable)]
    public void AnyOtherDatabaseError_IsNotContention_ItIsAFailureNobodyShouldHide(string sqlState)
    {
        Assert.False(LeaseDraftLocking.IsContention(Postgres(sqlState)));
        Assert.False(LeaseDraftLocking.IsContention(new DbUpdateException("save failed", Postgres(sqlState))));
    }

    [Fact]
    public void AnythingThatIsNotTheDatabase_IsNotContention()
    {
        Assert.False(LeaseDraftLocking.IsContention(new InvalidOperationException("not a lock")));
        Assert.False(LeaseDraftLocking.IsContention(new OperationCanceledException()));
    }

    [Fact]
    public void TheWaitForTheLock_IsAFewSecondsAndLocalToTheTransaction()
    {
        Assert.Equal("SET LOCAL lock_timeout = '5s'", LeaseDraftLocking.SetLockTimeoutSql);
    }
}

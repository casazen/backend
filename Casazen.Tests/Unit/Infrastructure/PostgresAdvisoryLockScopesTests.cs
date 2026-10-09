using Casazen.Infrastructure.Data;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The scopes of the PostgreSQL advisory locks are numbers that every feature picks on its own, and a C# enum accepts two
/// names with the same number without a word. Two scopes with one number would serialize each other's unrelated work (and
/// could make two features wait for each other), so a number is used by one scope only. The branches of the redesign wave
/// each add scopes to <c>PostgresAdvisoryLocks.Scope</c>: this fails when two of them picked the same number.
/// </summary>
public class PostgresAdvisoryLockScopesTests
{
    [Fact]
    public void Scope_EveryNumberBelongsToOneScopeOnly()
    {
        var shared = Enum.GetNames<PostgresAdvisoryLocks.Scope>()
            .GroupBy(name => (int)Enum.Parse<PostgresAdvisoryLocks.Scope>(name))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group)}")
            .ToList();

        Assert.True(shared.Count == 0, "Advisory lock scopes that share a number: " + string.Join("; ", shared));
    }
}

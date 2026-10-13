namespace Casazen.Tests.Unit;

/// <summary>Clock frozen at a given UTC instant, for tests on the calendar "today" (FD-06).</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

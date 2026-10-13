namespace Casazen.Tests.Unit;

/// <summary>
/// Clock under the test's control (FD-06): starts at a given UTC instant and moves only with <see cref="Advance"/> or
/// <see cref="SetUtcNow"/>, e.g. to simulate the hourly runs of a recurring job.
/// </summary>
public sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _utcNow;
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
            _utcNow += delta;
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
            _utcNow = value;
    }
}

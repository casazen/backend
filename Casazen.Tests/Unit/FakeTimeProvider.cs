namespace Casazen.Tests.Unit;

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

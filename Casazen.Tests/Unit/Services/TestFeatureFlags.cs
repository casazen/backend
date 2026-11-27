using Casazen.Core.Features;

namespace Casazen.Tests.Unit.Services;

internal sealed class TestFeatureFlags : IFeatureFlags
{
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);

    public TestFeatureFlags(params string[] enabled)
    {
        foreach (var flag in enabled)
            _enabled.Add(flag);
    }

    public bool IsEnabled(string flag)
    {
        lock (_enabled)
            return _enabled.Contains(flag);
    }

    public void Set(string flag, bool enabled)
    {
        lock (_enabled)
        {
            if (enabled)
                _enabled.Add(flag);
            else
                _enabled.Remove(flag);
        }
    }
}

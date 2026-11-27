namespace Casazen.Core.Features;

public interface IFeatureFlags
{
    bool IsEnabled(string flag);
}

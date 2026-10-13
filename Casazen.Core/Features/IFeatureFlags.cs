namespace Casazen.Core.Features;

/// <summary>Reads the feature flags (<see cref="FeatureFlags"/>) at the time of the call.</summary>
public interface IFeatureFlags
{
    /// <summary>True only when <c>Features:{flag}</c> is set to <c>true</c>; missing or invalid means off.</summary>
    bool IsEnabled(string flag);
}

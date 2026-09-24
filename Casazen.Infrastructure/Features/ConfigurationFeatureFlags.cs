using Casazen.Core.Features;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Features;

/// <summary><see cref="IFeatureFlags"/> backed by the <c>Features</c> configuration section.</summary>
public sealed class ConfigurationFeatureFlags(IConfiguration configuration) : IFeatureFlags
{
    public bool IsEnabled(string flag) => IsEnabled(configuration, flag);

    /// <summary>Same rule for the code that runs before the service provider exists (service registration).</summary>
    public static bool IsEnabled(IConfiguration configuration, string flag) =>
        bool.TryParse(configuration[$"{FeatureFlags.SectionName}:{flag}"], out var enabled) && enabled;
}

using Casazen.Core.Features;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Features;

public sealed class ConfigurationFeatureFlags(IConfiguration configuration) : IFeatureFlags
{
    public bool IsEnabled(string flag) => IsEnabled(configuration, flag);

    public static bool IsEnabled(IConfiguration configuration, string flag) =>
        bool.TryParse(configuration[$"{FeatureFlags.SectionName}:{flag}"], out var enabled) && enabled;
}

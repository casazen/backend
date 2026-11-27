using Casazen.Core.Services;

namespace Casazen.Web.Demo;

/// <summary>The demo environment exists only outside Production.</summary>
public sealed class HostDemoEnvironmentGate(IHostEnvironment environment) : IDemoEnvironmentGate
{
    public bool IsAvailable => !environment.IsProduction();
}

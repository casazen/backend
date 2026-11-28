using Casazen.Core.Services;

namespace Casazen.Web.Demo;

public sealed class HostDemoEnvironmentGate(IHostEnvironment environment) : IDemoEnvironmentGate
{
    public bool IsAvailable => !environment.IsProduction();
}

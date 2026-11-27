using Casazen.Core.Services;
using Microsoft.Extensions.Hosting;

namespace Casazen.Web.Demo;

public sealed class HostDemoEnvironmentGate(IHostEnvironment environment) : IDemoEnvironmentGate
{
    public bool IsAvailable => !environment.IsProduction();
}

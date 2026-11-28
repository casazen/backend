using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services;

internal static class CrmTestIndex
{
    public const string Key = "0123456789abcdef0123456789abcdef-crm-test-key";

    public static ICrmContactIndex Create(string? key = Key, string environment = "Production") =>
        new CrmContactIndex(
            Options.Create(new CrmOptions { ContactIndexKey = key }),
            Mock.Of<IHostEnvironment>(host => host.EnvironmentName == environment),
            NullLogger<CrmContactIndex>.Instance);
}

using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>The keyed index of the CRM (<c>Crm:ContactIndexKey</c>) as the tests of the suppression list and of the unsubscribe links build it.</summary>
internal static class CrmTestIndex
{
    /// <summary>A key of the length the validator asks for.</summary>
    public const string Key = "0123456789abcdef0123456789abcdef-crm-test-key";

    /// <summary>A real <see cref="CrmContactIndex"/> over <paramref name="key"/> (null: not configured) in <paramref name="environment"/>.</summary>
    public static ICrmContactIndex Create(string? key = Key, string environment = "Production") =>
        new CrmContactIndex(
            Options.Create(new CrmOptions { ContactIndexKey = key }),
            Mock.Of<IHostEnvironment>(host => host.EnvironmentName == environment),
            NullLogger<CrmContactIndex>.Instance);
}

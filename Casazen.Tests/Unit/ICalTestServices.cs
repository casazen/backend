using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit;

/// <summary>iCal services wired for unit tests (PC-10): default import window, no real network.</summary>
internal static class ICalTestServices
{
    public static ICalImportService ImportService(TimeProvider? clock = null, ICalImportOptions? options = null) =>
        new(clock ?? TimeProvider.System, Options.Create(options ?? new ICalImportOptions()));

    public static PropertyICalSyncService PropertySync(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        IConfiguration configuration,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<PropertyICalSyncService>? logger = null,
        ICalImportOptions? importOptions = null,
        TimeProvider? clock = null)
    {
        var options = Options.Create(importOptions ?? new ICalImportOptions());
        return new(
            db,
            externalHttpClient,
            new ICalImportService(TimeProvider.System, options),
            new ICalExportService(),
            scopeFactory ?? Mock.Of<IServiceScopeFactory>(),
            configuration,
            options,
            logger ?? Mock.Of<ILogger<PropertyICalSyncService>>(),
            clock ?? TimeProvider.System);
    }
}

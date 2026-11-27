using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

public static class SupplierConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSupplierConsoleServices(this IServiceCollection services)
    {
        services.AddScoped<ISupplierPaymentReadModels, SupplierPaymentReadModels>();
        services.AddScoped<ISupplierEarningsService, SupplierEarningsService>();
        services.AddScoped<ISupplierKpiService, SupplierKpiService>();
        services.AddScoped<ISupplierTodayService, SupplierTodayService>();
        services.AddScoped<ISupplierServiceRequestReader, SupplierServiceRequestReader>();
        services.AddScoped<IHostServiceRequestReader, HostServiceRequestReader>();
        services.AddScoped<ISupplierClientsService, SupplierClientsService>();
        services.AddOptions<Casazen.Core.Options.SupplierRankingOptions>()
            .BindConfiguration(Casazen.Core.Options.SupplierRankingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.SupplierRankingOptions>, Casazen.Core.Options.SupplierRankingOptionsValidator>();
        services.AddScoped<ISupplierReliabilityService, SupplierReliabilityService>();
        services.AddScoped<ISupplierMatchService, SupplierMatchService>();
        services.AddScoped<IServiceRequestAssistService, ServiceRequestAssistService>();

        return services;
    }
}

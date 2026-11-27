using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;

namespace Casazen.Web.Extensions;

public static class AccountantPackageServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenAccountantPackageServices(this IServiceCollection services)
    {
        services.AddScoped<IAgencySummaryService, AgencySummaryService>();
        services.AddScoped<IAccountantPackageService, AccountantPackageService>();
        services.AddScoped<IAccountantPackageSettingsService, AccountantPackageSettingsService>();
        services.AddScoped<IAccountantPackageDeliveryService, AccountantPackageDelivery>();
        services.AddScoped<AccountantPackageMonthlyJob>();
        services.AddSingleton<ILegalDocumentService, LegalDocumentService>();
        services.AddSingleton<ICheckInLegalTexts, CheckInLegalTexts>();

        return services;
    }
}

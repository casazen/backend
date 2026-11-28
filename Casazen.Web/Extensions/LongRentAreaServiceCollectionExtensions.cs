using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The read side of the long-term area: the rent register, the bank statement import, the agenda, the fiscal report, the tenants and the properties
/// of the area.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class LongRentAreaServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenLongRentAreaServices(this IServiceCollection services)
    {
        services.AddScoped<IRentRegisterService, RentRegisterService>();
        services.AddScoped<IRentBankImportService, RentBankImportService>();
        services.AddScoped<ILongRentAgendaService, LongRentAgendaService>();
        services.AddScoped<ILongRentFiscalReportService, LongRentFiscalReportService>();
        services.AddScoped<ILongRentTenantService, LongRentTenantService>();
        services.AddScoped<ILongRentPropertyService, LongRentPropertyService>();

        return services;
    }
}

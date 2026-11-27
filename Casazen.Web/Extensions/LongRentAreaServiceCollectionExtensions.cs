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
        // Rent register, agenda and overview of the long-term area (LR-01): read side, scope per property in SQL.
        services.AddScoped<IRentRegisterService, RentRegisterService>();
        // Import of a bank statement on the rents: credits matched to the installments to collect, confirmed through the offline payment.
        services.AddScoped<IRentBankImportService, RentBankImportService>();
        services.AddScoped<ILongRentAgendaService, LongRentAgendaService>();
        // Fiscal report per lease for the accountant (LR-11b): the rules of the tax advisory (LT-08) on the leases of the scope.
        services.AddScoped<ILongRentFiscalReportService, LongRentFiscalReportService>();
        // The tenants of the area (LR-09): the people read from the parties of the leases, the copy of their data and their erasure.
        services.AddScoped<ILongRentTenantService, LongRentTenantService>();
        // The properties of the area with the APE, the cadastral data, who holds them and the change of mode programmed (LR-07a).
        services.AddScoped<ILongRentPropertyService, LongRentPropertyService>();

        return services;
    }
}

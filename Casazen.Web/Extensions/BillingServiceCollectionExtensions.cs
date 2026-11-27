using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The entitlement, the Stripe billing of the plans, the platform invoices, the rent billing and SEPA, the condominium accounting, the bank link and
/// the lease maintenance.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class BillingServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenBillingServices(this IServiceCollection services)
    {
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IStripeBillingService, StripeBillingService>();
        services.AddScoped<IBillingCheckoutService, BillingCheckoutService>();
        // PL-13: VAT computed by Stripe Tax (no rate in code); the SaaS e-invoices are issued manually (health check "einvoicing").
        // The port to the SDI, ISdiEInvoiceProvider, is registered with the fiscal documents
        // (AddCasazenFiscalDocuments, E17/F01/US02): the adapter of the provider when Sdi:Provider is configured, else the unconfigured one.
        services.AddScoped<IPlatformInvoiceService, PlatformInvoiceService>();
        services.AddScoped<IBillingEntryGate, BillingEntryGate>();
        services.AddScoped<IRentBillingService, RentBillingService>();
        services.AddScoped<ITenantRentSepaService, TenantRentSepaService>();
        services.AddScoped<CondominiumPaymentReminderService>();
        services.AddScoped<IStripeRentSepaGateway, StripeRentSepaGateway>();
        services.AddScoped<ICondominiumAccountingService, CondominiumAccountingService>();
        services.AddScoped<ITenantCondominiumSettlementService, TenantCondominiumSettlementService>();
        services.AddScoped<ITenantExpenseProposalService, TenantExpenseProposalService>();
        services.AddScoped<ICondominiumHistoricalImportService, CondominiumHistoricalImportService>();
        services.AddScoped<IAdministratorStatementReaderService, AdministratorStatementReaderService>();
        services.AddScoped<IStripeBankLinkGateway, StripeBankLinkGateway>();
        services.AddScoped<ILeaseMaintenanceService, LeaseMaintenanceService>();
        services.AddScoped<ILeaseMaintenancePaymentApplier>(sp => sp.GetRequiredService<ILeaseMaintenanceService>());
        services.AddScoped<IBankLinkService, BankLinkService>();

        return services;
    }
}

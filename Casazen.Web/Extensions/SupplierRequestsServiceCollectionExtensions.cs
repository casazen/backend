using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

public static class SupplierRequestsServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSupplierRequestServices(this IServiceCollection services)
    {
        services.AddScoped<IServiceRequestRepository, ServiceRequestRepository>();
        services.AddOptions<Casazen.Core.Options.ServiceRequestOptions>()
            .BindConfiguration(Casazen.Core.Options.ServiceRequestOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ServiceRequestOptions>, Casazen.Core.Options.ServiceRequestOptionsValidator>();
        services.AddScoped<ServiceRequestNotifier>();
        // Payment of the service requests inside CasaZen (SP-15a, decision D2): direct charge on the supplier's Stripe account with
        // the platform commission. The commission and the times are configuration (SupplierPayments), validated at startup: the
        // percentage has no default in code. The gateway is the only place that sets the application fee.
        services.AddOptions<Casazen.Core.Options.SupplierPaymentsOptions>()
            .BindConfiguration(Casazen.Core.Options.SupplierPaymentsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.SupplierPaymentsOptions>, Casazen.Core.Options.SupplierPaymentsOptionsValidator>();
        services.AddSingleton<ISupplierPaymentGateway, StripeSupplierPaymentGateway>();
        services.AddSingleton<IPlatformPaymentGateway, StripePlatformPaymentGateway>();
        // The payment service holds the payer's side (SP-15a) and, in its own interfaces, the Stripe webhook, the jobs and the admin
        // refunds (SP-15b) and the cycle of the money CasaZen holds (SP-15c-a): one instance per scope, so they share the payment lock
        // and the way a link is issued.
        services.AddScoped<SupplierPaymentService>();
        services.AddScoped<ISupplierPaymentService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentWebhookService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentJobService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentRefundService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierFundsService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierFundsJobService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        // The complaint about a job paid through CasaZen and its dispute (SP-15c-c, decision VT-D16) act on the same payment under the same
        // lock, so they are the same instance. The penalty of a lost dispute goes to a sink that only logs until the reliability events of the
        // ranking are connected (VT-D17): replace it, do not add to it.
        services.AddScoped<IServiceDisputeService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<IServiceDisputeJobService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierDisputePenalties, LoggingSupplierDisputePenalties>();
        services.AddScoped<ISupplierPaymentAdminService, SupplierPaymentAdminService>();
        services.AddScoped<ISupplierPaymentJobScheduler, SupplierPaymentJobScheduler>();
        services.AddScoped<ServiceRequestService>();
        services.AddScoped<IServiceRequestService>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IShowcaseRequestCustomerActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IServiceRequestThreadActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IServiceRequestOfferActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IServiceRequestOfferCompletion>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IServiceRequestAutoCancelService, ServiceRequestAutoCancelService>();
        services.AddScoped<ServiceRequestBackupSelector>();
        services.AddScoped<IServiceRequestGroupRunService, ServiceRequestGroupRunService>();
        services.AddScoped<IHostSupplierUsageReader, HostSupplierUsageReader>();

        return services;
    }
}

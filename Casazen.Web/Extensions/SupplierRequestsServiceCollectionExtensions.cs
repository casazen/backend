using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The service requests of the suppliers and their payments inside CasaZen: the request service and its notifier, the payment gateways and service,
/// the disputes, the automatic cancellation, the backup suppliers and the usage reader.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class SupplierRequestsServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSupplierRequestServices(this IServiceCollection services)
    {
        services.AddScoped<IServiceRequestRepository, ServiceRequestRepository>();
        // Service requests with their time and price (SP-04): the emails and pushes to the two parties, the lifecycle after the
        // take, the photos of the work; the times and the tolerance are configuration (Suppliers:ServiceRequests), validated at startup.
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
        // The money CasaZen holds (SP-15c-a, decision VT-D1): the other gateway of the supplier payments, on the platform account and with no
        // application fee at all.
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
        // One instance per request for both doors (SP-11): the hosts' and the suppliers' operations, and the narrow set of what the
        // customer of a public showcase does to its own request, which only the customer's area (IShowcaseBookingManager) uses.
        services.AddScoped<ServiceRequestService>();
        services.AddScoped<IServiceRequestService>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IShowcaseRequestCustomerActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        // The conversation of a request on measure (SP-10b): the supplier reads the thread and writes in it.
        services.AddScoped<IServiceRequestThreadActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IServiceRequestOfferActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IServiceRequestOfferCompletion>(provider => provider.GetRequiredService<ServiceRequestService>());
        // Automatic cancellation of the requests nobody answered (SP-04, D8): the recurring job runs only with the flag on.
        services.AddScoped<IServiceRequestAutoCancelService, ServiceRequestAutoCancelService>();
        // Backup suppliers of a host's request (SP-BKP, flag SupplierRequestBackups for the creation of groups only): the choice of the backups
        // and the always-on pass over the groups that follows their windows.
        services.AddScoped<ServiceRequestBackupSelector>();
        services.AddScoped<IServiceRequestGroupRunService, ServiceRequestGroupRunService>();
        // The suppliers a host already asked for a job, for «Le mie app» of the marketplace (MKT, SR-09c): a view of its requests.
        services.AddScoped<IHostSupplierUsageReader, HostSupplierUsageReader>();

        return services;
    }
}

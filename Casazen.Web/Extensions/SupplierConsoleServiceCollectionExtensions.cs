using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The supplier console: the payment read models, the earnings, the KPIs, the home, the inbox, the host list of interventions, the clients, the
/// ranking and the matching of the suppliers.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class SupplierConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenSupplierConsoleServices(this IServiceCollection services)
    {
        // The reads of the payments of a request for the supplier console: the summary of a job, the counters and the figures of the
        // home (SP-04e). The only reader of the payment table outside the payments service.
        services.AddScoped<ISupplierPaymentReadModels, SupplierPaymentReadModels>();
        // The supplier's earnings, the page "Incassi": its completed and paid jobs with gross, commission and net, the figures above them and
        // the CSV for its accountant (SP-16a).
        services.AddScoped<ISupplierEarningsService, SupplierEarningsService>();
        // Supplier dashboard KPIs from the service requests (SU-11, A4-15), the money of the console home (SP-04) and its overview (SP-04e).
        services.AddScoped<ISupplierKpiService, SupplierKpiService>();
        // The home ("Oggi") and the activation checklist of the supplier console (SP-04).
        services.AddScoped<ISupplierTodayService, SupplierTodayService>();
        // Supplier inbox, history and request detail: address, date and host contact after the take (SU-08, A4-14).
        services.AddScoped<ISupplierServiceRequestReader, SupplierServiceRequestReader>();
        // The host's list of interventions with the counters of its chips, and what a request says to its host: the stay, the contact of
        // the supplier and the action to do (SR-08a).
        services.AddScoped<IHostServiceRequestReader, HostServiceRequestReader>();
        // The supplier's customers, the page "Clienti": hosts and private customers of the showcase aggregated from the requests (SP-16a).
        services.AddScoped<ISupplierClientsService, SupplierClientsService>();
        // The ranking of the suppliers (SP-REL): who is recommended, the order of the list a host chooses from and, with the backups, who
        // is called. The weights are configuration (SupplierRanking), validated at startup, every one with a default; the events the
        // reliability is made of (answers in time, jobs without a dispute, missed deadlines, disputes) are recorded by the request flow.
        services.AddOptions<Casazen.Core.Options.SupplierRankingOptions>()
            .BindConfiguration(Casazen.Core.Options.SupplierRankingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.SupplierRankingOptions>, Casazen.Core.Options.SupplierRankingOptionsValidator>();
        services.AddScoped<ISupplierReliabilityService, SupplierReliabilityService>();
        services.AddScoped<ISupplierMatchService, SupplierMatchService>();
        // «Descrivi di cosa hai bisogno» of the guided request: the AI reads the host's words and proposes the service, the urgency and who to ask
        // (WIZ-AI-REQUEST). Shown only while an AI provider is in effect; the request is created by the wizard as always.
        services.AddScoped<IServiceRequestAssistService, ServiceRequestAssistService>();

        return services;
    }
}

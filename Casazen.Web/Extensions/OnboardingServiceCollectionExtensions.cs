using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the activation of CasaZen with Octorate (epic E10, NF-104..NF-107) is registered: the flow status service, its
/// providers and the reader of the mapping. Every E10 task adds its registrations here, none in <c>ServiceCollectionExtensions</c>;
/// <c>Program.cs</c> calls it once. Nothing here is conditional: what is not ready yet sits behind <c>Features:OctorateOnboarding</c> where
/// it is used, not by being left out of DI.
/// </summary>
public static class OnboardingServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOnboarding(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The state of the six flows on each property (E10/F01/US01/T02): computed on every read, nothing is written.
        services.AddScoped<IOnboardingFlowStatusService, OnboardingFlowStatusService>();

        // The mapping, read through the mapping service of E02 and never from its tables; the prerequisites the flows share.
        services.AddScoped<IPropertyMappingReader, ChannelMappingReader>();
        services.AddScoped<FlowPrerequisiteReader>();

        // One provider for each flow that exists. A flow nobody has built yet (messages, documents, reports) has the placeholder that
        // answers «not provided» (order 0): E05, E06 and E07 register their own provider with a higher order and it stops being asked.
        services.AddScoped<IFlowReadinessProvider, ListingsFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, BookingsFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, CheckInComplianceFlowProvider>();
        services.AddScoped<IFlowReadinessProvider>(_ => new NotProvidedFlowProvider(OnboardingFlow.Messages));
        services.AddScoped<IFlowReadinessProvider>(_ => new NotProvidedFlowProvider(OnboardingFlow.Documents));
        services.AddScoped<IFlowReadinessProvider>(_ => new NotProvidedFlowProvider(OnboardingFlow.Reports));

        // The plan of the eleven parts, the path of UX-10 and the deferral (E10/F01/US01/T03). One evaluator for each part that exists; the parts
        // nobody has built yet (messages and AI, fiscal documents, partners) have the placeholder that answers «not provided» (order 0): E05, E06
        // and E09 register their own evaluator with a higher order and it stops being asked.
        services.AddScoped<IOnboardingPlanService, OnboardingPlanService>();
        services.AddScoped<IOnboardingPartEvaluator, OctorateConnectionPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, PropertiesPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, ChannelsPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, ListingsPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, PricesRulesPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, CompliancePartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, IdentificationPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, ActivationPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator>(_ => new NotProvidedPartEvaluator(OnboardingPartKey.MessagesAi));
        services.AddScoped<IOnboardingPartEvaluator>(_ => new NotProvidedPartEvaluator(OnboardingPartKey.FiscalDocuments));
        services.AddScoped<IOnboardingPartEvaluator>(_ => new NotProvidedPartEvaluator(OnboardingPartKey.Partners));

        // The activation of the flows, the reader of the active flows for the consumers, the final read-only test with one check for each flow
        // that has one (the others are skipped as «not provided»), and the hourly "To do" sync (E10/F01/US01/T04).
        services.Configure<OnboardingOptions>(configuration.GetSection(OnboardingOptions.SectionName));
        services.AddScoped<IOnboardingFlowActivationService, OnboardingFlowActivationService>();
        services.AddScoped<IFlowActivationReader, FlowActivationReader>();
        services.AddScoped<IOnboardingFlowTestRunService, OnboardingFlowTestRunService>();
        services.AddScoped<IFlowDryRunCheck, ListingsDryRunCheck>();
        services.AddScoped<IFlowDryRunCheck, BookingsDryRunCheck>();
        services.AddScoped<IFlowDryRunCheck, CheckInComplianceDryRunCheck>();
        services.AddScoped<OnboardingFlowTodoSyncJob>();

        // The guided steps of the Octorate panel and «Do it for me» (E10/F01/US03/T01): the steps with their verification, the requests with
        // the staff queue. One default verifier for each kind of step (order 0); an epic that can read more registers its own for the same kind
        // with a higher order (the marketplace epic, E09/F02/US01/T02, for the access to a channel) and it is the one that runs.
        services.AddScoped<IOnboardingPanelStepService, OnboardingPanelStepService>();
        services.AddScoped<IOnboardingAssistService, OnboardingAssistService>();
        services.AddScoped<IPanelStepVerifier, PoliceConnectionPanelVerifier>();
        services.AddScoped<IPanelStepVerifier, IstatConnectionPanelVerifier>();
        services.AddScoped<IPanelStepVerifier, ChannelAccessDefaultPanelVerifier>();

        // The integration settings (E10/F01/US02/T01, NF-108): the overview of the connection and of what can be reopened, and the reopen of a
        // part. It reads the facts the other epics store and clears a deferral through the plan; it adds no path that changes a sender.
        services.AddScoped<IOnboardingIntegrationService, OnboardingIntegrationService>();

        // The bulk configuration of an agency (E10/F02/US01/T01, NF-105): a choice applied to many properties through the sender switch of E03, the
        // progress («142 of 250 ready»), the pilot group and the progressive activation through the activation service above; and the import of
        // the data that changes per property from a CSV or Excel file (T02), read and validated, never stored.
        services.AddScoped<IOnboardingBulkService, OnboardingBulkService>();
        services.AddScoped<IOnboardingBulkImportService, OnboardingBulkImportService>();

        // The owner portfolio of an agency (E10/F02/US02/T01, NF-109): the import of the owners and of their shares of the properties from a CSV
        // or Excel file, and the readiness of every owner and property for the owner accounting and the portal. It never sends an invitation.
        services.AddScoped<IOwnerOnboardingService, OwnerOnboardingService>();

        return services;
    }
}

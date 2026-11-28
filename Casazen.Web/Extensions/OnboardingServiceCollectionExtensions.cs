using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Infrastructure;

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

        services.AddScoped<IOnboardingFlowStatusService, OnboardingFlowStatusService>();

        services.AddScoped<IPropertyMappingReader, ChannelMappingReader>();
        services.AddScoped<FlowPrerequisiteReader>();

        // One provider for each of the six flows (order 10). The messages, documents and reports flows read the facts of the epics that own them
        // (E05, E06, E07) through the providers of E10/F01/US04/T01, which replace the placeholders that answered «not provided» (order 0): a flow
        // is never ready from a flag, only from what is stored. NotProvidedFlowProvider stays as the answer for a flow that has no provider.
        services.AddScoped<IFlowReadinessProvider, ListingsFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, BookingsFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, CheckInComplianceFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, MessagesFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, DocumentsFlowProvider>();
        services.AddScoped<IFlowReadinessProvider, ReportsFlowProvider>();

        services.AddScoped<IOnboardingPlanService, OnboardingPlanService>();
        services.AddScoped<IOnboardingPartEvaluator, OctorateConnectionPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, PropertiesPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, ChannelsPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, ListingsPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, PricesRulesPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, CompliancePartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, IdentificationPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, ActivationPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, MessagesAiPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, FiscalDocumentsPartEvaluator>();
        services.AddScoped<IOnboardingPartEvaluator, PartnersPartEvaluator>();

        services.AddScoped<IOnboardingPartEvaluator, GuestRegistrationPartEvaluator>();

        services.Configure<OnboardingOptions>(configuration.GetSection(OnboardingOptions.SectionName));
        services.AddScoped<IOnboardingFlowActivationService, OnboardingFlowActivationService>();
        services.AddScoped<IFlowActivationReader, FlowActivationReader>();
        services.AddScoped<IOnboardingFlowTestRunService, OnboardingFlowTestRunService>();
        services.AddScoped<IFlowDryRunCheck, ListingsDryRunCheck>();
        services.AddScoped<IFlowDryRunCheck, BookingsDryRunCheck>();
        services.AddScoped<IFlowDryRunCheck, CheckInComplianceDryRunCheck>();
        services.AddScoped<OnboardingFlowTodoSyncJob>();
        services.AddScoped<OnboardingTodoReconciler>();
        services.AddScoped<ImportedListingTodoReconciler>();

        // The guided steps of the Octorate panel and «Do it for me» (E10/F01/US03/T01): the steps with their verification, the requests with
        // the staff queue. One default verifier for each kind of step (order 0); an epic that can read more registers its own for the same kind
        // with a higher order (the marketplace epic, E09/F02/US01/T02, for the access to a channel) and it is the one that runs.
        services.AddScoped<IOnboardingPanelStepService, OnboardingPanelStepService>();
        services.AddScoped<IOnboardingAssistService, OnboardingAssistService>();
        services.AddScoped<IPanelStepVerifier, PoliceConnectionPanelVerifier>();
        services.AddScoped<IPanelStepVerifier, IstatConnectionPanelVerifier>();
        services.AddScoped<IPanelStepVerifier, ChannelAccessDefaultPanelVerifier>();

        // The support vault of «Do it for me» (E20/F03/US01/T01, decision O5): the credentials support keeps for a host and their controlled opening,
        // and the regional ISTAT credentials the host enters (write-only).
        services.AddScoped<ISupportVaultService, SupportVaultService>();
        services.AddScoped<IPropertyIstatCredentialsService, PropertyIstatCredentialsService>();

        services.AddScoped<IAssistPanelVerification, OrgScopedPanelVerification>();

        services.AddScoped<IOnboardingIntegrationService, OnboardingIntegrationService>();

        // The bulk configuration of an agency (E10/F02/US01/T01, NF-105): a choice applied to many properties through the sender switch of E03, the
        // progress («142 of 250 ready»), the pilot group and the progressive activation through the activation service above; and the import of
        // the data that changes per property from a CSV or Excel file (T02), read and validated, never stored.
        services.AddScoped<IOnboardingBulkService, OnboardingBulkService>();
        services.AddScoped<IOnboardingBulkImportService, OnboardingBulkImportService>();

        services.AddScoped<IOwnerOnboardingService, OwnerOnboardingService>();

        return services;
    }
}

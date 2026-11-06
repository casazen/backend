using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

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

        return services;
    }
}

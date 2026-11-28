using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class RoutinesServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenRoutineServices(this IServiceCollection services)
    {
        services.AddScoped<RoutineBookingActions>();
        services.AddScoped<IRoutineService, RoutineService>();
        services.AddScoped<PresetAgentTrainer>();
        services.AddScoped<IRoutineManagementService, RoutineManagementService>();
        services.AddScoped<IPropertyCleaningSetupService, PropertyCleaningSetupService>();
        services.AddScoped<ICleaningAutomationService, CleaningAutomationService>();
        services.AddScoped<ICleaningCostSource, CleaningCostSource>();
        // FIX/BE-CLEANING-VAT: the question of the VAT of a cleaning company recharged to the owner (api/service-requests/{id}/cleaning-vat, no flag).
        services.AddScoped<ICleaningVatService, CleaningVatService>();
        services.AddScoped<IDomainEventRecorder, DomainEventRecorder>();
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddScoped<IEventHandler<DomainEvent>, RoutineEventHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, ActivityAutoCloseHandler>();
        // NF-08, NF-22: the popup alerts (ADR-025). The router stores an alert for a user, holds it back during the user's quiet hours
        // unless it is a legal one, and pushes it through IPopupAlertPublisher (SignalR, registered with the push services); the job
        // popup-alert-due (RecurringJobsRegistration) pushes the ones that fell due. The user reads them at api/me/alerts.
        services.AddScoped<IAlertRouter, AlertRouter>();

        return services;
    }
}

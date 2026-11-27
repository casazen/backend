using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The automation routines and their management, the cleaning automation, the domain events (recorder, bus and handlers) and the popup alert router.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class RoutinesServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenRoutineServices(this IServiceCollection services)
    {
        // NF-26: Automation routines (ADR-022).
        services.AddScoped<RoutineBookingActions>();
        services.AddScoped<IRoutineService, RoutineService>();
        services.AddScoped<PresetAgentTrainer>();
        // E01/F04/US01/T03: the routines of an org for the routines page (api/routines, behind Features:Routines); the engine above runs them.
        services.AddScoped<IRoutineManagementService, RoutineManagementService>();
        // E24/F01/US01 (NF-120, decision O11): the default cleaner of a property with the price and the time agreed (api/properties/{id}/cleaning-setup, no flag of its own), the
        // automation of the cleaning at check-out that the routine action CreateCleaningRequest runs and the consumer of the changes of a stay follows (behind Features:Routines), and
        // the one read of the cleanings completed with their cost that the invoicing of the owners takes (T03).
        services.AddScoped<IPropertyCleaningSetupService, PropertyCleaningSetupService>();
        services.AddScoped<ICleaningAutomationService, CleaningAutomationService>();
        services.AddScoped<ICleaningCostSource, CleaningCostSource>();
        // FIX/BE-CLEANING-VAT: the question of the VAT of a cleaning company recharged to the owner (api/service-requests/{id}/cleaning-vat, no flag).
        services.AddScoped<ICleaningVatService, CleaningVatService>();
        // ADR-022: domain events are recorded in the outbox of the current unit of work (R1) and published by the dispatcher on the
        // in-process bus, which runs each handler in its own scope under the event's org (R3, R6).
        services.AddScoped<IDomainEventRecorder, DomainEventRecorder>();
        services.AddSingleton<IEventBus, InProcessEventBus>();
        // NF-21: the bus is the one way an event reaches the routines of its org (E01/F01/US01/T05).
        services.AddScoped<IEventHandler<DomainEvent>, RoutineEventHandler>();
        // ADR-026-R4: an event whose condition is met closes the "To do" items it satisfies (E04/F01/US01/T01).
        services.AddScoped<IEventHandler<DomainEvent>, ActivityAutoCloseHandler>();
        // NF-08, NF-22: the popup alerts (ADR-025). The router stores an alert for a user, holds it back during the user's quiet hours
        // unless it is a legal one, and pushes it through IPopupAlertPublisher (SignalR, registered with the push services); the job
        // popup-alert-due (RecurringJobsRegistration) pushes the ones that fell due. The user reads them at api/me/alerts.
        services.AddScoped<IAlertRouter, AlertRouter>();

        return services;
    }
}

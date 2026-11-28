using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The booking operations on top of the event bus (epic E08, ADR-022): what happens by itself when a booking is created, confirmed,
/// cancelled, checked in or out. The events are recorded by the services that change a booking, in the save of the change
/// (<see cref="BookingEvents"/>); the handlers registered here are their consumers, each an <see cref="IEventHandler{T}"/> of every
/// <see cref="DomainEvent"/> that filters on its type, runs in its own scope under the org of the event and does nothing twice for an
/// event delivered again. They are always on: the events are the switch (nothing happens until the dispatcher publishes one) and the
/// "To do" items they raise are the host's own.
/// </summary>
public static class BookingOperationsServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenBookingOperations(this IServiceCollection services)
    {
        services.AddScoped<IEventHandler<DomainEvent>, BookingCleaningActivityHandler>();

        services.AddScoped<IEventHandler<DomainEvent>, PropertyReadyAutomationHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, BookingSpecialRequestActivityHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, ChannelBookingAlertHandler>();

        services.AddScoped<IEventHandler<DomainEvent>, CleaningBookingChangesHandler>();

        services.AddScoped<IOctorateBookingTargets, OctorateBookingTargets>();

        // E08/F01/US01/T03 (NF-74, NF-80): a confirmed booking is created on Octorate once, with a state the host sees, and asked again on retry.
        services.AddScoped<IBookingOctorateSyncService, BookingOctorateSyncService>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBookingCreateHandler>();

        services.AddScoped<IEventHandler<DomainEvent>, OctoratePaymentHandler>();

        services.AddScoped<IBookingPaymentLinkService, BookingPaymentLinkService>();

        // E08/F02/US02 (NF-75, NF-76, D8): which channels let the host change or cancel a booking from CasaZen (all closed unless
        // Octorate:ChannelActions opens one), the cancellation of a channel booking asked to Octorate first, and the cancellation of a booking
        // CasaZen holds pushed to Octorate after the local one.
        services.AddOptions<OctorateChannelActionsOptions>().BindConfiguration(OctorateChannelActionsOptions.SectionName);
        services.AddSingleton(provider => new OtaChannelActionPolicy(provider.GetRequiredService<IOptions<OctorateChannelActionsOptions>>().Value));
        services.AddScoped<IOtaBookingCancellationService, OtaBookingCancellationService>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBookingCancelHandler>();

        // E08/F02/US01/T02 (NF-75, ADR-022-R4, R5): the edit of a booking CasaZen holds is written to its reservation after the local one; a conflict
        // on Octorate is kept with both versions and never overwritten silently.
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBookingUpdateHandler>();

        services.AddScoped<IChannelManagerAvailability, ChannelManagerAvailabilityService>();

        services.AddScoped<IEventHandler<DomainEvent>, BookingOverlapHandler>();

        services.AddScoped<IBookingOctorateSyncReader, BookingOctorateSyncReader>();

        services.AddScoped<IPropertyDayRuleService, PropertyDayRuleService>();

        // E08/F05/US02 (NF-36, NF-78): the changes of the calendar reach the channel manager. The recorder adds the process of a change (a manual block, a change of the
        // price and the rules, a first publication) to the unit of work of the caller; the push sends its chunks (quota, back-off, a process id or a failure, never a false
        // success); the runner follows what is due and what the channel manager accepted; the queries read it back for the host; the service previews and applies a change
        // all or nothing; the handler records the whole horizon of a listing when it is published for the first time. The job runs them in the background.
        services.AddOptions<OctorateCalendarOptions>().BindConfiguration(OctorateCalendarOptions.SectionName);
        services.AddScoped<ICalendarChangeRecorder, CalendarChangeRecorder>();
        services.AddScoped<ICalendarPushService, CalendarPushService>();
        services.AddScoped<ICalendarChangeRunner, CalendarChangeRunner>();
        services.AddScoped<ICalendarChangeQueries, CalendarChangeQueries>();
        services.AddScoped<ICalendarChangeService, CalendarChangeService>();
        services.AddScoped<CalendarChangeProcessJob>();
        services.AddScoped<IEventHandler<ListingPublishedEvent>, ListingPublishedCalendarHandler>();
        return services;
    }
}

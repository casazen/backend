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
        // E08/F06/US01/T02 (NF-79): a stay ended without a cleaning request, a booking with a special request, a booking from a channel.
        services.AddScoped<IEventHandler<DomainEvent>, BookingCleaningActivityHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, BookingSpecialRequestActivityHandler>();
        services.AddScoped<IEventHandler<DomainEvent>, ChannelBookingAlertHandler>();

        // E08/F01/US01/T02 (NF-74): where in Octorate a property books; every writer of a booking to Octorate asks it first.
        services.AddScoped<IOctorateBookingTargets, OctorateBookingTargets>();

        // E08/F01/US01/T03 (NF-74, NF-80): a confirmed booking is created on Octorate once, with a state the host sees, and asked again on retry.
        services.AddScoped<IBookingOctorateSyncService, BookingOctorateSyncService>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBookingCreateHandler>();

        // E08/F01/US01/T04 (NF-74, D9): a payment of a mirrored booking that became paid is recorded on its Octorate reservation once.
        services.AddScoped<IEventHandler<DomainEvent>, OctoratePaymentHandler>();

        // E08/F01/US01/T05 (NF-74): the payment link of a booking the host entered (the checkout page and the webhook are the checkout's).
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

        // E08/F01/US02/T01 (NF-74, NF-80): whether the channel manager can take a stay, asked before a booking of CasaZen is written and when the
        // host looks at the dates (the public calendar and availability never ask it).
        services.AddScoped<IChannelManagerAvailability, ChannelManagerAvailabilityService>();

        // E08/F01/US02/T02 (NF-80): a booking that arrives from the channel manager on taken nights raises an urgent To do and an alert, and the
        // item closes by itself when the nights are free again.
        services.AddScoped<IEventHandler<DomainEvent>, BookingOverlapHandler>();

        // E08/F03/US03/T02 (NF-74, NF-77): the state of the mirror of a booking, read for the booking answer (a read only, no push).
        services.AddScoped<IBookingOctorateSyncReader, BookingOctorateSyncReader>();

        // E08/F05/US01/T02 (NF-36, NF-78): the price and the rules of the nights of a property. The store of the rules and the read of every day of
        // the calendar; the quote, the checkout, the public calendar and the search of the booking site read the rules through it (T03, T04).
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

using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;

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

        // E08/F01/US01/T05 (NF-74): the payment link of a booking the host entered (the checkout page and the webhook are the checkout's).
        services.AddScoped<IBookingPaymentLinkService, BookingPaymentLinkService>();
        return services;
    }
}

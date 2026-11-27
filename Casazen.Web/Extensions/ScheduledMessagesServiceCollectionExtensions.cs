using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The scheduled messages of the routines (epic E05, feature F07, NF-71, ADR-022, ADR-032): the plan, its timeline, the one dispatcher that sends, the handler that follows the booking, the alerts to the hosts and the
/// jobs. An extension class of its own, so this feature never collides with the shared <c>ServiceCollectionExtensions</c> or with <c>AddMessagesHub</c>, whose list of services a test photographs;
/// <c>Program.cs</c> calls it once. Nothing here is conditional: <c>Features:ScheduledMessages</c> is read where the services act (and by the registration of the jobs), and with it off nothing is planned and
/// nothing is sent through the plan.
/// </summary>
public static class ScheduledMessagesServiceCollectionExtensions
{
    public static IServiceCollection AddScheduledMessages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F07/US01/T02: the send window and the times of the plan (section Messaging:ScheduledMessages).
        services.AddOptions<ScheduledMessageOptions>().BindConfiguration(ScheduledMessageOptions.SectionName);

        // E05/F07/US01/T02: the plan, the timeline, edit, skip and «Send now», and the door the routine engine uses; the handler that plans on a booking event and its job (behind the flag).
        services.AddScoped<IScheduledMessageService, ScheduledMessageService>();
        services.AddScoped<IEventHandler<DomainEvent>, ScheduledMessageBookingHandler>();
        services.AddScoped<ScheduledMessagePlanJob>();

        // E05/F07/US01/T03: the one place that sends, the sweep that catches up and retries, and the alerts to the hosts with the job that holds them for the quiet hours.
        services.AddScoped<IScheduledMessageDispatcher, ScheduledMessageDispatcher>();
        services.AddScoped<ScheduledMessageSweepJob>();
        services.AddScoped<ScheduledMessageAlertDelivery>();
        services.AddScoped<ScheduledMessageAlertJob>();

        return services;
    }
}

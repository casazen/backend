using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The copy of the OTA conversations (epic E05, feature F01, ADR-032): the sync service, its triggers (the handler of the chat webhook and the two
/// reconciliation jobs) and the retention of the copy. An extension class of its own, so this epic never collides with the shared
/// <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. The chat reads are methods of the client E02 already registers, and the
/// mapping of the listings is the service of <c>AddCasazenOctorate</c>. Nothing here is conditional: what is not ready sits behind
/// <c>Features:OctorateConnector</c> where it is used (the jobs are only scheduled while it is on, <c>RecurringJobsRegistration</c>).
/// </summary>
public static class MessageSyncServiceCollectionExtensions
{
    public static IServiceCollection AddMessageSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E05/F01/US01/T02 (NF-62, NF-64): the threads and messages of the channels, copied from the channel manager and kept up to date.
        services.AddScoped<IMessageSyncService, MessageSyncService>();

        // E05/F01/US01/T03 (NF-62, OCT-08, OCT-09): the chat notification of the webhook re-reads the thread; the pass of every 10 minutes and the
        // deep pass of every night recover whatever the webhook missed. The event bus runs the handler in its own scope under the org of the event.
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, ChatMessageReceivedHandler>();
        services.AddScoped<OtaMessageReconcileJob>();
        services.AddScoped<OtaMessageDeepReconcileJob>();

        // E05/F01/US01/T03 (ADR-032-R13): the copy follows the retention of the guest data; the erasure of a guest goes through GuestDataEraser.
        services.AddScoped<IOtaMessageRetentionService, OtaMessageRetentionService>();

        return services;
    }
}

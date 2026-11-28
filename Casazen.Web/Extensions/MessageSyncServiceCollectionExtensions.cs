using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The copy of the OTA conversations (epic E05, feature F01, ADR-032): the sync service, its triggers (the handler of the chat webhook and the two
/// reconciliation jobs), the retention of the copy and the guard that keeps Octorate's own assistant off the threads it reads (E05/F05/US03/T03).
/// An extension class of its own, so this epic never collides with the shared
/// <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once. The chat reads are methods of the client E02 already registers, and the
/// mapping of the listings is the service of <c>AddCasazenOctorate</c>. Nothing here is conditional: what is not ready sits behind
/// <c>Features:OctorateConnector</c> where it is used (the jobs are only scheduled while it is on, <c>RecurringJobsRegistration</c>).
/// </summary>
public static class MessageSyncServiceCollectionExtensions
{
    public static IServiceCollection AddMessageSync(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<MessageSyncPacing>();
        services.AddScoped<IMessageSyncService, MessageSyncService>();

        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, ChatMessageReceivedHandler>();
        services.AddScoped<OtaMessageReconcileJob>();
        services.AddScoped<OtaMessageDeepReconcileJob>();

        services.AddScoped<IOtaMessageRetentionService, OtaMessageRetentionService>();

        // E05/F05/US03/T03 (NF-67, OCT-11, AC-NF67-6): Octorate's own assistant is kept off on the threads CasaZen manages. The consumer of OtaThreadSynced, which
        // the copy records for a thread it read with the flag Octorate reports (at every pass while that flag reads on); it runs whenever the connector does, not
        // only when the assistant of CasaZen is on, and does nothing while Features:OctorateConnector is off. No configuration of its own.
        services.AddScoped<IEventHandler<DomainEvent>, OctorateAiAssistantGuard>();

        return services;
    }
}

using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// A guest wrote (<see cref="DomainEventType.GuestMessageReceived"/>, a conversation of a channel or of a direct booking): the assistant is asked to look at the message, in a job of its own
/// (<see cref="AssistantDraftJob"/>), so the dispatcher of the events never waits for the model (E05/F05/US02/T01, NF-67).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only behind its switches.</b> Nothing is queued while <c>Features:MessageAssistant</c> is off, nor for an organization that has not switched its own assistant on
/// (<c>OrgAssistantRules.Enabled</c>): the messages of an organization that never turned the assistant on are not looked at at all, and no row of the decision log is written for them.
/// The job asks the flag again when it runs.</item>
/// <item><b>Once.</b> A message that already has its row in the decision log queues nothing; an event delivered again before the job ran queues a second job, which finds the row the first
/// one wrote (one row per message) and does nothing.</item>
/// <item><b>The org is the event's</b> (ADR-022-R6): the job carries it, and every read of the job names it. The payload holds ids and codes (ADR-022-R21); the log carries the event and the
/// message.</item>
/// </list>
/// </remarks>
public sealed class AssistantMessageHandler(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IBackgroundJobClient jobs,
    ILogger<AssistantMessageHandler> logger) : IEventHandler<DomainEvent>
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.GuestMessageReceived || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.MessageAssistant))
            return;

        if (KeyOf(domainEvent) is not { } key)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId} does not name a conversation of a guest the assistant can read: nothing is queued",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId);
            return;
        }

        var orgId = domainEvent.OrgId;
        var messageId = domainEvent.EntityId;
        var switchedOn = await db.OrgAssistantRules
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(r => r.OrgId == orgId && r.Enabled, ct);
        if (!switchedOn)
            return;

        var processed = await db.AssistantDecisionLogs
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(l => l.OrgId == orgId && l.InboundMessageId == messageId, ct);
        if (processed)
            return;

        var threadKey = key.ToString();
        var jobId = jobs.Enqueue<AssistantDraftJob>(job => job.RunAsync(orgId, threadKey, messageId, CancellationToken.None));
        logger.LogInformation("Message {MessageId} of conversation {ThreadKey} (org {OrgId}) goes to the assistant (job {JobId})", messageId, threadKey, orgId, jobId);
    }

    /// <summary>The conversation of the payload, when it is the one its source says: <c>ota:</c> for a channel, <c>direct:</c> for the guest area.</summary>
    private static MessageThreadKey? KeyOf(DomainEvent domainEvent)
    {
        GuestMessageReceivedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GuestMessageReceivedPayload>(domainEvent.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null || !MessageThreadKey.TryParse(payload.ThreadKey, out var key))
            return null;

        return (payload.Source, key.Source) switch
        {
            (GuestMessageReceivedPayload.OtaSource, MessageThreadSource.Ota) => key,
            (GuestMessageReceivedPayload.DirectSource, MessageThreadSource.Direct) => key,
            _ => null,
        };
    }
}

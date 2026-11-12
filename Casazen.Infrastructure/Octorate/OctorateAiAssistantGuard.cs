using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Keeps the assistant of Octorate itself off on the conversations CasaZen manages (E05/F05/US03/T03, ADR-032, ADR-022, OCT-11, D4, NF-67,
/// AC-NF67-6): the consumer of <see cref="DomainEventType.OtaThreadSynced"/>, which the copy of the conversations records for every thread it
/// reads with the flag Octorate reports (<c>aiDriven</c>). CasaZen is the inbox of the threads of the channels, so a guest who gets an answer from
/// Octorate's assistant and another from CasaZen's gets two: when the flag is not off, the handler switches it off
/// (<see cref="IOctorateApiClient.SetThreadAiAssistantAsync"/>) and the copy says so.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Whenever the connector runs</b>, not only when the assistant of CasaZen is on: nothing while <c>Features:OctorateConnector</c> is off, and
/// no call for an organization that disconnected (its error is the typed one, below).</item>
/// <item><b>One call per thread per pass, and none for a thread that is off.</b> The payload says what the pass read: <c>false</c> ends the handler
/// at once; <c>true</c> or not said (a thread seen for the first time) is a call. The copy is read as well (it is the newest word on the flag): a thread
/// it already holds as off, because an earlier delivery of this event or a later pass found it so, costs no call. The call states the value it sets,
/// so a repeat is safe.</item>
/// <item><b>Verified at every pass, not once.</b> A person who switches the assistant on again in Octorate, and a call that failed, are found by the
/// next pass, which reads the flag again, records a new event while it reads on (<c>MessageSyncService.SyncedEventKey</c>: the ten minutes of the pass are
/// in the key) and so calls again. The copy is changed only after a call Octorate accepted; the pass that follows reads back what Octorate holds.</item>
/// <item><b>Only what CasaZen manages.</b> A thread with no property (its listing has no confirmed mapping) and a property whose listing is not decided
/// (<see cref="IChannelMappingService.IsWriteBlockedAsync"/>, the one definition every write to Octorate asks, OCT-04) are left as they are: CasaZen
/// does not write to a channel for them, so nothing it says can double an answer, and the host's own setting on the channel manager is not ours to change.</item>
/// <item><b>A refusal of Octorate never fails the event.</b> The calls of the org used up (the reserve of the pass is left to the people who use
/// CasaZen, <see cref="MessageSyncService.RateReserve"/>), a license without the chat module, an organization that is not connected, a thread that is
/// gone and any other typed error of Octorate end in one line of the log with the thread, the organization and the code, and the thread keeps the flag
/// Octorate reported: the next pass tries again. Anything that is not an error of Octorate (the database after a call that was made) rises, and the
/// dispatcher delivers the event again, which the call, being idempotent, survives.</item>
/// <item><b>The org is the event's</b> (ADR-022-R6): the handler runs under it, every read names it, and the call is made for it. The payload holds ids
/// and the flag (ADR-022-R21); the ids of the call are those of the thread row of the org, never the payload's. <b>Privacy:</b> the logs carry ids and
/// codes, never the name of the guest, a message or an address.</item>
/// </list>
/// The account-level message agent of Octorate has no operation in its API: it is a setting of the account that the host leaves off (see
/// <c>docs/runbooks/message-assistant.md</c>); this handler does not pretend to control it.
/// </remarks>
public sealed class OctorateAiAssistantGuard(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IChannelMappingService mappings,
    IFeatureFlags featureFlags,
    OctorateRateLimiter rateLimiter,
    TimeProvider timeProvider,
    ILogger<OctorateAiAssistantGuard> logger) : IEventHandler<DomainEvent>
{
    /// <summary>The code in the log of a thread left alone because the calls of its organization are down to the reserve.</summary>
    public const string RateLimitedCode = "octorate_rate_limited";

    /// <summary>The code in the log of an event whose payload is not an <see cref="OtaThreadSyncedPayload"/>.</summary>
    public const string PayloadUnreadableCode = "payload_unreadable";

    private const string Tenant = AppDbContext.TenantQueryFilter;

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (domainEvent.Type != DomainEventType.OtaThreadSynced || domainEvent.EntityId == Guid.Empty)
            return;

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        if (PayloadOf(domainEvent) is not { } payload)
        {
            logger.LogWarning(
                "Event {EventId} ({EventType}) of org {OrgId}: {Code}, the assistant of Octorate is not looked at",
                domainEvent.Id,
                domainEvent.Type,
                domainEvent.OrgId,
                PayloadUnreadableCode);
            return;
        }

        // The pass read the assistant as off: nothing to do and no call, the most frequent event of all.
        if (payload.AiAssistantOn == false)
            return;

        var orgId = domainEvent.OrgId;
        var thread = await db.OtaMessageThreads
            .IgnoreQueryFilters([Tenant])
            .FirstOrDefaultAsync(t => t.OrgId == orgId && t.Id == domainEvent.EntityId, ct);
        if (thread is null)
        {
            // Gone with its retention or its organization after the pass: there is no conversation left to keep an assistant away from.
            logger.LogDebug("Event {EventId} of org {OrgId}: the thread {ThreadId} is not there any more", domainEvent.Id, orgId, domainEvent.EntityId);
            return;
        }

        // The copy is the newest word: a delivery of this event that came before, or a pass that came after, found the assistant off already.
        if (thread.AiAssistantOn == false)
            return;

        // OCT-04: nothing is written to a channel manager for a listing nobody confirmed. The thread gets its property at the first pass after the confirmation.
        if (thread.PropertyId is not { } propertyId || await mappings.IsWriteBlockedAsync(propertyId, ct))
        {
            logger.LogDebug("Thread {ThreadId} of org {OrgId}: its listing is not one CasaZen manages, the assistant of Octorate is left as it is", thread.Id, orgId);
            return;
        }

        // The pass leaves a few calls of the bucket to the people who use CasaZen, and the dispatcher serves every org within seconds: it does not wait.
        if (rateLimiter.AvailableCalls(orgId) <= MessageSyncService.RateReserve)
        {
            logger.LogWarning(
                "Thread {ThreadId} of org {OrgId}: the assistant of Octorate is not switched off, {Code}; the next pass tries again",
                thread.Id,
                orgId,
                RateLimitedCode);
            return;
        }

        try
        {
            using (orgScope.Use(orgId))
                await client.SetThreadAiAssistantAsync(thread.OctorateAccommodationId, thread.OctorateThreadId, enabled: false, ct);
        }
        catch (OctorateException ex)
        {
            // The thread keeps the flag Octorate reported, so nothing in CasaZen says the assistant is off when it is not. The codes are Octorate's and
            // ours (no body, no name); a refusal that no retry changes (not connected, no chat module) is found the same at the next pass.
            logger.LogWarning(
                "Thread {ThreadId} of org {OrgId}: the assistant of Octorate is not switched off, {Code} (HTTP {StatusCode}); the next pass tries again",
                thread.Id,
                orgId,
                ex.Code,
                ex.OctorateStatusCode);
            return;
        }

        // Octorate accepted the call. The next pass reads the flag back from Octorate (and finds a person who switched it on again).
        thread.AiAssistantOn = false;
        thread.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Thread {ThreadId} of org {OrgId}: the assistant of Octorate is switched off", thread.Id, orgId);
    }

    /// <summary>What the pass read, or <c>null</c> when the payload is not that.</summary>
    private static OtaThreadSyncedPayload? PayloadOf(DomainEvent domainEvent)
    {
        try
        {
            return JsonSerializer.Deserialize<OtaThreadSyncedPayload>(domainEvent.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

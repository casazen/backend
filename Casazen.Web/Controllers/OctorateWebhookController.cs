using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// The Octorate webhook (ADR-023-R7, ADR-022-R19, E02/F01/US01/T03). Octorate signs nothing, so the call is authenticated by the secret
/// in its address (<see cref="OctorateWebhookVerifier"/>) and its body is a notification only: the routing keys (event name,
/// accommodation, object id) are read, the org is resolved from the accommodation through the confirmed mappings, one
/// <see cref="DomainEventType.OctorateWebhookReceived"/> event per org is written to the outbox and the call is answered 200 at once.
/// The handlers of the event bus re-read the object through the API, asynchronously; nothing is processed here.
/// </summary>
/// <remarks>
/// <para>A wrong secret is 401 and a security event in the log (never the secret, never the body). An empty or placeholder
/// <c>Octorate:WebhookSecret</c> is 500, never an accepted call. A notification whose accommodation has no confirmed mapping (not linked
/// to a CasaZen property yet, or another partner's) is acknowledged with 200 and recorded nowhere, so Octorate does not retry it for two
/// days; only the outcome is logged, not the code.</para>
/// <para>The secret is part of the path: the request logs must not print the path of <c>/api/webhooks/octorate/*</c>
/// (docs/runbooks/octorate-webhooks.md).</para>
/// </remarks>
[ApiController]
[Route("api/webhooks/octorate/{secret}")]
[AllowAnonymous]
public sealed class OctorateWebhookController(
    OctorateWebhookVerifier verifier,
    AppDbContext db,
    IDomainEventRecorder events,
    TimeProvider timeProvider,
    ILogger<OctorateWebhookController> logger) : ControllerBase
{
    /// <summary>The largest body read: a notification carries ids only.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    private static readonly EventId SecretRefusedEvent = new(52_101, "OctorateWebhookSecretRefused");

    /// <summary>Receives one notification of Octorate.</summary>
    /// <response code="200">Recorded for every org that has the accommodation confirmed, or acknowledged with nothing to record.</response>
    /// <response code="401"><c>invalid_webhook_secret</c>: the secret of the address is not the configured one.</response>
    /// <response code="500"><c>octorate_webhook_not_configured</c>: no usable <c>Octorate:WebhookSecret</c>.</response>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Receive(string secret, CancellationToken cancellationToken)
    {
        // A placeholder committed in a file is public: an address built on it would accept forged calls.
        if (RequiredConfiguration.IsMissing(verifier.ConfiguredSecret) || !verifier.HasUsableSecret)
        {
            logger.LogError("Octorate webhook secret not configured (Octorate__WebhookSecret, 32+ characters): the call is refused");
            return this.ApiProblem(
                StatusCodes.Status500InternalServerError, "octorate_webhook_not_configured", "OctorateWebhookNotConfigured");
        }

        if (!verifier.IsValid(secret))
        {
            logger.LogWarning(
                SecretRefusedEvent,
                "Security: Octorate webhook refused, the secret of the address does not match (remote {RemoteIp})",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return this.ApiProblem(StatusCodes.Status401Unauthorized, "invalid_webhook_secret", "OctorateWebhookSecretInvalid");
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync(cancellationToken);

        var notification = OctorateWebhookNotification.Read(body, Request.ContentType, Request.Query["event"].ToString());
        if (notification.EventName is not { } eventName || notification.AccommodationId is not { } accommodationId)
        {
            logger.LogInformation(
                "Octorate webhook acknowledged without effect: the call names no known event or no accommodation (event {EventKnown}, accommodation {AccommodationKnown})",
                notification.EventName is not null, notification.AccommodationId is not null);
            return Ok();
        }

        // The subscription is the partner's, not a property's: the org is found from the accommodation, across orgs (a system read),
        // and only where a person confirmed the mapping. Several orgs may have mapped the same accommodation: each gets its event.
        var mappings = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OctorateAccommodationId == accommodationId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new { m.Id, m.OrgId })
            .ToListAsync(cancellationToken);
        if (mappings.Count == 0)
        {
            logger.LogInformation("Octorate webhook {EventType} for an accommodation with no confirmed mapping: acknowledged, nothing recorded", eventName);
            return Ok();
        }

        var receivedAt = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var mapping in mappings)
        {
            // No dedup key: a key made of these ids would merge two real changes of one reservation; a redelivery only makes the
            // handler re-read the object, which is idempotent.
            events.Record(
                DomainEventType.OctorateWebhookReceived,
                mapping.Id,
                new OctorateWebhookPayload(eventName, accommodationId, notification.ObjectId, receivedAt),
                dedupKey: null,
                orgId: mapping.OrgId);
        }

        var recorded = db.ChangeTracker.Entries<OutboxEvent>()
            .Where(e => e.State == EntityState.Added && e.Entity.EventType == DomainEventType.OctorateWebhookReceived)
            .Select(e => e.Entity.Id)
            .ToList();
        await db.SaveChangesAsync(cancellationToken);

        foreach (var eventId in recorded)
            logger.LogInformation("Octorate webhook recorded: event {EventId}, type {EventType}, processed {Processed}", eventId, eventName, true);

        return Ok();
    }
}

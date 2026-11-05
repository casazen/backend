using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;

namespace Casazen.Infrastructure.Events;

/// <summary>
/// <see cref="IDomainEventRecorder"/> on the outbox (ADR-022-R1): adds an <see cref="OutboxEvent"/> to the current unit of work,
/// the scoped <see cref="AppDbContext"/>, and never saves it. The caller's <c>SaveChangesAsync</c> commits the domain change and the
/// event together, or neither.
/// </summary>
/// <remarks>
/// The org is the <c>orgId</c> argument when given, otherwise the org of the authenticated request (<see cref="ITenantContext"/>).
/// Outside a request (a Hangfire job, a webhook) there is none, so such a caller passes the org; with neither the event is refused
/// rather than written for no org.
/// </remarks>
public sealed class DomainEventRecorder(
    AppDbContext db,
    ITenantContext tenantContext,
    TimeProvider timeProvider) : IDomainEventRecorder
{
    /// <summary>The JSON of the payloads: camelCase, the convention of the API and of the handlers that read them.</summary>
    internal static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(DomainEventType type, Guid entityId, object payload, string? dedupKey = null, Guid? orgId = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (dedupKey is not null && (string.IsNullOrWhiteSpace(dedupKey) || dedupKey.Length > OutboxEvent.DedupKeyMaxLength))
            throw new ArgumentException($"A dedup key has 1 to {OutboxEvent.DedupKeyMaxLength} characters.", nameof(dedupKey));

        db.OutboxEvents.Add(new OutboxEvent
        {
            OrgId = OrgOf(orgId),
            EventType = type,
            EntityId = entityId,
            OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
            PayloadJson = JsonSerializer.Serialize(payload, payload.GetType(), PayloadJsonOptions),
            DedupKey = dedupKey,
        });
    }

    private Guid OrgOf(Guid? orgId)
    {
        if (orgId is { } given)
        {
            return given != Guid.Empty
                ? given
                : throw new ArgumentException("The org of a domain event cannot be empty.", nameof(orgId));
        }

        if (tenantContext.OrgId is { } requestOrg && requestOrg != Guid.Empty)
            return requestOrg;

        throw new InvalidOperationException(
            "A domain event needs an org: outside an authenticated request (a job, a webhook) pass orgId to Record.");
    }
}

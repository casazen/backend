using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// How a domain change records its event (ADR-022-R1): the event is added to the outbox of the current unit of work and saved by
/// the same <c>SaveChangesAsync</c> as the change, in one transaction, so there is never an event without its change nor a change
/// without its event. Nothing is published here: the outbox dispatcher does it.
/// </summary>
public interface IDomainEventRecorder
{
    /// <summary>
    /// Adds the event to the current <c>DbContext</c> without saving it.
    /// </summary>
    /// <param name="type">What happened.</param>
    /// <param name="entityId">The entity it happened to (booking, service request, payment...).</param>
    /// <param name="payload">Serialized as JSON (camelCase): ids and codes only, never personal data in clear (ADR-022-R21).</param>
    /// <param name="dedupKey">Optional key unique within the org: a second event with the same key is refused by the database.</param>
    /// <param name="orgId">
    /// The org of the event. Optional inside an authenticated request (the caller's org is used); required everywhere else (a
    /// Hangfire job, a webhook), where there is no request org.
    /// </param>
    /// <exception cref="InvalidOperationException">No <paramref name="orgId"/> and no org of the request.</exception>
    void Record(DomainEventType type, Guid entityId, object payload, string? dedupKey = null, Guid? orgId = null);
}

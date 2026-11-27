using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One domain event in the transactional outbox (ADR-022-R1, R2): written by <c>IDomainEventRecorder</c> on the same
/// <c>DbContext</c> and in the same <c>SaveChanges</c> as the domain change it describes, then published on the event bus
/// by the outbox dispatcher (at least once) and kept for the test mode and the audit until the cleanup job removes it.
/// </summary>
/// <remarks>
/// <para>Tenant-owned (ADR-022-R17): the global tenant query filter applies to every read, and the jobs that read every org
/// (the dispatcher, the cleanup) say so with <c>IgnoreQueryFilters([AppDbContext.TenantQueryFilter])</c>. A handler receives
/// <see cref="OrgId"/> with the event and works for that org only.</para>
/// <para><see cref="PayloadJson"/> carries ids and codes, never personal data in clear (ADR-022-R21): a handler reads what it
/// needs from <see cref="EntityId"/>.</para>
/// </remarks>
[Table("OutboxEvents")]
public class OutboxEvent : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="DedupKey"/>) where the key is set (ADR-022-R8).</summary>
    public const string DedupIndexName = "UIX_OutboxEvents_OrgId_DedupKey";

    /// <summary>Longest <see cref="DedupKey"/> the column takes.</summary>
    public const int DedupKeyMaxLength = 200;

    /// <summary>Longest <see cref="LastError"/> the column takes; a longer text is cut.</summary>
    public const int LastErrorMaxLength = 2000;

    /// <summary>The payload of an event that carries nothing, and of a dead letter once anonymized (ADR-022-R21).</summary>
    public const string EmptyPayload = "{}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org the event belongs to; the handlers work for this org only (ADR-022-R6, R17).</summary>
    public Guid OrgId { get; set; }

    public DomainEventType EventType { get; set; }

    /// <summary>The entity the event is about (booking, service request, payment...).</summary>
    public Guid EntityId { get; set; }

    /// <summary>When the event was recorded (UTC); the dispatcher publishes in this order.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>The event's data as JSON (ids and codes, no personal data in clear: ADR-022-R21).</summary>
    [Required]
    public string PayloadJson { get; set; } = EmptyPayload;

    /// <summary>
    /// Optional key that makes the event unique within its org (unique index <see cref="DedupIndexName"/>): a time trigger
    /// emitted twice (restart, overlapping runs) is refused by the database (ADR-022-R8).
    /// </summary>
    [MaxLength(DedupKeyMaxLength)]
    public string? DedupKey { get; set; }

    /// <summary>When every handler returned for this event (UTC); <c>null</c> while it waits for the dispatcher.</summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>Failed deliveries so far (ADR-022-R5).</summary>
    public int Attempts { get; set; }

    /// <summary>What the last failed delivery said (exception type and message), cut to <see cref="LastErrorMaxLength"/>.</summary>
    [MaxLength(LastErrorMaxLength)]
    public string? LastError { get; set; }

    /// <summary>
    /// The event failed every attempt: the dispatcher no longer delivers it and the cleanup job never deletes it (ADR-022-R5,
    /// R15); after the retention it keeps only its audit fields (ADR-022-R21).
    /// </summary>
    public bool DeadLettered { get; set; }
}

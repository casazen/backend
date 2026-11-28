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

    public const int LastErrorMaxLength = 2000;

    public const string EmptyPayload = "{}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public DomainEventType EventType { get; set; }

    public Guid EntityId { get; set; }

    public DateTime OccurredAt { get; set; }

    [Required]
    public string PayloadJson { get; set; } = EmptyPayload;

    /// <summary>
    /// Optional key that makes the event unique within its org (unique index <see cref="DedupIndexName"/>): a time trigger
    /// emitted twice (restart, overlapping runs) is refused by the database (ADR-022-R8).
    /// </summary>
    [MaxLength(DedupKeyMaxLength)]
    public string? DedupKey { get; set; }

    public DateTime? DispatchedAt { get; set; }

    public int Attempts { get; set; }

    [MaxLength(LastErrorMaxLength)]
    public string? LastError { get; set; }

    public bool DeadLettered { get; set; }
}

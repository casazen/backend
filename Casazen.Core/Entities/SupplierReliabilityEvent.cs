using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// One thing that weighs on the reliability of a supplier (SP-REL, the events of <c>SP-10c</c>): it answered on time, did not, completed
/// a job, was disputed. The ranking of the suppliers (<c>SupplierRanking</c>) adds up the events of the last days with weights in
/// configuration; the events themselves are never shown to anybody, and neither is the score.
/// </summary>
/// <remarks>
/// <para><b>Tenancy.</b> Keyed by the supplier org (<see cref="SupplierOrgId"/> = <see cref="SupplierProfile.OrgId"/>), like
/// <see cref="SupplierAvailability"/>: it is <b>not</b> <c>ITenantOwned</c> (a supplier acts as <c>User.SupplierOrgId</c>, and the
/// events are written by jobs and webhooks that belong to no org). It is in the allow-list of <c>TenantQueryFilterArchitectureTests</c>,
/// only <c>SupplierReliabilityService</c> reads and writes it, and every statement carries an explicit supplier predicate
/// (<c>SupplierReliabilityTenancyTests</c>).</para>
/// <para><b>Idempotent.</b> An event about a request is recorded once per supplier and kind (unique index
/// <c>UIX_SupplierReliabilityEvents_Request</c>): a job that runs twice, a retried webhook or a second completion add nothing.</para>
/// </remarks>
[Table("SupplierReliabilityEvents")]
public class SupplierReliabilityEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid SupplierOrgId { get; set; }

    public SupplierReliabilityEventKind Kind { get; set; }

    public Guid? RequestId { get; set; }

    public bool AsBackup { get; set; }

    public DateTime OccurredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>A reference for whoever reads the table (the id of the dispute on Stripe); never shown.</summary>
    [MaxLength(200)]
    public string? Detail { get; set; }

    [ForeignKey(nameof(SupplierOrgId))]
    public SupplierProfile SupplierProfile { get; set; } = null!;
}

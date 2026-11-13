using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The month of an owner in the owner accounting (E06/F05/US02/T02, AC-NF110-3, AC-NF110-8): whether a change came after the last version (dirty), the
/// final close with its reconciliation of the approved closing balance to the paid wires (or the explicit waiver of the difference, with its reason),
/// and the retention of its evidence with the legal hold that stops its expiry.
/// </summary>
[Table("OwnerClosePeriods")]
public class OwnerClosePeriod : ITenantOwned
{
    public const string PeriodIndexName = "UIX_OwnerClosePeriods_OrgId_Owner_Period";
    public const int ReasonMaxLength = 500;
    public const int JurisdictionMaxLength = 8;
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public int PeriodYear { get; set; }

    public int PeriodMonth { get; set; }

    public OwnerClosePeriodStatus Status { get; set; } = OwnerClosePeriodStatus.Open;

    /// <summary>A line of the month was posted after the cut-off of its last version: a new version is needed before approval, publication or close.</summary>
    public bool IsDirty { get; set; }

    public DateTime? DirtySince { get; set; }

    public DateTime? ClosedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ClosedByUserId { get; set; }

    /// <summary>The version the month was closed on.</summary>
    public Guid? ClosedVersionId { get; set; }

    /// <summary>The approved closing balance at the close.</summary>
    [Precision(18, 2)]
    public decimal? DueAmount { get; set; }

    /// <summary>What the paid wires of the month's statements transferred.</summary>
    [Precision(18, 2)]
    public decimal? PaidAmount { get; set; }

    /// <summary>The due minus the paid at the close: zero, or waived.</summary>
    [Precision(18, 2)]
    public decimal? Difference { get; set; }

    /// <summary>Why the difference or the negative balance was accepted (only with <c>agency.owner.financial.manage</c>).</summary>
    [MaxLength(ReasonMaxLength)]
    public string? WaiverReason { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? WaivedByUserId { get; set; }

    public DateTime? WaivedAt { get; set; }

    /// <summary>The jurisdiction whose retention applies.</summary>
    [MaxLength(JurisdictionMaxLength)]
    public string? RetentionJurisdiction { get; set; }

    public int? RetentionYears { get; set; }

    /// <summary>The last day the evidence of the month must be kept (31 December of the close year plus the retention years).</summary>
    public DateOnly? RetainUntil { get; set; }

    /// <summary>A legal hold: the evidence of the month is never deleted while it holds, whatever its retention says.</summary>
    public bool LegalHold { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? LegalHoldReason { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? LegalHoldByUserId { get; set; }

    public DateTime? LegalHoldAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>The PostgreSQL <c>xmin</c> of the row.</summary>
    public uint RowVersion { get; set; }
}

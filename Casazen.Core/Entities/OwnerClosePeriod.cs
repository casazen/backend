using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

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

    public bool IsDirty { get; set; }

    public DateTime? DirtySince { get; set; }

    public DateTime? ClosedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? ClosedByUserId { get; set; }

    public Guid? ClosedVersionId { get; set; }

    [Precision(18, 2)]
    public decimal? DueAmount { get; set; }

    [Precision(18, 2)]
    public decimal? PaidAmount { get; set; }

    [Precision(18, 2)]
    public decimal? Difference { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? WaiverReason { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? WaivedByUserId { get; set; }

    public DateTime? WaivedAt { get; set; }

    [MaxLength(JurisdictionMaxLength)]
    public string? RetentionJurisdiction { get; set; }

    public int? RetentionYears { get; set; }

    public DateOnly? RetainUntil { get; set; }

    public bool LegalHold { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? LegalHoldReason { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? LegalHoldByUserId { get; set; }

    public DateTime? LegalHoldAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public uint RowVersion { get; set; }
}

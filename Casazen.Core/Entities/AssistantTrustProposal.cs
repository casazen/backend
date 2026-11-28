using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A change of trust level that CasaZen proposes for a situation (E05/F05/US01/T01, NF-67, PO-08): after a period of drafts a person approved without editing,
/// «move this situation from draft to automatic». <b>The level changes only when a person accepts</b> (AC-NF67-5): the proposal waits as
/// <see cref="TrustProposalStatus.Proposed"/>. The row keeps the figures the proposal was made on, so the decision can be understood later.
/// </summary>
/// <remarks>No personal data: situations, modes, counts and percentages.</remarks>
[Table("AssistantTrustProposals")]
public class AssistantTrustProposal : ITenantOwned
{
    public const int DecidedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public AssistantSituation Situation { get; set; }

    public AssistantMode FromMode { get; set; }

    public AssistantMode ToMode { get; set; }

    public int DraftsMeasured { get; set; }

    [Precision(5, 2)]
    public decimal ApprovedWithoutEditPercent { get; set; }

    public DateTime PeriodStart { get; set; }

    public TrustProposalStatus Status { get; set; } = TrustProposalStatus.Proposed;

    [MaxLength(DecidedByUserIdMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

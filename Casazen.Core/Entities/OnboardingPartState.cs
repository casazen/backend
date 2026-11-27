using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// What an org chose to do with one part of the Octorate activation plan (NF-104, E10/F01/US01/T01): the host's intent «Do it later», with the
/// "To do" item that stands for it. One row per org and part (unique index <see cref="OrgPartIndexName"/>), created the first time the host
/// defers or leaves the part.
/// </summary>
/// <remarks>
/// <para><b>Derived, not stored.</b> Whether a part is done is worked out on read from the stored facts (the connection, the mapping, the
/// sender...), as the activation checklist of PL-15 does: nothing is "done" by a flag. A part with no row is open. The row only keeps what no
/// fact can tell: that the host deferred the part, when, and when to remind it.</para>
/// <para>The user is the opaque Auth0 subject (<c>string</c>, as <see cref="OrgMember.UserId"/>), not a foreign key. No personal data.</para>
/// </remarks>
[Table("OnboardingPartStates")]
public class OnboardingPartState : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PartKey"/>): a part has one state for each org.</summary>
    public const string OrgPartIndexName = "UIX_OnboardingPartStates_OrgId_PartKey";

    /// <summary>Longest <see cref="UpdatedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2): the org the plan belongs to. Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public OnboardingPartKey PartKey { get; set; }

    public OnboardingPartStatus Status { get; set; } = OnboardingPartStatus.Open;

    /// <summary>When the host deferred the part (UTC); <c>null</c> while it is open.</summary>
    public DateTime? DeferredAt { get; set; }

    /// <summary>When the plan reminds the host of a deferred part (UTC); <c>null</c> = no reminder.</summary>
    public DateTime? RemindAt { get; set; }

    /// <summary>The "To do" item (<c>InternalActivity</c>) a deferral or an exit created; a reference, not a foreign key.</summary>
    public Guid? ActivityId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who changed the row last: the Auth0 subject of the account.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UpdatedByUserId { get; set; } = string.Empty;
}

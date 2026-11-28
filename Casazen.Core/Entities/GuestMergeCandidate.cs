using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Two CRM profiles that are probably the same guest, proposed to a person for a manual merge (NF-88, CRM-01, AC-NF88-3). The matching
/// writes a row when it finds a likely duplicate that it cannot be sure of; a person decides, and only then do the profiles merge.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The model never merges.</b> A row is a proposal and a record of the decision (<see cref="Status"/>, <see cref="DecidedAt"/>,
/// <see cref="DecidedByUserId"/>); the merge itself is a service, run by a person's action. A <see cref="GuestMergeStatus.Dismissed"/> pair
/// stays in the table so that the matching does not propose it again.</item>
/// <item><b>One row for each pair.</b> The pair is stored with <see cref="ProfileAId"/> smaller than <see cref="ProfileBId"/> (by
/// <see cref="Guid.CompareTo(Guid)"/>), so that (A, B) and (B, A) are the same row; the unique index on the org and the two profiles
/// keeps it so. The database enforces the order with a check (<c>CK_GuestMergeCandidates_ProfileOrder</c>, <c>"ProfileAId" &lt; "ProfileBId"</c>,
/// which also refuses a profile paired with itself) and refuses a pair stored the other way round. PostgreSQL compares a <c>uuid</c> byte by byte
/// in its canonical order, which is the order of <see cref="Guid.CompareTo(Guid)"/>: <b>the code that inserts a pair must sort it</b>, smaller id
/// first, and the check accepts it. The database also checks that the score is between 0 and 100.</item>
/// <item><b>No personal data.</b> A row holds ids, a score and reason codes, never a name, a phone number or a birth date: the reasons say
/// which kind of evidence matched (<c>same_phone</c>, <c>same_name_birth</c>), not what it was.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the two profiles, which are always of the same org.</item>
/// </list>
/// </remarks>
[Table("GuestMergeCandidates")]
public class GuestMergeCandidate : ITenantOwned
{
    public const int MinScore = 0;

    public const int MaxScore = 100;

    public const int DecidedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid ProfileAId { get; set; }

    public Guid ProfileBId { get; set; }

    public int Score { get; set; }

    [Required, Column(TypeName = "jsonb")]
    public string ReasonsJson { get; set; } = "[]";

    public GuestMergeStatus Status { get; set; } = GuestMergeStatus.Proposed;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DecidedAt { get; set; }

    [MaxLength(DecidedByUserIdMaxLength)]
    public string? DecidedByUserId { get; set; }
}

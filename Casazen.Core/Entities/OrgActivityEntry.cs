using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One line of the activity log of an org (AM-02b): who did what, when, to which thing. The log answers «chi ha fatto cosa»
/// to the people who administer the org, and it is deliberately poor: <b>ids and codes only, never a name, an email, an
/// amount or a free text</b>. The client composes the sentence from <see cref="Type"/> and the ids, resolving them with the
/// data it can already see.
/// </summary>
/// <remarks>
/// <para><b>Written inside the unit of work of the change it records</b> (<c>IActivityLog.Record</c> stages the row in the
/// change tracker; the service's own <c>SaveChanges</c> writes it, in its transaction): if the change is rolled back, so is
/// the line, and a change can never be missed by the log. Append-only: nothing updates a line; the only deletion is the
/// retention (<c>OrgTeam:ActivityRetentionMonths</c>, 12 months by default, <c>IOrgActivityRetentionService</c>).</para>
/// <para><b>No personal data.</b> <see cref="ActorUserId"/> and <see cref="SubjectId"/> are opaque ids. There is no foreign key
/// to the account on purpose (the history outlives it, like <c>OrgInvitation.InvitedByUserId</c>): erasing an account leaves
/// an id nobody can resolve, no name, no email. <see cref="DetailsJson"/> holds a handful of codes (a role, a plan tier, a
/// count) chosen from a closed list per type (<c>OrgActivityCatalog</c>), and the catalog refuses anything else.</para>
/// </remarks>
[Table("OrgActivityEntries")]
public class OrgActivityEntry : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public DateTime When { get; set; }

    [MaxLength(255)]
    public string? ActorUserId { get; set; }

    public OrgActivityArea Area { get; set; }

    public OrgActivityType Type { get; set; }

    public OrgActivitySubjectType SubjectType { get; set; }

    [Required, MaxLength(255)]
    public string SubjectId { get; set; } = string.Empty;

    [Required, Column(TypeName = "jsonb")]
    public string DetailsJson { get; set; } = "{}";
}

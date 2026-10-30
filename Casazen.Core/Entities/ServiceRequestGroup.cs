using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

/// <summary>
/// The requests a host sent for one job to a supplier (the primary) and, at the same moment, to up to three backups declared as
/// such (SP-BKP, decision of the product owner of 2026-10-29/30): the primary has one hour to take the job, and the first backup
/// that accepts has it if the primary does not. One row per group; the requests are rows of <c>ServiceRequests</c> that carry
/// <see cref="ServiceRequest.GroupId"/> and a <see cref="ServiceRequest.GroupRole"/>.
/// </summary>
/// <remarks>
/// <para><b>Two parties and more, no tenant filter (TN-2 allow-list).</b> The group belongs to the host org (<see cref="OrgId"/>) but
/// its members are read and changed by other orgs (the backups' suppliers) and by a system job that works across every org, so
/// the global filter of the caller's org would hide it from them. Every statement scopes explicitly (by the group id, by the host
/// org or by the member request), and <c>ServiceRequestGroupTenancyTests</c> lists the only code allowed to use the table.</para>
/// <para><b>Concurrency.</b> Every change of a group runs under the advisory lock
/// <c>PostgresAdvisoryLocks.Scope.ServiceRequestGroup</c> (key: the group id) and is saved only if the row is as it was read
/// (<see cref="Version"/>, <c>xmin</c>): two takes, or a take and the job, never both close the group.</para>
/// </remarks>
[Table("ServiceRequestGroups")]
public class ServiceRequestGroup
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The host org the requests belong to.</summary>
    public Guid OrgId { get; set; }

    public ServiceRequestGroupPhase Phase { get; set; } = ServiceRequestGroupPhase.PrimaryWindow;

    /// <summary>
    /// The end of the primary's window: the instant after which the work goes to the backup that accepted first. Moved forward when
    /// the host turns down a time the primary proposed and the window had already passed (the same rule as a single request).
    /// </summary>
    public DateTime PrimaryDueAt { get; set; }

    /// <summary>
    /// The end of the second window, when every supplier still waiting can take the job: <c>null</c> until the group enters
    /// <see cref="ServiceRequestGroupPhase.BackupWindow"/>. After it the requests that nobody took lapse.
    /// </summary>
    public DateTime? BackupDueAt { get; set; }

    /// <summary>
    /// True when the time of the job was one of the published slots of the primary (the host picked it from the agenda): then it must
    /// be one of each backup's too, when the job is assigned to it. False when the host typed the length of the work, which skips that
    /// check for the primary (and so for the backups: only the jobs a backup already took can get in its way).
    /// </summary>
    public bool SlotPlanned { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the group was closed; <c>null</c> while it is open.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token: PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no column
    /// of its own), as for <see cref="ServiceRequest.Version"/>.
    /// </summary>
    public uint Version { get; set; }

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;
}

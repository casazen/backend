using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The rules of the backup suppliers (SP-BKP, decision of the product owner of 2026-10-29/30): a host's request is sent to the
/// supplier it chose (the <b>primary</b>) and, at the same moment, to up to three <b>backups</b>, declared as such and picked by the
/// ranking of the suppliers (SP-REL). The primary has one hour (<c>Suppliers:ServiceRequests:PrimaryResponseMinutes</c>) to take the job;
/// the first backup that accepts has it if the primary does not, and <b>its acceptance closes the race</b> for the other backups. One
/// place for the service, the readers, the job and the tests.
/// </summary>
/// <remarks>
/// <para><b>The decisions</b> (the product owner's of 2026-10-30, and the defaults he left open):
/// (1) a backup can only say it would take the job (the <i>acceptance</i>, <see cref="ServiceRequest.BackupAcceptedAt"/>) while the
/// primary has its window; (2) <b>the first backup that accepts closes the race</b>: at that instant the requests of the other backups are
/// closed for good (<c>BackupRaceClosed</c>, no penalty) and they cannot accept any more; when the window passes, or the primary refuses,
/// the backup that accepted takes the job by itself; (3) the acceptance holds the backup's slot in its agenda from then on (checked when it
/// accepts, under the supplier's calendar lock, and again when the job is assigned) and frees it if the primary takes the job, which
/// releases the backup with no penalty; the backups that did not accept never hold a slot; (4) when nobody accepted by the end of the
/// primary's window, the group opens a second window (<c>BackupResponseMinutes</c>, 60) in which the primary is still in the race but
/// without any priority, and the first to take the job has it, which closes the others; (5) the primary that takes the job after its hour
/// is refused only when a backup has accepted; (6) a backup sees what a supplier sees before the take (the comune, the time, the price):
/// never the property or the host's notes.</para>
/// </remarks>
public static class ServiceRequestGroupRules
{
    public static readonly Expression<Func<ServiceRequest, bool>> VisibleToHost = r =>
        r.GroupRole == ServiceRequestGroupRole.None
        || r.GroupRole == ServiceRequestGroupRole.Primary
        || r.GroupRole == ServiceRequestGroupRole.PromotedBackup;

    public static readonly Expression<Func<ServiceRequest, bool>> NotWaitingBackup = r =>
        r.GroupRole != ServiceRequestGroupRole.Backup || r.Status != ServiceRequestStatus.Richiesto;

    /// <summary>
    /// The requests that hold, or may hold, a time in the supplier's agenda: all but a backup that still waits. A backup holds its slot from
    /// the moment it accepts (it is the only one that can have the job when the primary does not take it), so one that accepted is in; the
    /// ones that did not accept, and the ones closed by the race, are not.
    /// </summary>
    public static readonly Expression<Func<ServiceRequest, bool>> HoldsSupplierTime = r =>
        r.GroupRole != ServiceRequestGroupRole.Backup || r.Status != ServiceRequestStatus.Richiesto || r.BackupAcceptedAt != null;

    public static IQueryable<ServiceRequest> OnlyVisibleToHost(this IQueryable<ServiceRequest> requests) => requests.Where(VisibleToHost);

    public static IQueryable<ServiceRequest> WithoutWaitingBackups(this IQueryable<ServiceRequest> requests) => requests.Where(NotWaitingBackup);

    public static IQueryable<ServiceRequest> OnlyHoldingSupplierTime(this IQueryable<ServiceRequest> requests) => requests.Where(HoldsSupplierTime);

    /// <summary>True for a backup that has not taken the job and is not out of the group: the host does not see it, and it holds no slot unless it accepted.</summary>
    public static bool IsWaitingBackup(ServiceRequestGroupRole role, ServiceRequestStatus status) =>
        role == ServiceRequestGroupRole.Backup && status == ServiceRequestStatus.Richiesto;

    public static bool IsVisibleToHost(ServiceRequestGroupRole role) =>
        role is ServiceRequestGroupRole.None or ServiceRequestGroupRole.Primary or ServiceRequestGroupRole.PromotedBackup;

    public sealed record Member(
        Guid RequestId,
        ServiceRequestGroupRole Role,
        ServiceRequestStatus Status,
        DateTime? BackupAcceptedAt,
        bool HasPendingProposal,
        ServiceRequestActorParty? CancelledBy)
    {
        public bool IsLive => Status == ServiceRequestStatus.Richiesto;
    }

    /// <summary>
    /// The backups that accepted and still wait, the earliest first (the instant, then the id: a total order, so two runs agree). Since the
    /// first acceptance closes the race there is at most one; the order stays as the last guard of a race the database could not stop.
    /// </summary>
    public static IReadOnlyList<Member> PreApprovedInOrder(IEnumerable<Member> members) =>
        [.. members
            .Where(m => m.Role == ServiceRequestGroupRole.Backup && m.IsLive && m.BackupAcceptedAt is not null)
            .OrderBy(m => m.BackupAcceptedAt)
            .ThenBy(m => m.RequestId)];

    public static bool PrimaryIsOut(Member primary, ServiceRequestGroupPhase phase, DateTime primaryDueAt, DateTime now)
    {
        if (primary.Status == ServiceRequestStatus.Rifiutato)
            return true;

        if (primary.Status == ServiceRequestStatus.Annullato)
            return primary.CancelledBy != ServiceRequestActorParty.Host;

        if (primary.Status != ServiceRequestStatus.Richiesto)
            return false;

        return phase != ServiceRequestGroupPhase.Closed
               && !primary.HasPendingProposal
               && now >= primaryDueAt;
    }

    public enum TakeVerdict
    {
        Allowed,

        GroupClosed,

        BackupMustWaitForPrimary,

        PrimaryWindowPassed,

        NotYourTurn,
    }

    /// <summary>
    /// Whether <paramref name="requester"/> may take the job now. During the primary's window only the primary can; after it, the
    /// earliest pre-approval has the job (the primary is refused when there is one), and with no pre-approval the first to take has it,
    /// primary included. While the primary has a time proposed that the host has not answered, the backups wait. The caller applies it
    /// under the group's lock, on the rows read after taking it.
    /// </summary>
    public static TakeVerdict CanTake(
        ServiceRequestGroupPhase phase,
        DateTime primaryDueAt,
        DateTime now,
        Member requester,
        Member primary,
        IReadOnlyCollection<Member> backups)
    {
        if (phase == ServiceRequestGroupPhase.Closed)
            return TakeVerdict.GroupClosed;

        var preApproved = PreApprovedInOrder(backups);
        var primaryWindowPassed = phase == ServiceRequestGroupPhase.PrimaryWindow && now >= primaryDueAt;

        if (requester.Role is ServiceRequestGroupRole.Primary)
            return primaryWindowPassed && preApproved.Count > 0 ? TakeVerdict.PrimaryWindowPassed : TakeVerdict.Allowed;

        if (requester.Role != ServiceRequestGroupRole.Backup)
            return TakeVerdict.GroupClosed;

        if (primary.Status is ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso
                or ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato
            || (primary.Status == ServiceRequestStatus.Annullato && primary.CancelledBy == ServiceRequestActorParty.Host))
        {
            return TakeVerdict.GroupClosed;
        }

        if (phase == ServiceRequestGroupPhase.BackupWindow)
            return TakeVerdict.Allowed;

        if (primary.IsLive && (!primaryWindowPassed || primary.HasPendingProposal))
            return TakeVerdict.BackupMustWaitForPrimary;

        if (preApproved.Count > 0)
            return preApproved[0].RequestId == requester.RequestId ? TakeVerdict.Allowed : TakeVerdict.NotYourTurn;

        return TakeVerdict.Allowed;
    }
}

public static class ServiceRequestGroupErrorCodes
{
    public const string BackupWaitsForPrimary = "service_request_backup_waits_for_primary";

    public const string PrimaryWindowPassed = "service_request_primary_window_passed";

    public const string BackupNotYourTurn = "service_request_backup_not_your_turn";

    public const string NotAWaitingBackup = "service_request_not_a_waiting_backup";

    public const string BackupCannotPropose = "service_request_backup_cannot_propose";

    public const string BackupCannotQuote = "service_request_backup_cannot_quote";

    /// <summary>422: the second window of the group is open, so the backup takes the job directly instead of accepting as a backup.</summary>
    public const string BackupAlreadyActive = "service_request_backup_already_active";

    /// <summary>
    /// 409: another backup accepted first, which closes the race for the others (the first backup that accepts is the only one that can have
    /// the job): this one can no longer accept, and loses nothing by it.
    /// </summary>
    public const string BackupRaceClosed = "service_request_backup_race_closed";

    public const string BackupWaitsForPrimaryMessageKey = "ServiceRequestBackupWaitsForPrimary";
    public const string PrimaryWindowPassedMessageKey = "ServiceRequestPrimaryWindowPassed";
    public const string BackupNotYourTurnMessageKey = "ServiceRequestBackupNotYourTurn";
    public const string NotAWaitingBackupMessageKey = "ServiceRequestNotAWaitingBackup";
    public const string BackupCannotProposeMessageKey = "ServiceRequestBackupCannotPropose";
    public const string BackupCannotQuoteMessageKey = "ServiceRequestBackupCannotQuote";
    public const string BackupAlreadyActiveMessageKey = "ServiceRequestBackupAlreadyActive";
    public const string BackupRaceClosedMessageKey = "ServiceRequestBackupRaceClosed";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        BackupWaitsForPrimaryMessageKey,
        PrimaryWindowPassedMessageKey,
        BackupNotYourTurnMessageKey,
        NotAWaitingBackupMessageKey,
        BackupCannotProposeMessageKey,
        BackupCannotQuoteMessageKey,
        BackupAlreadyActiveMessageKey,
        BackupRaceClosedMessageKey,
    ];
}

public sealed record ServiceRequestGroupView(
    Guid Id,
    ServiceRequestGroupPhase Phase,
    DateTime PrimaryDueAt,
    DateTime? BackupDueAt,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    IReadOnlyList<ServiceRequestGroupMemberView> Members);

public sealed record ServiceRequestGroupMemberView(
    Guid RequestId,
    ServiceRequestGroupRole Role,
    Guid SupplierOrgId,
    string SupplierName,
    ServiceRequestStatus Status,
    DateTime? AcceptedAt,
    bool TookTheJob,
    string? CancellationReason);

/// <summary>
/// The backup side of a request as its supplier sees it (<see cref="SupplierServiceRequestView.Backup"/>): <c>null</c> outside a group.
/// A supplier never learns who the other suppliers of the group are.
/// </summary>
/// <param name="Role"><see cref="SupplierBackupRoles.Primary"/> (the host chose this supplier) or <see cref="SupplierBackupRoles.Backup"/>.</param>
/// <param name="State">One of <see cref="SupplierBackupStates"/> (an open set: show a generic wording for a value you do not know).</param>
/// <param name="PrimaryDueAt">The end of the primary's window: the instant after which the work goes to the backup that accepted first.</param>
/// <param name="BackupDueAt">The end of the second window; null until the group enters it.</param>
/// <param name="AcceptedAt">When this backup accepted; null if it did not.</param>
public sealed record SupplierJobBackup(
    string Role,
    string State,
    DateTime PrimaryDueAt,
    DateTime? BackupDueAt,
    DateTime? AcceptedAt);

public static class SupplierBackupRoles
{
    public const string Primary = "primary";
    public const string Backup = "backup";
}

public static class SupplierBackupStates
{
    public const string Waiting = "waiting";

    public const string Accepted = "accepted";

    public const string Active = "active";

    public const string Won = "won";

    public const string Priority = "priority";

    /// <summary>The primary, after its window and while the second one is open: still in the race, without the priority.</summary>
    public const string Late = "late";

    public const string Closed = "closed";

    public static string Of(
        ServiceRequestGroupRole role,
        ServiceRequestStatus status,
        ServiceRequestGroupPhase phase,
        DateTime primaryDueAt,
        DateTime now,
        DateTime? acceptedAt)
    {
        if (role == ServiceRequestGroupRole.PromotedBackup)
            return Won;

        if (status != ServiceRequestStatus.Richiesto || phase == ServiceRequestGroupPhase.Closed)
            return Closed;

        return role switch
        {
            ServiceRequestGroupRole.Primary => phase == ServiceRequestGroupPhase.BackupWindow || now >= primaryDueAt ? Late : Priority,
            ServiceRequestGroupRole.Backup when acceptedAt is not null => Accepted,
            ServiceRequestGroupRole.Backup => phase == ServiceRequestGroupPhase.BackupWindow ? Active : Waiting,
            _ => Closed,
        };
    }
}

public enum ServiceRequestGroupResolution
{
    NothingToDo = 0,

    ClosedPrimaryTook = 1,

    ClosedBackupTook = 2,

    BackupWindowOpened = 3,

    ClosedLapsed = 4,

    ClosedCancelled = 5,

    Conflict = 6,
}

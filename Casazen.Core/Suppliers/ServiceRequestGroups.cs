using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The rules of the backup suppliers (SP-BKP, decision of the product owner of 2026-10-29/30): a host's request is sent to the
/// supplier it chose (the <b>primary</b>) and, at the same moment, to up to three <b>backups</b>, declared as such. The primary has one
/// hour (<c>Suppliers:ServiceRequests:PrimaryResponseMinutes</c>) to take the job; the first backup that accepts has it if the primary
/// does not. One place for the service, the readers, the job and the tests.
/// </summary>
/// <remarks>
/// <para><b>The decisions with a default</b> (the product owner can change them):
/// (1) a backup can only say it would take the job (the <i>pre-approval</i>, <see cref="ServiceRequest.BackupAcceptedAt"/>) while the
/// primary has its window; when the window passes, the earliest pre-approval has the job; (2) when nobody pre-approved, the group opens a
/// second window (<c>BackupResponseMinutes</c>, 60) in which the primary is still in the race but without any priority, and the first to
/// take the job has it; (3) the primary that takes the job after its hour is refused only when a backup holds a pre-approval; (4) a
/// backup never holds a slot of its agenda before it has the job: the slot is checked again when the job is assigned and, if it is not
/// free, the next pre-approval is tried; (5) a backup sees what a supplier sees before the take (the comune, the time, the price): never
/// the property or the host's notes.</para>
/// </remarks>
public static class ServiceRequestGroupRules
{
    /// <summary>The requests the host sees: the ones outside a group, the primary and the backup that took the job. Never a backup that waits, nor the primary it replaced.</summary>
    public static readonly Expression<Func<ServiceRequest, bool>> VisibleToHost = r =>
        r.GroupRole == ServiceRequestGroupRole.None
        || r.GroupRole == ServiceRequestGroupRole.Primary
        || r.GroupRole == ServiceRequestGroupRole.PromotedBackup;

    /// <summary>The requests that are not a backup waiting for the primary: the ones that hold, or may hold, a time in the supplier's agenda.</summary>
    public static readonly Expression<Func<ServiceRequest, bool>> NotWaitingBackup = r =>
        r.GroupRole != ServiceRequestGroupRole.Backup || r.Status != ServiceRequestStatus.Richiesto;

    /// <summary>The requests the host sees (see <see cref="VisibleToHost"/>).</summary>
    public static IQueryable<ServiceRequest> OnlyVisibleToHost(this IQueryable<ServiceRequest> requests) => requests.Where(VisibleToHost);

    /// <summary>The requests that are not a backup waiting for the primary (see <see cref="NotWaitingBackup"/>).</summary>
    public static IQueryable<ServiceRequest> WithoutWaitingBackups(this IQueryable<ServiceRequest> requests) => requests.Where(NotWaitingBackup);

    /// <summary>True for a backup that has not taken the job and is not out of the group: it holds nothing and the host does not see it.</summary>
    public static bool IsWaitingBackup(ServiceRequestGroupRole role, ServiceRequestStatus status) =>
        role == ServiceRequestGroupRole.Backup && status == ServiceRequestStatus.Richiesto;

    /// <summary>True for the roles the host's lists show (the compiled form of <see cref="VisibleToHost"/>).</summary>
    public static bool IsVisibleToHost(ServiceRequestGroupRole role) =>
        role is ServiceRequestGroupRole.None or ServiceRequestGroupRole.Primary or ServiceRequestGroupRole.PromotedBackup;

    /// <summary>
    /// What a member of a group is, as the rules need to know it: the role, the status, the pre-approval and whether the request has
    /// a time proposed that the host has not answered yet.
    /// </summary>
    public sealed record Member(
        Guid RequestId,
        ServiceRequestGroupRole Role,
        ServiceRequestStatus Status,
        DateTime? BackupAcceptedAt,
        bool HasPendingProposal,
        ServiceRequestActorParty? CancelledBy)
    {
        /// <summary>The request still waits for its supplier's answer.</summary>
        public bool IsLive => Status == ServiceRequestStatus.Richiesto;
    }

    /// <summary>The backups that pre-approved and still wait, the earliest first (the instant, then the id: a total order, so two runs agree).</summary>
    public static IReadOnlyList<Member> PreApprovedInOrder(IEnumerable<Member> members) =>
        [.. members
            .Where(m => m.Role == ServiceRequestGroupRole.Backup && m.IsLive && m.BackupAcceptedAt is not null)
            .OrderBy(m => m.BackupAcceptedAt)
            .ThenBy(m => m.RequestId)];

    /// <summary>
    /// True when the primary has lost its turn: it refused, or cancelled the request itself (the host's cancellation closes the group
    /// instead), or its window passed with no time proposed that the host has to answer. A primary with a proposal waiting is still the
    /// host's to decide: the group does not move.
    /// </summary>
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

    /// <summary>The verdict of <see cref="CanTake"/>.</summary>
    public enum TakeVerdict
    {
        /// <summary>The request may be taken now.</summary>
        Allowed,

        /// <summary>The group is over (someone took the job, or it lapsed): nothing to take.</summary>
        GroupClosed,

        /// <summary>A backup, while the primary has its window: it can only pre-approve.</summary>
        BackupMustWaitForPrimary,

        /// <summary>The primary, after its window, while a backup holds a pre-approval: the job is that backup's.</summary>
        PrimaryWindowPassed,

        /// <summary>A backup, when another backup holds the earlier pre-approval: it is that one's turn.</summary>
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

        // Only a backup that waits can take the job from here on.
        if (requester.Role != ServiceRequestGroupRole.Backup)
            return TakeVerdict.GroupClosed;

        // The primary took the job, or the host cancelled: there is nothing left to take, whatever the group's row says yet.
        if (primary.Status is ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso
                or ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato
            || (primary.Status == ServiceRequestStatus.Annullato && primary.CancelledBy == ServiceRequestActorParty.Host))
        {
            return TakeVerdict.GroupClosed;
        }

        if (phase == ServiceRequestGroupPhase.BackupWindow)
            return TakeVerdict.Allowed;

        // The primary window: the backups wait while the primary has its hour, or has a proposal the host has to answer.
        if (primary.IsLive && (!primaryWindowPassed || primary.HasPendingProposal))
            return TakeVerdict.BackupMustWaitForPrimary;

        // The primary is out: the earliest pre-approval first.
        if (preApproved.Count > 0)
            return preApproved[0].RequestId == requester.RequestId ? TakeVerdict.Allowed : TakeVerdict.NotYourTurn;

        return TakeVerdict.Allowed;
    }
}

/// <summary>Stable codes (ProblemDetails <c>code</c>) and message keys of the refusals of the backup rules.</summary>
public static class ServiceRequestGroupErrorCodes
{
    /// <summary>422: the supplier is a backup and the primary still has its window: it can accept as a backup, not take the job.</summary>
    public const string BackupWaitsForPrimary = "service_request_backup_waits_for_primary";

    /// <summary>422: the primary's window passed and a backup holds a pre-approval: the job is that backup's.</summary>
    public const string PrimaryWindowPassed = "service_request_primary_window_passed";

    /// <summary>422: another backup accepted before this one: it is that one's turn.</summary>
    public const string BackupNotYourTurn = "service_request_backup_not_your_turn";

    /// <summary>422: the action is for a backup that waits (accept as a backup) and the request is not one.</summary>
    public const string NotAWaitingBackup = "service_request_not_a_waiting_backup";

    /// <summary>422: a backup cannot propose another time (the host would answer a supplier that is not the one it chose).</summary>
    public const string BackupCannotPropose = "service_request_backup_cannot_propose";

    /// <summary>422: a backup cannot send a quote (SP-04b): the host would answer a supplier that is not the one it chose.</summary>
    public const string BackupCannotQuote = "service_request_backup_cannot_quote";

    /// <summary>422: the second window of the group is open, so the backup takes the job directly instead of accepting as a backup.</summary>
    public const string BackupAlreadyActive = "service_request_backup_already_active";

    public const string BackupWaitsForPrimaryMessageKey = "ServiceRequestBackupWaitsForPrimary";
    public const string PrimaryWindowPassedMessageKey = "ServiceRequestPrimaryWindowPassed";
    public const string BackupNotYourTurnMessageKey = "ServiceRequestBackupNotYourTurn";
    public const string NotAWaitingBackupMessageKey = "ServiceRequestNotAWaitingBackup";
    public const string BackupCannotProposeMessageKey = "ServiceRequestBackupCannotPropose";
    public const string BackupCannotQuoteMessageKey = "ServiceRequestBackupCannotQuote";
    public const string BackupAlreadyActiveMessageKey = "ServiceRequestBackupAlreadyActive";

    /// <summary>Every <c>SharedResources</c> key the backup rules raise (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        BackupWaitsForPrimaryMessageKey,
        PrimaryWindowPassedMessageKey,
        BackupNotYourTurnMessageKey,
        NotAWaitingBackupMessageKey,
        BackupCannotProposeMessageKey,
        BackupCannotQuoteMessageKey,
        BackupAlreadyActiveMessageKey,
    ];
}

/// <summary>
/// A group of requests as the host who sent it sees it: where it is, the primary and the backups with their state, and who took the
/// job. Names are the suppliers' business names (the host picked the primary and CasaZen declared the backups to it); nothing else of a
/// supplier is given.
/// </summary>
public sealed record ServiceRequestGroupView(
    Guid Id,
    ServiceRequestGroupPhase Phase,
    DateTime PrimaryDueAt,
    DateTime? BackupDueAt,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    IReadOnlyList<ServiceRequestGroupMemberView> Members);

/// <summary>One request of a group: whose it is, what it is in the group, its status, and whether it took the job.</summary>
/// <param name="AcceptedAt">When a backup said it would take the job (the pre-approval); null for the others.</param>
/// <param name="TookTheJob">True for the request that was taken: the primary, or the backup that replaced it.</param>
/// <param name="CancellationReason">The code of <see cref="ServiceRequestCancellationReasons"/> of a request cancelled by CasaZen or by the host; null for any other.</param>
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

/// <summary>The values of <see cref="SupplierJobBackup.Role"/>.</summary>
public static class SupplierBackupRoles
{
    public const string Primary = "primary";
    public const string Backup = "backup";
}

/// <summary>
/// The values of <see cref="SupplierJobBackup.State"/>, derived once here from the role, the status and the phase of the group (never
/// stored). An open set.
/// </summary>
public static class SupplierBackupStates
{
    /// <summary>A backup, the primary has its window: it can accept as a backup.</summary>
    public const string Waiting = "waiting";

    /// <summary>A backup that accepted: the job is its if the primary does not take it.</summary>
    public const string Accepted = "accepted";

    /// <summary>A backup, the second window is open: it can take the job now, and the first to do so has it.</summary>
    public const string Active = "active";

    /// <summary>The backup that took the job.</summary>
    public const string Won = "won";

    /// <summary>The primary, within its window: it has the priority.</summary>
    public const string Priority = "priority";

    /// <summary>The primary, after its window and while the second one is open: still in the race, without the priority.</summary>
    public const string Late = "late";

    /// <summary>A request of the group that is over for this supplier (taken by another, lapsed or cancelled).</summary>
    public const string Closed = "closed";

    /// <summary>The state of a request of a group for its supplier.</summary>
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

        // A request that is no longer waiting for its supplier's answer (taken, refused, cancelled) or whose group is over is closed.
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

/// <summary>What one pass over a group did (<c>IServiceRequestService.ResolveGroupAsync</c>), for the job's counters and the tests.</summary>
public enum ServiceRequestGroupResolution
{
    /// <summary>Nothing was due: the group is as it was (or does not exist, or is closed already).</summary>
    NothingToDo = 0,

    /// <summary>The primary took the job (or was found to have it): the backups were closed.</summary>
    ClosedPrimaryTook = 1,

    /// <summary>A backup that had accepted took the job: the primary and the other backups were closed.</summary>
    ClosedBackupTook = 2,

    /// <summary>The second window opened: nobody had accepted (or no accepted backup could take the job), the backups can take it now.</summary>
    BackupWindowOpened = 3,

    /// <summary>Nobody took the job: the requests lapsed (<c>NoResponse</c>).</summary>
    ClosedLapsed = 4,

    /// <summary>The host cancelled the request: the backups were closed.</summary>
    ClosedCancelled = 5,

    /// <summary>The group changed under the pass (another call got there first): left as it is, the next run looks again.</summary>
    Conflict = 6,
}

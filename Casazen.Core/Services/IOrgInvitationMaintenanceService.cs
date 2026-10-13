namespace Casazen.Core.Services;

/// <summary>What one run of the invitation maintenance did.</summary>
/// <param name="Skipped">Another run was in progress: this one did nothing.</param>
/// <param name="Reminded">Reminders sent (third day).</param>
/// <param name="Expired">Invitations marked expired (and their inviters told).</param>
/// <param name="Purged">Closed invitations deleted, with the name and email of the invitee, after the retention.</param>
public sealed record OrgInvitationMaintenanceResult(bool Skipped, int Reminded, int Expired, int Purged)
{
    public static OrgInvitationMaintenanceResult SkippedRun { get; } = new(true, 0, 0, 0);
}

/// <summary>
/// The recurring work on the org invitations (AM-02), run hourly by <c>OrgInvitationMaintenanceJob</c>: the reminder of the
/// third day (with a fresh link), the expiry of the invitations nobody accepted (and a note to the inviter), and the
/// deletion of the closed ones once the retention has passed. Every step is idempotent and takes the org's seats lock for
/// the invitation it touches, so it never races with a person who accepts, revokes or sends it again; a session advisory
/// lock keeps two runs from overlapping.
/// </summary>
public interface IOrgInvitationMaintenanceService
{
    Task<OrgInvitationMaintenanceResult> RunAsync(CancellationToken cancellationToken = default);
}

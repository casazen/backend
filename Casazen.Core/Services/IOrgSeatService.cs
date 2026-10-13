namespace Casazen.Core.Services;

/// <summary>
/// The seats of an org (AM-02, decisions D13 and D35): how many people the plan allows and how many the org has.
/// </summary>
/// <param name="Max">
/// What the <b>effective</b> plan allows (<c>IEntitlementService.ResolveMaxSeats</c>); <see cref="int.MaxValue"/> =
/// unlimited. With a subscription not in good standing the effective plan is Starter, so this drops to 2 while the members
/// stay: <see cref="Used"/> can then be above <see cref="Max"/>.
/// </param>
/// <param name="ActiveMembers">Members that are not deactivated (the accountant counts like everyone else).</param>
/// <param name="PendingInvitations">Invitations still pending whose expiry has not passed: each holds a seat.</param>
public sealed record OrgSeatUsage(int Max, int ActiveMembers, int PendingInvitations)
{
    /// <summary>Seats in use: active members plus open invitations.</summary>
    public int Used => ActiveMembers + PendingInvitations;

    public bool IsUnlimited => Max == int.MaxValue;

    /// <summary>Free seats (never negative); <see cref="int.MaxValue"/> when unlimited.</summary>
    public int Available => IsUnlimited ? int.MaxValue : Math.Max(0, Max - Used);

    /// <summary>True when one more person can be invited.</summary>
    public bool CanInvite => IsUnlimited || Used < Max;
}

/// <summary>Stable codes of the refusals about seats (409), translated by the clients.</summary>
public static class OrgSeatErrors
{
    /// <summary>409: the plan has no free seat for another person (a new invitation, or the reactivation of a member).</summary>
    public const string LimitReached = "org_seat_limit_reached";
}

/// <summary>
/// Counts the seats of an org and decides whether one more person fits. Reading is free; the decision that must hold
/// under concurrency (<see cref="EnsureSeatAvailableAsync"/>) is taken by the caller inside the seats lock
/// (<c>PostgresAdvisoryLocks.Scope.OrgSeats</c>), together with the write that takes the seat, exactly like
/// <c>IEntitlementService.CreatePropertyWithinLimitAsync</c> does for properties.
/// </summary>
public interface IOrgSeatService
{
    /// <summary>The seats of <paramref name="orgId"/> right now.</summary>
    Task<OrgSeatUsage> GetUsageAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws 409 <see cref="OrgSeatErrors.LimitReached"/> when the org has no free seat. <b>The caller holds the seats lock
    /// of the org</b> and takes the seat (inserts the invitation, reactivates the member) before releasing it: checked
    /// alone, two requests could both see the last seat free.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgSeatErrors.LimitReached"/>.</exception>
    Task EnsureSeatAvailableAsync(Guid orgId, CancellationToken cancellationToken = default);
}

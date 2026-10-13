namespace Casazen.Core.Services;

/// <summary>What a member asks (<see cref="IOrgAccessRequestService.RequestAsync"/>).</summary>
/// <param name="OrgId">The org of the caller (read from its own account by the controller, never from the request).</param>
/// <param name="ActorUserId">The member who asks (<c>User.Id</c>).</param>
/// <param name="Area">One of <see cref="Casazen.Core.OrgTeam.OrgAccessRequestRules.Areas"/>.</param>
/// <param name="Note">A short note for the administrators; it goes in the email and nowhere else.</param>
/// <param name="Language">The language of the emails: <c>it</c> (default) or <c>en</c>.</param>
public sealed record RequestOrgAccess(Guid OrgId, string ActorUserId, string Area, string? Note, string? Language = null);

/// <summary>The outcome of a request: how many administrators were told.</summary>
/// <param name="Notified">The administrators an email was queued for (0 when there was nobody to tell or the queue refused every email).</param>
public sealed record OrgAccessRequested(int Notified);

/// <summary>Stable codes of the refusals of <see cref="IOrgAccessRequestService"/>, translated by the clients.</summary>
public static class OrgAccessRequestErrors
{
    /// <summary>422: the area or page asked for is not one a member can ask access to.</summary>
    public const string AreaUnknown = "access_request_area_unknown";

    /// <summary>409: the member already sent as many requests as it may in 24 hours; the administrators have been told.</summary>
    public const string LimitReached = "access_request_limit_reached";
}

/// <summary>
/// A member who cannot do something asks the administrators of its org to give it access (AM-02b, wave spec A6). Nothing is
/// stored but the line in the activity log (<c>AccessRequested</c>: who, which area; never the note): the answer is the
/// administrators changing the role or the properties of the person, as they always could.
/// </summary>
/// <remarks>
/// <para><b>Who is told.</b> The active owner and administrators of the org (the people who hold <c>org.members.manage</c>),
/// whose account is active and has an email, but for the requester itself. One email each, queued after the commit, in the
/// language of the request.</para>
/// <para><b>Limits.</b> The endpoint is rate limited per person; on top of it a member sends at most
/// <c>OrgTeam:AccessRequestDailyLimit</c> (3) requests in 24 hours, counted from the activity log under a lock per person, so
/// the limit holds across API instances: the next is 409 <see cref="OrgAccessRequestErrors.LimitReached"/>.</para>
/// </remarks>
public interface IOrgAccessRequestService
{
    /// <exception cref="UnauthorizedAccessException">The caller is not an active member of the org (403).</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><see cref="OrgAccessRequestErrors.AreaUnknown"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgAccessRequestErrors.LimitReached"/>.</exception>
    Task<OrgAccessRequested> RequestAsync(RequestOrgAccess request, CancellationToken cancellationToken = default);
}

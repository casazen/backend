namespace Casazen.Core.Services;

/// <summary>What a member asks (<see cref="IOrgAccessRequestService.RequestAsync"/>).</summary>
/// <param name="OrgId">The org of the caller (read from its own account by the controller, never from the request).</param>
/// <param name="ActorUserId">The member who asks (<c>User.Id</c>).</param>
/// <param name="Area">One of <see cref="Casazen.Core.OrgTeam.OrgAccessRequestRules.Areas"/>.</param>
/// <param name="Note">A short note for the administrators; it goes in the email and nowhere else.</param>
/// <param name="Language">The language of the emails: <c>it</c> (default) or <c>en</c>.</param>
public sealed record RequestOrgAccess(Guid OrgId, string ActorUserId, string Area, string? Note, string? Language = null);

public sealed record OrgAccessRequested(int Notified);

public static class OrgAccessRequestErrors
{
    public const string AreaUnknown = "access_request_area_unknown";

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
    Task<OrgAccessRequested> RequestAsync(RequestOrgAccess request, CancellationToken cancellationToken = default);
}

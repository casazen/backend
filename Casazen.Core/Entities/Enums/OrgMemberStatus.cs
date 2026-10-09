namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Whether a member of an org can use it (<see cref="OrgMember.Status"/>, AM-01). A deactivated member is refused with
/// 403 <c>member_inactive</c> on every request, immediately (the request tenant reads it without cache), while its
/// CasaZen account and its memberships stay as they are, so the reactivation gives everything back.
/// Persisted as int, explicit values: append-only.
/// </summary>
public enum OrgMemberStatus
{
    Active = 1,
    Deactivated = 2,
}

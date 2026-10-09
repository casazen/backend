using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>What narrows the list of the activity log; every field is optional and they combine (all must hold).</summary>
/// <param name="From">Lines at or after this instant (UTC).</param>
/// <param name="To">Lines at or before this instant (UTC).</param>
/// <param name="Types">Only these events (any of them).</param>
/// <param name="Area">Only this area.</param>
/// <param name="ActorUserId">Only what this account did.</param>
/// <param name="SystemActor">Only what nobody did: the jobs and the webhooks (it excludes <paramref name="ActorUserId"/>).</param>
public sealed record OrgActivityFilter(
    DateTime? From = null,
    DateTime? To = null,
    IReadOnlyCollection<OrgActivityType>? Types = null,
    OrgActivityArea? Area = null,
    string? ActorUserId = null,
    bool SystemActor = false);

/// <summary>One line of the activity log as the endpoints return it: ids and codes only (see <see cref="Casazen.Core.Entities.OrgActivityEntry"/>).</summary>
public sealed record OrgActivityItem(
    Guid Id,
    DateTime When,
    string? ActorUserId,
    OrgActivityArea Area,
    OrgActivityType Type,
    OrgActivitySubjectType SubjectType,
    string SubjectId,
    IReadOnlyDictionary<string, string> Details);

/// <summary>A page of the list, newest first, with how many lines match in all.</summary>
public sealed record OrgActivityPage(IReadOnlyList<OrgActivityItem> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// Reads the activity log of an org (AM-02b): the page the people who administer the org look at, and the whole matching
/// list for the CSV. Always of the org passed in, which is the caller's own (read from its account by the controller), never a
/// value of the request. Newest first; lines with the same instant by id, so a page never repeats or skips a line.
/// </summary>
public interface IOrgActivityService
{
    /// <summary>One page of the matching lines. <paramref name="page"/> counts from 1.</summary>
    Task<OrgActivityPage> ListAsync(
        Guid orgId,
        OrgActivityFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every matching line, newest first, read one at a time from the database (the CSV does not hold the log in memory). The
    /// database connection is held until the caller has read the last line or stopped.
    /// </summary>
    IAsyncEnumerable<OrgActivityItem> StreamAsync(
        Guid orgId,
        OrgActivityFilter filter,
        CancellationToken cancellationToken = default);
}

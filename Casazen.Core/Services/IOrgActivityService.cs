using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>What narrows the list of the activity log; every field is optional and they combine (all must hold).</summary>
/// <param name="From">Lines at or after this instant (UTC).</param>
/// <param name="To">Lines at or before this instant (UTC).</param>
/// <param name="Types">Only these events (any of them).</param>
/// <param name="Area">Only this area.</param>
/// <param name="ActorUserId">Only what this account did.</param>
/// <param name="SystemActor">Only what nobody did: the jobs and the webhooks (it excludes <paramref name="ActorUserId"/>).</param>
/// <param name="PeopleOnly">Only what some person did, whoever (the opposite of <paramref name="SystemActor"/>; AM-02c: the home of the account shows no «Sistema»).</param>
public sealed record OrgActivityFilter(
    DateTime? From = null,
    DateTime? To = null,
    IReadOnlyCollection<OrgActivityType>? Types = null,
    OrgActivityArea? Area = null,
    string? ActorUserId = null,
    bool SystemActor = false,
    bool PeopleOnly = false);

public sealed record OrgActivityItem(
    Guid Id,
    DateTime When,
    string? ActorUserId,
    OrgActivityArea Area,
    OrgActivityType Type,
    OrgActivitySubjectType SubjectType,
    string SubjectId,
    IReadOnlyDictionary<string, string> Details);

public sealed record OrgActivityPage(IReadOnlyList<OrgActivityItem> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// Reads the activity log of an org (AM-02b): the page the people who administer the org look at, and the whole matching
/// list for the CSV. Always of the org passed in, which is the caller's own (read from its account by the controller), never a
/// value of the request. Newest first; lines with the same instant by id, so a page never repeats or skips a line.
/// </summary>
public interface IOrgActivityService
{
    Task<OrgActivityPage> ListAsync(
        Guid orgId,
        OrgActivityFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrgActivityItem>> LatestAsync(
        Guid orgId,
        OrgActivityFilter filter,
        int count,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<OrgActivityItem> StreamAsync(
        Guid orgId,
        OrgActivityFilter filter,
        CancellationToken cancellationToken = default);
}

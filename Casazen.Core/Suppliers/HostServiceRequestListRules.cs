using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The quick filters (chips) of the host's list of interventions (SR-08a, <c>view</c> of <c>GET api/service-requests</c>). The first
/// four partition the requests by status, so each request is in exactly one of them and their counters add up to
/// <see cref="All"/>; <see cref="NeedsAction"/> is another way to cut the same requests and overlaps them.
/// </summary>
public enum HostServiceRequestView
{
    /// <summary>Every request.</summary>
    All,

    /// <summary>"In attesa": waiting for the supplier's answer (<see cref="ServiceRequestStatus.Richiesto"/>).</summary>
    Pending,

    /// <summary>"In corso": taken or in progress (<see cref="ServiceRequestStatus.PresoInCarico"/>, <see cref="ServiceRequestStatus.InCorso"/>).</summary>
    Active,

    /// <summary>"Da pagare": finished, not paid (<see cref="ServiceRequestStatus.Completato"/>).</summary>
    ToPay,

    /// <summary>"Conclusi": paid, refused or cancelled (the statuses nothing leaves, <see cref="ServiceRequestStateMachine.IsFinal"/>).</summary>
    Done,

    /// <summary>The requests that ask something of the host (<see cref="HostServiceRequestTodoRules.NeedTheHost"/>, the definition of the Home).</summary>
    NeedsAction,
}

/// <summary>What the list of interventions is ordered by (SR-08a).</summary>
public enum HostServiceRequestSortKey
{
    /// <summary>When the work is (its scheduled time); the requests with no time come last, whichever the direction.</summary>
    Scheduled,

    /// <summary>When the request was made.</summary>
    Created,

    /// <summary>What waits for the host first, then the status in the order of the work (the order the page opens on).</summary>
    Status,
}

/// <summary>An order of the list: the key and the direction. The id always follows, so a page boundary never repeats or skips a request.</summary>
public sealed record HostServiceRequestSort(HostServiceRequestSortKey Key, bool Descending = false);

/// <summary>
/// What a host asks of its list of interventions (short-rent). Everything is optional; the filters narrow each other. The
/// caller has already checked the property and the stay it names.
/// </summary>
/// <param name="View">The chip.</param>
/// <param name="Status">One status (the older <c>status</c> parameter).</param>
/// <param name="PropertyId">The requests of one property.</param>
/// <param name="BookingId">The requests of one stay.</param>
/// <param name="SupplierOrgId">The requests sent to one supplier.</param>
/// <param name="Category">A category code (<see cref="ServiceCategories"/>), already normalized.</param>
/// <param name="From">First Europe/Rome day, included: the work's day (its scheduled time, else the day of the request).</param>
/// <param name="To">Last Europe/Rome day, included.</param>
/// <param name="Text">A free text, already trimmed (<see cref="HostServiceRequestListRules.TryNormalizeText"/>).</param>
/// <param name="Sort">The order; <c>null</c> keeps the newest request first, as the list always was.</param>
public sealed record HostServiceRequestQuery(
    HostServiceRequestView View = HostServiceRequestView.All,
    ServiceRequestStatus? Status = null,
    Guid? PropertyId = null,
    Guid? BookingId = null,
    Guid? SupplierOrgId = null,
    string? Category = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Text = null,
    HostServiceRequestSort? Sort = null,
    int Page = 1,
    int PageSize = HostServiceRequestLimits.DefaultPageSize);

/// <summary>Limits of a page of the host's list.</summary>
public static class HostServiceRequestLimits
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 10_000;
}

/// <summary>The counters of the chips (<c>GET api/service-requests/counts</c>): the requests that pass the filters but the chip.</summary>
public sealed record HostServiceRequestCounts(int All, int Pending, int Active, int ToPay, int Done, int NeedsAction);

/// <summary>
/// The rules of the host's list of interventions that are not the shape of a query (SR-08a): the chips, the order, and how they are
/// written in a request. Each is defined once, here: the list, the counters of the chips and the tests all read these.
/// </summary>
public static class HostServiceRequestListRules
{
    /// <summary>The statuses nothing leaves (<see cref="ServiceRequestStateMachine.IsFinal"/>), in an array a query can turn into an IN list.</summary>
    private static readonly ServiceRequestStatus[] ClosedStatuses =
        [.. Enum.GetValues<ServiceRequestStatus>().Where(ServiceRequestStateMachine.IsFinal)];

    /// <summary>The requests of a chip. <see cref="HostServiceRequestView.All"/> keeps every request.</summary>
    public static Expression<Func<ServiceRequest, bool>> InView(HostServiceRequestView view) => view switch
    {
        HostServiceRequestView.Pending => r => r.Status == ServiceRequestStatus.Richiesto,
        HostServiceRequestView.Active => r => r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso,
        HostServiceRequestView.ToPay => r => r.Status == ServiceRequestStatus.Completato,
        HostServiceRequestView.Done => r => ClosedStatuses.Contains(r.Status),
        HostServiceRequestView.NeedsAction => HostServiceRequestTodoRules.NeedTheHost(),
        _ => r => true,
    };

    /// <summary>
    /// The place of a status on the way of a request, for the order that opens the page, after what asks something of the host
    /// (<see cref="HostServiceRequestTodoRules.NeedTheHost"/> comes first, a finished job and an answer to give included): what was
    /// refused (the host has to do the work again), the work in progress, the taken jobs, the new requests, and last what is over.
    /// </summary>
    public static Expression<Func<ServiceRequest, int>> StatusRank() =>
        r => r.Status == ServiceRequestStatus.Rifiutato ? 0
            : r.Status == ServiceRequestStatus.InCorso ? 1
            : r.Status == ServiceRequestStatus.PresoInCarico ? 2
            : r.Status == ServiceRequestStatus.Richiesto ? 3
            : r.Status == ServiceRequestStatus.Completato ? 4
            : r.Status == ServiceRequestStatus.Pagato ? 5
            : 6;

    /// <summary>
    /// The chip named by <paramref name="value"/> (any case, one name: <c>all</c>, <c>pending</c>, <c>active</c>, <c>topay</c>,
    /// <c>done</c> or <c>needsaction</c>); no value is <see cref="HostServiceRequestView.All"/>. False for anything else, numbers
    /// included.
    /// </summary>
    public static bool TryParseView(string? value, out HostServiceRequestView view)
    {
        view = HostServiceRequestView.All;
        return string.IsNullOrWhiteSpace(value) || EnumNames.TryParseDefined(value, out view);
    }

    /// <summary>
    /// The order named by <paramref name="value"/>: a <see cref="HostServiceRequestSortKey"/> and, after a colon, <c>asc</c> or
    /// <c>desc</c> (any case), like <c>scheduled:desc</c>. Without a direction: the next work first for <c>scheduled</c>, the newest
    /// first for <c>created</c>, what waits for the host first for <c>status</c>. No value is no order (<paramref name="sort"/> is null).
    /// False for anything else.
    /// </summary>
    public static bool TryParseSort(string? value, out HostServiceRequestSort? sort)
    {
        sort = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length > 2 || !EnumNames.TryParseDefined<HostServiceRequestSortKey>(parts[0], out var key))
            return false;

        var descending = key == HostServiceRequestSortKey.Created;
        if (parts.Length == 2)
        {
            if (string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase))
                descending = true;
            else if (string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase))
                descending = false;
            else
                return false;
        }

        sort = new HostServiceRequestSort(key, descending);
        return true;
    }

    /// <summary>
    /// The text to look for in the list: <paramref name="value"/> trimmed, 2 to 60 characters without a control character (the limits of
    /// the supplier's inbox). <c>null</c> for no text; false for a text the list refuses.
    /// </summary>
    public static bool TryNormalizeText(string? value, out string? text) => SupplierInboxSearch.TryNormalize(value, out text);
}

/// <summary>Stable codes (ProblemDetails <c>code</c>, HTTP 400) of a request to the host's list that the server does not understand (SR-08a).</summary>
public static class HostServiceRequestListErrors
{
    /// <summary><c>view</c> is not one of the chips.</summary>
    public const string InvalidViewCode = "service_request_list_invalid_view";

    /// <summary><c>sort</c> is not a key of the order, with an optional <c>:asc</c> or <c>:desc</c>.</summary>
    public const string InvalidSortCode = "service_request_list_invalid_sort";

    /// <summary><c>from</c> is after <c>to</c>.</summary>
    public const string InvalidRangeCode = "service_request_list_invalid_range";

    /// <summary><c>q</c> is shorter than 2 or longer than 60 characters, or holds a control character.</summary>
    public const string InvalidTextCode = "service_request_list_invalid_text";

    /// <summary><c>category</c> is not a code of <see cref="ServiceCategories"/>.</summary>
    public const string InvalidCategoryCode = "service_request_list_invalid_category";
}

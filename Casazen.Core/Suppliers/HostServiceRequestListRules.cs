using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Core.Suppliers;

public enum HostServiceRequestView
{
    All,

    Pending,

    Active,

    ToPay,

    Done,

    NeedsAction,
}

public enum HostServiceRequestSortKey
{
    Scheduled,

    Created,

    Status,
}

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

public static class HostServiceRequestLimits
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 10_000;
}

public sealed record HostServiceRequestCounts(int All, int Pending, int Active, int ToPay, int Done, int NeedsAction);

public static class HostServiceRequestListRules
{
    private static readonly ServiceRequestStatus[] ClosedStatuses =
        [.. Enum.GetValues<ServiceRequestStatus>().Where(ServiceRequestStateMachine.IsFinal)];

    public static Expression<Func<ServiceRequest, bool>> InView(HostServiceRequestView view) => view switch
    {
        HostServiceRequestView.Pending => r => r.Status == ServiceRequestStatus.Richiesto,
        HostServiceRequestView.Active => r => r.Status == ServiceRequestStatus.PresoInCarico || r.Status == ServiceRequestStatus.InCorso,
        HostServiceRequestView.ToPay => r => r.Status == ServiceRequestStatus.Completato,
        HostServiceRequestView.Done => r => ClosedStatuses.Contains(r.Status),
        HostServiceRequestView.NeedsAction => HostServiceRequestTodoRules.NeedTheHost(),
        _ => r => true,
    };

    public static Expression<Func<ServiceRequest, int>> StatusRank() =>
        r => r.Status == ServiceRequestStatus.Rifiutato ? 0
            : r.Status == ServiceRequestStatus.InCorso ? 1
            : r.Status == ServiceRequestStatus.PresoInCarico ? 2
            : r.Status == ServiceRequestStatus.Richiesto ? 3
            : r.Status == ServiceRequestStatus.Completato ? 4
            : r.Status == ServiceRequestStatus.Pagato ? 5
            : 6;

    public static bool TryParseView(string? value, out HostServiceRequestView view)
    {
        view = HostServiceRequestView.All;
        return string.IsNullOrWhiteSpace(value) || EnumNames.TryParseDefined(value, out view);
    }

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

    public static bool TryNormalizeText(string? value, out string? text) => SupplierInboxSearch.TryNormalize(value, out text);
}

public static class HostServiceRequestListErrors
{
    public const string InvalidViewCode = "service_request_list_invalid_view";

    public const string InvalidSortCode = "service_request_list_invalid_sort";

    public const string InvalidRangeCode = "service_request_list_invalid_range";

    public const string InvalidTextCode = "service_request_list_invalid_text";

    public const string InvalidCategoryCode = "service_request_list_invalid_category";
}

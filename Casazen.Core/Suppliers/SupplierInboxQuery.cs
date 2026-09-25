using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A page of the supplier inbox (SU-08): the requests in <paramref name="Statuses"/> (every status when empty) whose
/// activity date (<see cref="SupplierInboxStatusFilter"/>) falls within the Europe/Rome calendar days
/// <paramref name="From"/>–<paramref name="To"/> (both included, each optional), newest activity first.
/// </summary>
public sealed record SupplierInboxQuery(
    IReadOnlyCollection<ServiceRequestStatus> Statuses,
    DateOnly? From,
    DateOnly? To,
    int Page,
    int PageSize);

/// <summary>
/// The <c>status</c> values of <c>GET /api/supplier/inbox</c> (SU-08): <c>open</c> (default: waiting to be taken, taken,
/// in progress), <c>history</c> (completed, paid, rejected), <c>all</c>, or one status name
/// (<see cref="ServiceRequestStatus"/>, any case).
/// </summary>
/// <remarks>
/// The period of the inbox applies to the activity date of each request: the completion date for a completed or paid
/// request (the date the dashboard KPIs count it on, SU-11), the rejection date for a rejected one, the date it was
/// received for the open ones.
/// </remarks>
public static class SupplierInboxStatusFilter
{
    public const string OpenValue = "open";
    public const string HistoryValue = "history";
    public const string AllValue = "all";

    /// <summary>Requests the supplier still has to take or finish.</summary>
    public static readonly IReadOnlyList<ServiceRequestStatus> Open =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    /// <summary>Requests that are over for the supplier: completed, paid or rejected.</summary>
    public static readonly IReadOnlyList<ServiceRequestStatus> History =
    [
        ServiceRequestStatus.Completato,
        ServiceRequestStatus.Pagato,
        ServiceRequestStatus.Rifiutato,
    ];

    /// <summary>
    /// The statuses of <paramref name="value"/> (empty for <c>all</c>); a missing value is <c>open</c>. False for any
    /// other value, including numbers.
    /// </summary>
    public static bool TryParse(string? value, out IReadOnlyCollection<ServiceRequestStatus> statuses)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals(OpenValue, StringComparison.OrdinalIgnoreCase))
        {
            statuses = Open;
            return true;
        }

        if (text.Equals(HistoryValue, StringComparison.OrdinalIgnoreCase))
        {
            statuses = History;
            return true;
        }

        if (text.Equals(AllValue, StringComparison.OrdinalIgnoreCase))
        {
            statuses = [];
            return true;
        }

        // Only names: Enum.TryParse would also accept "3" or "1,2".
        var status = Enum.GetValues<ServiceRequestStatus>()
            .Where(s => s.ToString().Equals(text, StringComparison.OrdinalIgnoreCase))
            .Select(s => (ServiceRequestStatus?)s)
            .FirstOrDefault();
        if (status is { } single)
        {
            statuses = [single];
            return true;
        }

        statuses = [];
        return false;
    }
}

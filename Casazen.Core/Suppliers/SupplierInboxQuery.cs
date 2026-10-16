using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// A page of the supplier inbox (SU-08; SP-04): the requests in <paramref name="Statuses"/> (every status when empty) whose
/// activity date (<see cref="SupplierInboxStatusFilter"/>) falls within the Europe/Rome calendar days
/// <paramref name="From"/>–<paramref name="To"/> (both included, each optional), narrowed by the filters of the console, in
/// the order of <paramref name="Sort"/>.
/// </summary>
/// <param name="Sort">The order of the page; the default is the newest activity first.</param>
/// <param name="Service">
/// A service of the catalog (its id) or a category code: only the requests for it. A value that is neither matches nothing.
/// </param>
/// <param name="Comune">The comune of the property: its ISTAT code, or its name as the host wrote it (any case).</param>
/// <param name="When">The day of the job (<see cref="SupplierInboxWhen"/>): the requests with no day never match.</param>
/// <param name="ClientId">The customer: for a host's request, the host org (<c>clientId</c> of the inbox items).</param>
/// <param name="Source">Where the request comes from (SP-04e, <c>source</c>): only the requests of that origin.</param>
/// <param name="Text">
/// A free text (SP-04e, <c>q</c>, already trimmed, 2 to 60 characters): the requests whose service name, category (the code or its
/// label), comune contain it, any case, or whose booking code <b>is</b> it. Never the customer, the property, the notes or the address
/// (decision D9, SP-D23).
/// </param>
/// <param name="AwaitingCustomer">
/// Only for the requests the supplier has answered and the customer has not (SP-04e): <c>true</c> keeps the ones with a proposed time
/// (and, from SP-04b, a quote) waiting; <c>false</c> keeps the ones with none, which the supplier still has to answer. <c>null</c> does not
/// look at it.
/// </param>
/// <param name="WorkDays">
/// The days of the job (SP-04e, <c>today?days=</c>): like <paramref name="When"/> but for any number of Europe/Rome days; it takes
/// the place of <paramref name="When"/> when both are given.
/// </param>
public sealed record SupplierInboxQuery(
    IReadOnlyCollection<ServiceRequestStatus> Statuses,
    DateOnly? From,
    DateOnly? To,
    int Page,
    int PageSize,
    SupplierInboxSort Sort = SupplierInboxSort.Activity,
    string? Service = null,
    string? Comune = null,
    SupplierInboxWhen? When = null,
    Guid? ClientId = null,
    ServiceRequestSource? Source = null,
    string? Text = null,
    bool? AwaitingCustomer = null,
    SupplierInboxDayRange? WorkDays = null);

/// <summary>A run of Europe/Rome calendar days, both ends included.</summary>
public readonly record struct SupplierInboxDayRange(DateOnly First, DateOnly Last);

/// <summary>The limits of the free text of the inbox (<c>q</c>, SP-04e).</summary>
public static class SupplierInboxSearch
{
    /// <summary>Shortest text (after trimming): below it a search matches too much to mean anything.</summary>
    public const int MinLength = 2;

    /// <summary>Longest text.</summary>
    public const int MaxLength = 60;

    /// <summary>
    /// The text to search for: <paramref name="value"/> trimmed. <c>null</c> for nothing to search (a missing or blank value). False
    /// when it is shorter than <see cref="MinLength"/> or longer than <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool TryNormalize(string? value, out string? text)
    {
        text = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        if (trimmed.Length is < MinLength or > MaxLength)
            return false;

        text = trimmed;
        return true;
    }
}

/// <summary>The order of a page of the inbox.</summary>
public enum SupplierInboxSort
{
    /// <summary>Newest activity first (completion, rejection or cancellation date, else the date received): history.</summary>
    Activity,

    /// <summary>The request that has to be answered first comes first (<c>ResponseDueAt</c>, then the oldest): new requests.</summary>
    Urgency,

    /// <summary>The job that is first in time comes first (the scheduled time, else the check-out day; undated last): jobs.</summary>
    WorkTime,
}

/// <summary>The tabs of the inbox (SP-04, <c>tab</c> of <c>GET api/supplier/inbox</c>): each one is a set of statuses and an order.</summary>
public enum SupplierInboxTab
{
    /// <summary>"Nuove": waiting for the supplier's answer (<c>Richiesto</c>), the one to answer first on top.</summary>
    New,

    /// <summary>"Programmate": taken or in progress (<c>PresoInCarico</c>, <c>InCorso</c>), the next job on top.</summary>
    Scheduled,

    /// <summary>"Da incassare": completed and not paid yet (<c>Completato</c>), the latest on top.</summary>
    ToCollect,

    /// <summary>"Archivio": over for the supplier (<c>Pagato</c>, <c>Rifiutato</c>, <c>Annullato</c>), the latest on top.</summary>
    Archive,

    /// <summary>
    /// "Da rispondere" (SP-04e): <c>Richiesto</c> with <b>no</b> proposed time (nor quote) waiting: the supplier still has to answer,
    /// the one to answer first on top. The two tabs <see cref="ToAnswer"/> and <see cref="AwaitingCustomer"/> split <see cref="New"/>.
    /// </summary>
    ToAnswer,

    /// <summary>
    /// "In attesa del cliente" (SP-04e): <c>Richiesto</c> <b>with</b> a proposed time (or a quote) the customer has not answered: the
    /// supplier did its part, the one to expire first on top.
    /// </summary>
    AwaitingCustomer,
}

/// <summary>The <c>tab</c> values of the inbox and what each one means.</summary>
public static class SupplierInboxTabs
{
    public const string NewValue = "nuove";
    public const string ScheduledValue = "programmate";
    public const string ToCollectValue = "da-incassare";
    public const string ArchiveValue = "archivio";
    public const string ToAnswerValue = "da-rispondere";
    public const string AwaitingCustomerValue = "in-attesa";

    /// <summary>The tab named <paramref name="value"/> (any case, <c>_</c> or <c>-</c>); false for any other value, including numbers.</summary>
    public static bool TryParse(string? value, out SupplierInboxTab tab)
    {
        switch (value?.Trim().Replace('_', '-').ToLowerInvariant())
        {
            case NewValue:
                tab = SupplierInboxTab.New;
                return true;
            case ScheduledValue:
                tab = SupplierInboxTab.Scheduled;
                return true;
            case ToCollectValue:
                tab = SupplierInboxTab.ToCollect;
                return true;
            case ArchiveValue:
                tab = SupplierInboxTab.Archive;
                return true;
            case ToAnswerValue:
                tab = SupplierInboxTab.ToAnswer;
                return true;
            case AwaitingCustomerValue:
                tab = SupplierInboxTab.AwaitingCustomer;
                return true;
            default:
                tab = default;
                return false;
        }
    }

    /// <summary>The statuses of a tab.</summary>
    public static IReadOnlyList<ServiceRequestStatus> StatusesOf(SupplierInboxTab tab) => tab switch
    {
        SupplierInboxTab.New or SupplierInboxTab.ToAnswer or SupplierInboxTab.AwaitingCustomer => [ServiceRequestStatus.Richiesto],
        SupplierInboxTab.Scheduled => [ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso],
        SupplierInboxTab.ToCollect => [ServiceRequestStatus.Completato],
        SupplierInboxTab.Archive => [ServiceRequestStatus.Pagato, ServiceRequestStatus.Rifiutato, ServiceRequestStatus.Annullato],
        _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, "Unknown inbox tab."),
    };

    /// <summary>The order of a tab: the urgent first for the new ones, the next job first for the scheduled ones, the latest first for the others.</summary>
    public static SupplierInboxSort SortOf(SupplierInboxTab tab) => tab switch
    {
        SupplierInboxTab.New or SupplierInboxTab.ToAnswer or SupplierInboxTab.AwaitingCustomer => SupplierInboxSort.Urgency,
        SupplierInboxTab.Scheduled => SupplierInboxSort.WorkTime,
        _ => SupplierInboxSort.Activity,
    };

    /// <summary>
    /// Whether a tab keeps only the requests the customer has to answer (<c>true</c>), only the ones the supplier has to (<c>false</c>), or
    /// does not tell them apart (<c>null</c>): see <see cref="SupplierInboxQuery.AwaitingCustomer"/>. <see cref="SupplierInboxTab.New"/>
    /// is the sum of the two and stays what it was.
    /// </summary>
    public static bool? AwaitingCustomerOf(SupplierInboxTab tab) => tab switch
    {
        SupplierInboxTab.ToAnswer => false,
        SupplierInboxTab.AwaitingCustomer => true,
        _ => null,
    };
}

/// <summary>The day of the job asked by the <c>when</c> filter of the inbox.</summary>
public enum SupplierInboxWhen
{
    /// <summary>"oggi": today.</summary>
    Today,

    /// <summary>"settimana": the next 7 days, today included.</summary>
    Week,

    /// <summary>"mese": the current month.</summary>
    Month,
}

/// <summary>The <c>when</c> values of the inbox and the days they cover.</summary>
public static class SupplierInboxWhens
{
    public const string TodayValue = "oggi";
    public const string WeekValue = "settimana";
    public const string MonthValue = "mese";

    /// <summary>The filter named <paramref name="value"/> (any case); false for any other value, including numbers.</summary>
    public static bool TryParse(string? value, out SupplierInboxWhen when)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case TodayValue:
                when = SupplierInboxWhen.Today;
                return true;
            case WeekValue:
                when = SupplierInboxWhen.Week;
                return true;
            case MonthValue:
                when = SupplierInboxWhen.Month;
                return true;
            default:
                when = default;
                return false;
        }
    }

    /// <summary>The Europe/Rome days (both included) the filter covers when today is <paramref name="today"/>.</summary>
    public static (DateOnly From, DateOnly To) RangeOf(SupplierInboxWhen when, DateOnly today) => when switch
    {
        SupplierInboxWhen.Today => (today, today),
        SupplierInboxWhen.Week => (today, today.AddDays(6)),
        SupplierInboxWhen.Month => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
        _ => throw new ArgumentOutOfRangeException(nameof(when), when, "Unknown inbox period."),
    };
}

/// <summary>
/// The <c>status</c> values of <c>GET /api/supplier/inbox</c> (SU-08): <c>open</c> (default: waiting to be taken, taken,
/// in progress), <c>history</c> (completed, paid, rejected, cancelled), <c>all</c>, or one status name
/// (<see cref="ServiceRequestStatus"/>, any case). Since SP-04 the <c>tab</c> parameter does the same job in the words of the
/// console and, when it is sent, <c>status</c> is ignored.
/// </summary>
/// <remarks>
/// The period of the inbox applies to the activity date of each request: the completion date for a completed or paid
/// request (the date the dashboard KPIs count it on, SU-11), the rejection date for a rejected one, the cancellation date for
/// a cancelled one, the date it was received for the open ones.
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

    /// <summary>Requests that are over for the supplier: completed, paid, rejected or cancelled.</summary>
    public static readonly IReadOnlyList<ServiceRequestStatus> History =
    [
        ServiceRequestStatus.Completato,
        ServiceRequestStatus.Pagato,
        ServiceRequestStatus.Rifiutato,
        ServiceRequestStatus.Annullato,
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

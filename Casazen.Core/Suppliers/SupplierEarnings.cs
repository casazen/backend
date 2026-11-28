using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

public static class SupplierEarningStates
{
    public const string ToCollect = "to_collect";

    public const string Paid = "paid";

    public const string Late = "late";

    public const string InReview = "in_review";

    public const string Refunded = "refunded";

    public const string PartiallyRefunded = "partially_refunded";

    public const string Held = "held";

    public const string InDispute = "in_dispute";

    public static string Of(ServiceRequestStatus requestStatus, ServicePaymentStatus? paymentStatus, DateTime? lateAt) => paymentStatus switch
    {
        null => requestStatus == ServiceRequestStatus.Pagato ? Paid : ToCollect,
        ServicePaymentStatus.Paid or ServicePaymentStatus.Released => Paid,
        ServicePaymentStatus.PartiallyRefunded => PartiallyRefunded,
        ServicePaymentStatus.Refunded => Refunded,
        ServicePaymentStatus.NeedsReview => InReview,
        ServicePaymentStatus.Authorized or ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed => Held,
        ServicePaymentStatus.Disputed => InDispute,
        ServicePaymentStatus.Requested or ServicePaymentStatus.Failed => lateAt is null ? ToCollect : Late,
        _ => ToCollect,
    };
}

public enum SupplierEarningsTab
{
    All,

    ToCollect,

    Paid,

    Late,

    ToVerify,
}

public static class SupplierEarningsTabs
{
    public const string AllValue = "tutti";
    public const string ToCollectValue = "da-incassare";
    public const string PaidValue = "pagati";
    public const string LateValue = "in-ritardo";
    public const string ToVerifyValue = "da-verificare";

    public static bool TryParse(string? value, out SupplierEarningsTab tab)
    {
        switch (value?.Trim().Replace('_', '-').ToLowerInvariant())
        {
            case null or "" or AllValue:
                tab = SupplierEarningsTab.All;
                return true;
            case ToCollectValue:
                tab = SupplierEarningsTab.ToCollect;
                return true;
            case PaidValue:
                tab = SupplierEarningsTab.Paid;
                return true;
            case LateValue:
                tab = SupplierEarningsTab.Late;
                return true;
            case ToVerifyValue:
                tab = SupplierEarningsTab.ToVerify;
                return true;
            default:
                tab = default;
                return false;
        }
    }
}

public static class SupplierEarningsLimits
{
    public const int MaxPageSize = 100;

    public const int MaxExportRows = 20_000;

    public const int SeriesMonths = 8;
}

/// <summary>
/// A page of the earnings list. <paramref name="From"/> and <paramref name="To"/> are Europe/Rome calendar days, both included, on the
/// <b>date of the row</b>: the day it was paid for a paid job, otherwise the day it was completed (the day the money moved, or the work was done).
/// </summary>
public sealed record SupplierEarningsQuery(
    SupplierEarningsTab Tab,
    DateOnly? From,
    DateOnly? To,
    Guid? ClientId,
    int Page,
    int PageSize);

/// <summary>
/// One completed or paid job with its money (SP-16a). <b>The server does every sum</b>: the client never recomputes a commission or a net.
/// </summary>
/// <param name="RequestId">The request (the same id as <c>GET api/supplier/inbox/{id}</c>).</param>
/// <param name="ServiceName">The name of the service when the request was made; null for a request by category only.</param>
/// <param name="Category">The category code of the request.</param>
/// <param name="ClientId">The customer: the host org, or the private customer of the showcase (the id <c>clientId</c> filters the inbox with).</param>
/// <param name="ClientLabel">
/// The customer as the supplier sees it for a taken job (decision D9): the host org's name, or the private customer's full name. Null when the
/// customer was anonymized (<paramref name="ClientAnonymized"/>).
/// </param>
/// <param name="ClientAnonymized">The private customer was removed by the retention job: the console says "Cliente rimosso".</param>
/// <param name="CompletedAt">When the supplier completed the work.</param>
/// <param name="State">One of <see cref="SupplierEarningStates"/>.</param>
/// <param name="PaymentMode">How the request is paid: <c>Online</c> (inside CasaZen, with the commission) or <c>Manual</c> (outside, no commission).</param>
/// <param name="GrossCents">What the payer pays, in cents: the record's amount, else the total the supplier declared; null while it declared none.</param>
/// <param name="CommissionPercent">The percentage the payment was created with (a snapshot); 0 for a <c>Manual</c> job; null while an <c>Online</c> job has no payment yet.</param>
/// <param name="CommissionCents">What CasaZen keeps, in cents (0 when none is charged); null while an <c>Online</c> job has no payment yet or no amount.</param>
/// <param name="NetCents">What the supplier receives before Stripe's own fees: gross minus commission; null where the commission is.</param>
/// <param name="PaidAt">When it was paid.</param>
/// <param name="PaidVia"><c>Stripe</c> or <c>Offline</c> (outside CasaZen, recorded by the supplier or marked paid by the host); null until it is paid.</param>
/// <param name="RefundedCents">Cents given back to the payer so far. They do not change the net: the row keeps the figures of the payment.</param>
/// <param name="LateSince">When the payment was flagged late; only for a <c>late</c> row.</param>
public sealed record SupplierEarningRow(
    Guid RequestId,
    string? ServiceName,
    string Category,
    Guid ClientId,
    string? ClientLabel,
    bool ClientAnonymized,
    DateTime? CompletedAt,
    string State,
    ServiceRequestPaymentMode PaymentMode,
    int? GrossCents,
    decimal? CommissionPercent,
    int? CommissionCents,
    int? NetCents,
    DateTime? PaidAt,
    ServicePaymentChannel? PaidVia,
    int RefundedCents,
    DateTime? LateSince);

public sealed record SupplierToCollect(long Cents, int Jobs);

public sealed record SupplierPaidPayment(DateTime PaidAt, int CommissionCents, int NetCents);

public sealed record SupplierEarningsMonth(string Month, long NetCents);

/// <summary>
/// The commission that applies to a payment created now: the supplier's own while its override is in force, else the platform's. It is what the payments
/// use (<c>SupplierPayments:CommissionPercent</c>), never a number a screen writes. <paramref name="FreeUntil"/> is when a free period (an override of
/// 0 %) ends; null when there is none or it has no end.
/// </summary>
public sealed record SupplierCommissionInfo(decimal Percent, DateTime? FreeUntil);

/// <summary>The figures above the list of the earnings (SP-16a), independent of the filters of the list.</summary>
/// <param name="ToCollect">What is still to be collected: for a job paid outside CasaZen or not asked for yet the total the supplier declared, for one asked for inside it the net of its payment.</param>
/// <param name="PaidThisMonthNetCents">The net of the payments <c>Paid</c> with a payment date in the current Europe/Rome month: the same figure as the home.</param>
/// <param name="CommissionThisMonthCents">The commission CasaZen kept on them.</param>
/// <param name="LateCount">Payments asked for that are late and still owed.</param>
/// <param name="Series">The net paid in each of the last <see cref="SupplierEarningsLimits.SeriesMonths"/> Europe/Rome months, the current one last.</param>
/// <param name="Commission">The commission that applies now.</param>
/// <param name="OnlinePayments">The flag <c>SupplierOnlinePayments</c> is on and the supplier has at least one payment.</param>
public sealed record SupplierEarningsTotals(
    SupplierToCollect ToCollect,
    long PaidThisMonthNetCents,
    long CommissionThisMonthCents,
    int LateCount,
    IReadOnlyList<SupplierEarningsMonth> Series,
    SupplierCommissionInfo Commission,
    bool OnlinePayments);

public sealed record SupplierEarningsPage(IReadOnlyList<SupplierEarningRow> Items, int Total, SupplierEarningsTotals Totals);

public sealed record SupplierEarningMonthWindow(string Month, DateTime FromUtc, DateTime ToUtc);

public static class SupplierEarningMonths
{
    public static IReadOnlyList<SupplierEarningMonthWindow> Last(DateOnly today, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var current = new DateOnly(today.Year, today.Month, 1);
        var windows = new List<SupplierEarningMonthWindow>(count);
        for (var back = count - 1; back >= 0; back--)
        {
            var first = current.AddMonths(-back);
            windows.Add(new SupplierEarningMonthWindow(
                $"{first.Year:D4}-{first.Month:D2}",
                RomeCalendar.StartOfDayUtc(first),
                RomeCalendar.StartOfDayUtc(first.AddMonths(1))));
        }

        return windows;
    }
}

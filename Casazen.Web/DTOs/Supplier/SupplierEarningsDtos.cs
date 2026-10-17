using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>
/// A page of <c>GET api/supplier/earnings</c> (SP-16a): the supplier's completed and paid jobs with the money of each, the newest first, and the figures
/// above the list. <b>Every amount is in cents of euro and every sum is made by the server</b>: a screen never recomputes a commission or a net.
/// </summary>
public class SupplierEarningsResponse
{
    public IEnumerable<SupplierEarningDto> Items { get; set; } = [];

    /// <summary>How many rows the filters select, whatever the page.</summary>
    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    /// <summary>The time zone of the days and of the months: <c>Europe/Rome</c>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>The figures above the list, independent of the tab, the period and the customer of the list.</summary>
    public SupplierEarningsSummaryDto Summary { get; set; } = new();
}

/// <summary>One completed or paid job with its money.</summary>
public class SupplierEarningDto
{
    /// <summary>The request: the same id as <c>GET api/supplier/inbox/{id}</c>.</summary>
    public Guid RequestId { get; set; }

    /// <summary>The name of the service when the request was made; null for a request by category only (then <see cref="Category"/> is what to show).</summary>
    public string? ServiceName { get; set; }

    /// <summary>The category code of the request.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The customer: the host org or the private customer of the showcase. Filter the list (<c>clientId</c>) and the inbox with it.</summary>
    public Guid ClientId { get; set; }

    /// <summary>
    /// The customer as the supplier sees it for a taken job (decision D9): the host org's name, or the private customer's full name. Null when the customer
    /// was removed (<see cref="ClientAnonymized"/>).
    /// </summary>
    public string? ClientLabel { get; set; }

    /// <summary>The private customer was removed by the retention of the personal data: the console says "Cliente rimosso".</summary>
    public bool ClientAnonymized { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// <c>to_collect</c>, <c>paid</c>, <c>late</c>, <c>in_review</c>, <c>refunded</c> or <c>partially_refunded</c>. <b>An open set</b>: the held payments of
    /// SP-15c will add values (blocked, held, released, in dispute); show a generic wording for a value you do not know.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary><c>Online</c> (paid inside CasaZen, with the commission) or <c>Manual</c> (paid outside it, no commission).</summary>
    public string PaymentMode { get; set; } = string.Empty;

    /// <summary>What the payer pays: the payment's amount, else the total the supplier declared; null while it declared none.</summary>
    public int? GrossCents { get; set; }

    /// <summary>The percentage the payment was created with (a snapshot); 0 for a <c>Manual</c> job; null while an <c>Online</c> job has no payment yet.</summary>
    public decimal? CommissionPercent { get; set; }

    /// <summary>What CasaZen keeps (0 when none is charged); null where <see cref="CommissionPercent"/> is.</summary>
    public int? CommissionCents { get; set; }

    /// <summary>What the supplier receives before Stripe's own fees (gross minus commission); null where the commission is.</summary>
    public int? NetCents { get; set; }

    public DateTime? PaidAt { get; set; }

    /// <summary><c>Stripe</c>, or <c>Offline</c> (paid outside CasaZen: recorded by the supplier or marked paid by the host); null until it is paid.</summary>
    public string? PaidVia { get; set; }

    /// <summary>What was given back to the payer so far. The net is not rewritten: the row keeps the figures of the payment.</summary>
    public int RefundedCents { get; set; }

    /// <summary>When the payment was flagged late; only on a <c>late</c> row (the days late are a display matter).</summary>
    public DateTime? LateSince { get; set; }
}

/// <summary>The figures above the list of the earnings.</summary>
public class SupplierEarningsSummaryDto
{
    /// <summary>
    /// What is still to be collected: for a job paid outside CasaZen or not asked for yet the total the supplier declared, for one asked for inside it the net
    /// of its payment. The same figure as the home.
    /// </summary>
    public long ToCollectNetCents { get; set; }

    public int ToCollectJobs { get; set; }

    /// <summary>
    /// The net of the payments <c>Paid</c> with a payment date in the current month (Europe/Rome), a payment recorded as received outside CasaZen included:
    /// the same figure as the home. A payment refunded afterwards is not in it; a job the host marked paid by hand has no payment and is not either.
    /// </summary>
    public long PaidThisMonthNetCents { get; set; }

    /// <summary>The commission CasaZen kept on those payments.</summary>
    public long CommissionThisMonthCents { get; set; }

    /// <summary>Payments asked for that are late and still owed.</summary>
    public int LateCount { get; set; }

    /// <summary>The net paid in each of the last eight months (Europe/Rome), the current one last; the last month is <see cref="PaidThisMonthNetCents"/>.</summary>
    public IEnumerable<SupplierEarningsMonthDto> Series { get; set; } = [];

    /// <summary>The commission that applies to a payment created now.</summary>
    public SupplierEarningsCommissionDto Commission { get; set; } = new();

    /// <summary>The flag <c>SupplierOnlinePayments</c> is on and the supplier has at least one payment.</summary>
    public bool OnlinePayments { get; set; }
}

/// <summary>The net paid in a month.</summary>
public class SupplierEarningsMonthDto
{
    /// <summary>The month, <c>yyyy-MM</c> (Europe/Rome).</summary>
    public string Month { get; set; } = string.Empty;

    public long NetCents { get; set; }
}

/// <summary>
/// The commission that applies to a payment created now: the supplier's own while its override is in force, else the platform's
/// (<c>SupplierPayments:CommissionPercent</c>). The number comes only from here: no screen writes it.
/// </summary>
public class SupplierEarningsCommissionDto
{
    public decimal Percent { get; set; }

    /// <summary>When a free period (an own commission of 0 %) ends; null when there is none, or it has no end.</summary>
    public DateTime? FreeUntil { get; set; }
}

/// <summary>Maps the earnings of the supplier to the API.</summary>
public static class SupplierEarningsMapper
{
    public static SupplierEarningsResponse ToResponse(SupplierEarningsPage page, int pageNumber, int pageSize) => new()
    {
        Items = page.Items.Select(ToDto).ToList(),
        Total = page.Total,
        Page = pageNumber,
        PageSize = pageSize,
        TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
        Summary = new SupplierEarningsSummaryDto
        {
            ToCollectNetCents = page.Totals.ToCollect.Cents,
            ToCollectJobs = page.Totals.ToCollect.Jobs,
            PaidThisMonthNetCents = page.Totals.PaidThisMonthNetCents,
            CommissionThisMonthCents = page.Totals.CommissionThisMonthCents,
            LateCount = page.Totals.LateCount,
            Series = page.Totals.Series.Select(month => new SupplierEarningsMonthDto { Month = month.Month, NetCents = month.NetCents }).ToList(),
            Commission = new SupplierEarningsCommissionDto { Percent = page.Totals.Commission.Percent, FreeUntil = page.Totals.Commission.FreeUntil },
            OnlinePayments = page.Totals.OnlinePayments,
        },
    };

    private static SupplierEarningDto ToDto(SupplierEarningRow row) => new()
    {
        RequestId = row.RequestId,
        ServiceName = row.ServiceName,
        Category = row.Category,
        ClientId = row.ClientId,
        ClientLabel = row.ClientLabel,
        ClientAnonymized = row.ClientAnonymized,
        CompletedAt = row.CompletedAt,
        State = row.State,
        PaymentMode = row.PaymentMode.ToString(),
        GrossCents = row.GrossCents,
        CommissionPercent = row.CommissionPercent,
        CommissionCents = row.CommissionCents,
        NetCents = row.NetCents,
        PaidAt = row.PaidAt,
        PaidVia = row.PaidVia?.ToString(),
        RefundedCents = row.RefundedCents,
        LateSince = row.LateSince,
    };
}

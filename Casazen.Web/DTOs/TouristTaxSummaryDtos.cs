using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Casazen.Web.DTOs;

/// <summary>
/// The tourist tax owed, paid and receipted per comune for a period (<c>GET /api/tourist-tax/summary</c>, NF-100): definitions in
/// <see cref="ITouristTaxSummaryService"/> and <c>docs/TECHNICAL.md</c> ("Tourist tax summary"). Every amount is in euros to the cent and
/// again in whole cents, and says its <c>title</c> with its base, so no number is without a label.
/// </summary>
public sealed class TouristTaxSummaryDto
{
    /// <summary>The name of the summary with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>recorded</c>: what the engine recorded on the stays and what the host recorded as paid, nothing estimated.</summary>
    public string Basis { get; init; } = "recorded";

    /// <summary>Always EUR.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>
    /// True when the caller reaches only some of the properties of the org. A payment belongs to the org and to no property, so the summary
    /// then shows only what is owed on the properties reached: <c>paid</c>, <c>paidCents</c>, <c>balance</c>, <c>balanceCents</c>,
    /// <c>status</c> and <c>remittances</c> are <c>null</c> in every comune, <c>paid</c>, <c>paidCents</c>, <c>balance</c> and
    /// <c>balanceCents</c> in the total. Recording a payment, deleting one and opening a receipt answer 403
    /// <c>tourist_tax_payments_need_whole_org</c>.
    /// </summary>
    public bool PaymentsHidden { get; init; }

    public TouristTaxPeriodDto Period { get; init; } = new();

    /// <summary>The comuni with stays (check-in in the period) or, when the payments are shown, with payments (whose period lies inside it), by name.</summary>
    public IReadOnlyList<ComuneTaxSummaryDto> Comuni { get; init; } = [];

    public TouristTaxSummaryTotalsDto Total { get; init; } = new();

    public static TouristTaxSummaryDto From(TouristTaxSummary summary, string title)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new TouristTaxSummaryDto
        {
            Title = title,
            PaymentsHidden = summary.PaymentsHidden,
            Period = new TouristTaxPeriodDto { From = summary.Period.From, To = summary.Period.To },
            Comuni = summary.Comuni.Select(ComuneTaxSummaryDto.From).ToList(),
            Total = TouristTaxSummaryTotalsDto.From(summary.Total),
        };
    }
}

/// <summary>The days a summary covers, both included, as Europe/Rome calendar dates (<c>yyyy-MM-dd</c>).</summary>
public sealed class TouristTaxPeriodDto
{
    public DateOnly From { get; init; }

    public DateOnly To { get; init; }
}

/// <summary>One comune of the summary.</summary>
public sealed class ComuneTaxSummaryDto
{
    public string Comune { get; init; } = string.Empty;

    public int Stays { get; init; }

    public int Nights { get; init; }

    public int Guests { get; init; }

    /// <summary>The tax the engine recorded on the stays, euros to the cent (the sum of the recorded amounts, nothing estimated).</summary>
    public decimal Owed { get; init; }

    /// <summary><see cref="Owed"/> in cents of euro, exact.</summary>
    public long OwedCents { get; init; }

    /// <summary>Stays with no tax recorded: counted apart, never taken as a tax of zero. Above zero, <see cref="Owed"/> may be incomplete.</summary>
    public int StaysWithoutAmount { get; init; }

    /// <summary>
    /// The payments of the comune whose period lies inside the period of the summary, euros to the cent; <c>null</c> when the payments are
    /// hidden (<see cref="TouristTaxSummaryDto.PaymentsHidden"/>).
    /// </summary>
    public decimal? Paid { get; init; }

    /// <summary><see cref="Paid"/> in cents of euro, exact; <c>null</c> when the payments are hidden.</summary>
    public long? PaidCents { get; init; }

    /// <summary><see cref="Owed"/> minus <see cref="Paid"/>; negative when more was paid than is owed; <c>null</c> when the payments are hidden.</summary>
    public decimal? Balance { get; init; }

    /// <summary><see cref="Balance"/> in cents of euro, exact; <c>null</c> when the payments are hidden.</summary>
    public long? BalanceCents { get; init; }

    /// <summary>
    /// <c>NothingOwed</c>, <c>ToPay</c>, <c>Paid</c> or <c>Overpaid</c>; <c>null</c> when the payments are hidden. <c>NothingOwed</c> means that
    /// no amount is recorded, not that nothing is due: label it «Nessun importo registrato», and show the incomplete-amount note whenever
    /// <see cref="StaysWithoutAmount"/> is above zero, also for <c>Paid</c>.
    /// </summary>
    public TouristTaxSummaryStatus? Status { get; init; }

    /// <summary>The payments of the comune, oldest period first; <c>null</c> when the payments are hidden.</summary>
    public IReadOnlyList<TouristTaxRemittanceDto>? Remittances { get; init; }

    public static ComuneTaxSummaryDto From(ComuneTaxSummary comune)
    {
        ArgumentNullException.ThrowIfNull(comune);

        return new ComuneTaxSummaryDto
        {
            Comune = comune.Comune,
            Stays = comune.Stays,
            Nights = comune.Nights,
            Guests = comune.Guests,
            Owed = comune.Owed,
            OwedCents = PaymentCashRules.ToCents(comune.Owed),
            StaysWithoutAmount = comune.StaysWithoutAmount,
            Paid = comune.Paid,
            PaidCents = comune.Paid is { } paid ? PaymentCashRules.ToCents(paid) : null,
            Balance = comune.Balance,
            BalanceCents = comune.Balance is { } balance ? PaymentCashRules.ToCents(balance) : null,
            Status = comune.Status,
            Remittances = comune.Remittances?.Select(TouristTaxRemittanceDto.From).ToList(),
        };
    }
}

/// <summary>
/// The sums of the comuni of a summary; <c>balance</c> is the difference of the two totals. <c>paid</c>, <c>paidCents</c>, <c>balance</c> and
/// <c>balanceCents</c> are <c>null</c> when the payments are hidden (<see cref="TouristTaxSummaryDto.PaymentsHidden"/>).
/// </summary>
public sealed class TouristTaxSummaryTotalsDto
{
    public int Stays { get; init; }

    public int Nights { get; init; }

    public int Guests { get; init; }

    public decimal Owed { get; init; }

    public long OwedCents { get; init; }

    public int StaysWithoutAmount { get; init; }

    public decimal? Paid { get; init; }

    public long? PaidCents { get; init; }

    public decimal? Balance { get; init; }

    public long? BalanceCents { get; init; }

    public static TouristTaxSummaryTotalsDto From(TouristTaxSummaryTotals total)
    {
        ArgumentNullException.ThrowIfNull(total);

        return new TouristTaxSummaryTotalsDto
        {
            Stays = total.Stays,
            Nights = total.Nights,
            Guests = total.Guests,
            Owed = total.Owed,
            OwedCents = PaymentCashRules.ToCents(total.Owed),
            StaysWithoutAmount = total.StaysWithoutAmount,
            Paid = total.Paid,
            PaidCents = total.Paid is { } paid ? PaymentCashRules.ToCents(paid) : null,
            Balance = total.Balance,
            BalanceCents = total.Balance is { } balance ? PaymentCashRules.ToCents(balance) : null,
        };
    }
}

/// <summary>One recorded payment of the tourist tax (<c>POST /api/tourist-tax/remittances</c> answers with it).</summary>
public sealed class TouristTaxRemittanceDto
{
    public Guid Id { get; init; }

    /// <summary>The comune as the host typed it.</summary>
    public string Comune { get; init; } = string.Empty;

    public DateOnly PeriodFrom { get; init; }

    public DateOnly PeriodTo { get; init; }

    /// <summary>Euros to the cent, above zero.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    public DateOnly PaidOn { get; init; }

    public string? Reference { get; init; }

    public string? Notes { get; init; }

    /// <summary>A receipt is stored: <c>GET /api/tourist-tax/remittances/{id}/receipt</c> downloads it.</summary>
    public bool HasReceipt { get; init; }

    public static TouristTaxRemittanceDto From(TouristTaxRemittanceLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new TouristTaxRemittanceDto
        {
            Id = line.Id,
            Comune = line.Comune,
            PeriodFrom = line.PeriodFrom,
            PeriodTo = line.PeriodTo,
            Amount = line.Amount,
            AmountCents = PaymentCashRules.ToCents(line.Amount),
            PaidOn = line.PaidOn,
            Reference = line.Reference,
            Notes = line.Notes,
            HasReceipt = line.HasReceipt,
        };
    }
}

/// <summary>
/// A payment sent as a form (<c>POST /api/tourist-tax/remittances</c>, <c>multipart/form-data</c>): the fields are those of
/// <see cref="RecordRemittanceRequest"/> and an optional <c>receipt</c> file (a PDF, a JPEG or a PNG of at most 10 MB). The org and the user
/// are the caller's, never a field.
/// </summary>
public sealed class RecordRemittanceForm
{
    /// <summary>The comune: one of the comuni of the org's properties in Italy (422 <c>tourist_tax_comune_unknown</c> otherwise).</summary>
    public string? Comune { get; set; }

    /// <summary>First day of the period the payment pays, <c>yyyy-MM-dd</c>.</summary>
    [Required]
    public DateOnly? PeriodFrom { get; set; }

    /// <summary>Last day of that period, included, <c>yyyy-MM-dd</c>.</summary>
    [Required]
    public DateOnly? PeriodTo { get; set; }

    /// <summary>
    /// The amount in euros as a plain number with a dot and at most two decimals (<c>12.50</c>). Sent as text on purpose: a form is read with
    /// the culture of the request, where the Italian dot is a thousands separator and <c>12.50</c> would be read as 1250.
    /// </summary>
    public string? PaidAmount { get; set; }

    /// <summary>The day it was paid, <c>yyyy-MM-dd</c>: not after today in Europe/Rome.</summary>
    [Required]
    public DateOnly? PaidOn { get; set; }

    /// <summary>The reference of the payment (at most 200 characters).</summary>
    public string? Reference { get; set; }

    /// <summary>A note (at most 500 characters).</summary>
    public string? Notes { get; set; }

    /// <summary>The receipt: a PDF, a JPEG or a PNG of at most 10 MB, judged on its content.</summary>
    public IFormFile? Receipt { get; set; }
}

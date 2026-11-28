using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class TouristTaxSummaryDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "recorded";

    public string Currency { get; init; } = "EUR";

    public bool PaymentsHidden { get; init; }

    public TouristTaxPeriodDto Period { get; init; } = new();

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

public sealed class TouristTaxPeriodDto
{
    public DateOnly From { get; init; }

    public DateOnly To { get; init; }
}

public sealed class ComuneTaxSummaryDto
{
    public string Comune { get; init; } = string.Empty;

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

    public TouristTaxSummaryStatus? Status { get; init; }

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

public sealed class TouristTaxRemittanceDto
{
    public Guid Id { get; init; }

    public string Comune { get; init; } = string.Empty;

    public DateOnly PeriodFrom { get; init; }

    public DateOnly PeriodTo { get; init; }

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public DateOnly PaidOn { get; init; }

    public string? Reference { get; init; }

    public string? Notes { get; init; }

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

    [Required]
    public DateOnly? PeriodFrom { get; set; }

    [Required]
    public DateOnly? PeriodTo { get; set; }

    public string? PaidAmount { get; set; }

    [Required]
    public DateOnly? PaidOn { get; set; }

    public string? Reference { get; set; }

    public string? Notes { get; set; }

    public IFormFile? Receipt { get; set; }
}

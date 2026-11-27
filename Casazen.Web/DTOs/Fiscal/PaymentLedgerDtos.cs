using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

public sealed record PaymentLedgerLineDto
{
    public string Kind { get; init; } = string.Empty;
    public Guid BookingId { get; init; }
    public string BookingCode { get; init; } = string.Empty;
    public Guid? PaymentId { get; init; }
    public DateOnly Date { get; init; }
    public decimal Gross { get; init; }
    public decimal? ProviderFee { get; init; }
    public decimal? Net { get; init; }
    public string? ProviderCurrency { get; init; }
    public DateOnly? CreditDate { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string CollectedBy { get; init; } = string.Empty;
    public PaymentLedgerDocumentDto? Document { get; init; }

    public static PaymentLedgerLineDto From(PaymentLedgerLine line) => new()
    {
        Kind = line.Kind.ToString(),
        BookingId = line.BookingId,
        BookingCode = BookingCodes.Format(line.BookingCode),
        PaymentId = line.PaymentId,
        Date = line.Date,
        Gross = line.Gross,
        ProviderFee = line.ProviderFee,
        Net = line.Net,
        ProviderCurrency = line.ProviderCurrency,
        CreditDate = line.CreditDate,
        Currency = line.Currency,
        State = line.State.ToString(),
        CollectedBy = line.CollectedBy.ToString(),
        Document = line.Document is { } d ? new PaymentLedgerDocumentDto(d.Id, d.Number, d.Kind, d.Status, d.PaymentState) : null,
    };
}

public sealed record PaymentLedgerDocumentDto(Guid Id, string? Number, string Kind, string Status, string PaymentState);

public sealed record PaymentLedgerBookingDto(
    Guid BookingId,
    string BookingCode,
    string Channel,
    string Currency,
    IReadOnlyList<PaymentLedgerLineDto> Lines,
    bool Degraded);

public sealed record PaymentLedgerPageDto(IReadOnlyList<PaymentLedgerLineDto> Items, int TotalCount, int Page, int PageSize);

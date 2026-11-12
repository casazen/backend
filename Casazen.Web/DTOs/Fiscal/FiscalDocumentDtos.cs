using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// A fiscal document as the API gives it (E06/F01/US01/T01): stable codes for the kind, the status, the source, the payment and the SDI state (never a
/// display text), the stored amounts in <see cref="Currency"/>, the recipient snapshot and the lines. The XML and the PDF are separate downloads.
/// </summary>
public sealed record FiscalDocumentDto
{
    public Guid Id { get; init; }
    public Guid? BookingId { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Series { get; init; }
    public int? SequenceNumber { get; init; }
    public string? Number { get; init; }
    public DateOnly? IssueDate { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? RecipientKind { get; init; }
    public string? RecipientName { get; init; }
    public string? RecipientFiscalCode { get; init; }
    public string? RecipientVatNumber { get; init; }
    public string? RecipientAddressStreet { get; init; }
    public string? RecipientAddressCivic { get; init; }
    public string? RecipientPostalCode { get; init; }
    public string? RecipientCity { get; init; }
    public string? RecipientProvince { get; init; }
    public string? RecipientCountryCode { get; init; }
    public string? RecipientSdiCode { get; init; }
    public string? RecipientPec { get; init; }
    public decimal TaxableBase { get; init; }
    public decimal VatAmount { get; init; }
    public decimal StampDuty { get; init; }
    public decimal Total { get; init; }
    public decimal? VatRatePercent { get; init; }
    public string? VatNatureCode { get; init; }
    public string PaymentState { get; init; } = string.Empty;
    public string SdiStatus { get; init; } = string.Empty;
    public string? SdiErrorCode { get; init; }
    public DateTime? SdiStatusUpdatedAt { get; init; }
    public bool HasXml { get; init; }
    public bool OctoratePdfAvailable { get; init; }
    public Guid? CreditedDocumentId { get; init; }
    public Guid? ReplacesDocumentId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public IReadOnlyList<FiscalDocumentLineDto> Lines { get; init; } = [];

    public static FiscalDocumentDto From(FiscalDocument document) => new()
    {
        Id = document.Id,
        BookingId = document.BookingId,
        Source = document.Source.ToString(),
        Kind = document.Kind.ToString(),
        Status = document.Status.ToString(),
        Series = string.IsNullOrEmpty(document.Series) ? null : document.Series,
        SequenceNumber = document.SequenceNumber,
        Number = document.Number,
        IssueDate = document.IssueDate,
        Currency = document.Currency,
        RecipientKind = document.RecipientKind?.ToString(),
        RecipientName = document.RecipientName,
        RecipientFiscalCode = document.RecipientFiscalCode,
        RecipientVatNumber = document.RecipientVatNumber,
        RecipientAddressStreet = document.RecipientAddressStreet,
        RecipientAddressCivic = document.RecipientAddressCivic,
        RecipientPostalCode = document.RecipientPostalCode,
        RecipientCity = document.RecipientCity,
        RecipientProvince = document.RecipientProvince,
        RecipientCountryCode = document.RecipientCountryCode,
        RecipientSdiCode = document.RecipientSdiCode,
        RecipientPec = document.RecipientPec,
        TaxableBase = document.TaxableBase,
        VatAmount = document.VatAmount,
        StampDuty = document.StampDuty,
        Total = document.Total,
        VatRatePercent = document.VatRatePercent,
        VatNatureCode = document.VatNatureCode,
        PaymentState = document.PaymentState.ToString(),
        SdiStatus = document.SdiStatus,
        SdiErrorCode = document.SdiErrorCode,
        SdiStatusUpdatedAt = document.SdiStatusUpdatedAt,
        HasXml = !string.IsNullOrEmpty(document.FatturaPaXml),
        OctoratePdfAvailable = document.OctoratePdfAvailable,
        CreditedDocumentId = document.CreditedDocumentId,
        ReplacesDocumentId = document.ReplacesDocumentId,
        CreatedAt = document.CreatedAt,
        UpdatedAt = document.UpdatedAt,
        Lines = document.Lines.OrderBy(l => l.LineNumber).Select(FiscalDocumentLineDto.From).ToList(),
    };
}

/// <summary>A line of a fiscal document.</summary>
public sealed record FiscalDocumentLineDto(
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    decimal VatRatePercent,
    string? VatNatureCode)
{
    public static FiscalDocumentLineDto From(FiscalDocumentLine line) =>
        new(line.LineNumber, line.Description, line.Quantity, line.UnitPrice, line.TotalPrice, line.VatRatePercent, line.VatNatureCode);
}

/// <summary>A page of the list of the documents.</summary>
public sealed record FiscalDocumentPageDto(IReadOnlyList<FiscalDocumentDto> Items, int TotalCount, int Page, int PageSize);

/// <summary><c>POST api/fiscal/documents</c>: the draft of a booking.</summary>
public sealed record CreateFiscalDocumentRequest
{
    public Guid BookingId { get; init; }
}

/// <summary><c>POST api/fiscal/documents/{id}/issue</c>: the host confirms the issue.</summary>
public sealed record IssueFiscalDocumentRequest
{
    public bool Confirm { get; init; }
}

/// <summary><c>POST api/fiscal/documents/{id}/credit-note</c>: the amount (the whole document when left out), the reason and the confirmation.</summary>
public sealed record CreateCreditNoteRequest
{
    public decimal? Amount { get; init; }

    public string Reason { get; init; } = string.Empty;

    public bool Confirm { get; init; }
}

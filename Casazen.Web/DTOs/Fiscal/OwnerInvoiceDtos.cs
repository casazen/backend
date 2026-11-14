using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// An owner invoice as <c>api/owner-invoices</c> gives it (E14/F01/US01/T01): stable codes (never a display text), the stored amounts in <see cref="Currency"/>,
/// the owner and the period, the statement version it was made from, and the lines with their type and VAT. The recipient is named, its fiscal data are not
/// carried (they are on the document, <c>GET api/fiscal/documents/{id}</c>, and its PDF).
/// </summary>
public sealed record OwnerInvoiceDto
{
    public Guid Id { get; init; }
    public Guid? OwnerId { get; init; }
    public string? OwnerName { get; init; }
    public Guid? StatementVersionId { get; init; }
    public int? PeriodYear { get; init; }
    public int? PeriodMonth { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? Series { get; init; }
    public string? Number { get; init; }
    public DateOnly? IssueDate { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? RuleVersion { get; init; }
    public decimal TaxableBase { get; init; }
    public decimal VatAmount { get; init; }
    public decimal StampDuty { get; init; }
    public decimal Total { get; init; }
    public bool HasXml { get; init; }
    public bool CreditedInFull { get; init; }
    public IReadOnlyList<OwnerInvoiceLineDto> Lines { get; init; } = [];

    public static OwnerInvoiceDto From(FiscalDocument document) => new()
    {
        Id = document.Id,
        OwnerId = document.RecipientOwnerId,
        OwnerName = document.RecipientName,
        StatementVersionId = document.OwnerStatementVersionId,
        PeriodYear = document.PeriodYear,
        PeriodMonth = document.PeriodMonth,
        Kind = document.Kind.ToString(),
        Status = document.Status.ToString(),
        Series = string.IsNullOrEmpty(document.Series) ? null : document.Series,
        Number = document.Number,
        IssueDate = document.IssueDate,
        Currency = document.Currency,
        RuleVersion = document.RuleVersion,
        TaxableBase = document.TaxableBase,
        VatAmount = document.VatAmount,
        StampDuty = document.StampDuty,
        Total = document.Total,
        HasXml = !string.IsNullOrEmpty(document.FatturaPaXml),
        CreditedInFull = document.CreditedInFull,
        Lines = document.Lines.OrderBy(l => l.LineNumber).Select(OwnerInvoiceLineDto.From).ToList(),
    };
}

/// <summary>A line of an owner invoice: its type (<c>Commission</c>, <c>Recharge</c>, <c>AdvancedExpense</c>), the net, the rate and the VAT of the line.</summary>
public sealed record OwnerInvoiceLineDto(
    int LineNumber,
    string Type,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    decimal VatRatePercent,
    string? VatNatureCode,
    decimal? VatAmount)
{
    public static OwnerInvoiceLineDto From(FiscalDocumentLine line) => new(
        line.LineNumber, line.LineType.ToString(), line.Description, line.Quantity, line.UnitPrice, line.TotalPrice, line.VatRatePercent, line.VatNatureCode, line.VatAmount);
}

/// <summary>One owner invoice of the list: number, owner, total, currency, status and, once issued, how it is paid.</summary>
public sealed record OwnerInvoiceListItemDto(
    Guid Id,
    Guid OwnerId,
    string? OwnerName,
    int PeriodYear,
    int PeriodMonth,
    Guid? StatementVersionId,
    string? Number,
    DateOnly? IssueDate,
    string Status,
    string Kind,
    decimal TaxableBase,
    decimal VatAmount,
    decimal StampDuty,
    decimal Total,
    string Currency,
    bool CreditedInFull,
    OwnerInvoiceSettlementDto? Payment)
{
    public static OwnerInvoiceListItemDto From(OwnerInvoiceRow row) => new(
        row.Id,
        row.OwnerId,
        row.OwnerName,
        row.PeriodYear,
        row.PeriodMonth,
        row.StatementVersionId,
        row.Number,
        row.IssueDate,
        row.Status.ToString(),
        row.Kind.ToString(),
        row.TaxableBase,
        row.VatAmount,
        row.StampDuty,
        row.Total,
        row.Currency,
        row.CreditedInFull,
        row.Settlement is { } settlement ? new OwnerInvoiceSettlementDto(settlement.State, settlement.AmountDue) : null);
}

/// <summary>How an issued owner invoice is paid: <c>offset</c> (in the statement), <c>to_pay</c> (with the amount) or <c>credited</c>.</summary>
public sealed record OwnerInvoiceSettlementDto(string State, decimal AmountDue);

/// <summary><c>POST api/owner-invoices/drafts</c>: the statement version the invoice is made from.</summary>
public sealed record CreateOwnerInvoiceDraftRequest
{
    public Guid VersionId { get; init; }
}

/// <summary><c>POST api/agency-invoices/generate</c>: the period whose drafts are made.</summary>
public sealed record GenerateOwnerInvoicesRequest
{
    public int Year { get; init; }

    public int Month { get; init; }
}

/// <summary>What the drafts of a period did, with the owners whose draft could not be made and the code of the reason.</summary>
public sealed record OwnerInvoiceGenerationDto(
    int Year,
    int Month,
    int Owners,
    int Created,
    int Updated,
    int AlreadyIssued,
    int NothingToInvoice,
    IReadOnlyList<OwnerInvoiceFailure> Failed)
{
    public static OwnerInvoiceGenerationDto From(OwnerInvoiceGeneration generation) => new(
        generation.Year,
        generation.Month,
        generation.Owners,
        generation.Created,
        generation.Updated,
        generation.AlreadyIssued,
        generation.NothingToInvoice,
        generation.Failed);
}

/// <summary>The holder of a listing on a portal, where it differs from the default of the organization.</summary>
public sealed record ChannelAccountHolderDto(Guid PropertyId, string PropertyName, string Channel, string Holder, DateTime UpdatedAt)
{
    public static ChannelAccountHolderDto From(ChannelAccountHolderView view) =>
        new(view.PropertyId, view.PropertyName, view.Channel.ToString(), view.Holder.ToString(), view.UpdatedAt);
}

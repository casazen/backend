using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// One self-billed invoice (TD17) of the list (<c>GET api/fiscal/self-billing</c>, E17/F01/US03/T01): stable codes (never a display text), the stored amounts in
/// <see cref="Currency"/>, the holder it is made for (<c>Agency</c> or <c>Owner</c>), the foreign portal (the cedente), the month of the commissions and the day it is to be
/// issued by, and the state towards the SDI. The fiscal data of the holder and of the portal are on the document, <c>GET api/fiscal/documents/{id}</c>, and its PDF and XML.
/// </summary>
public sealed record SelfBillingListItemDto(
    Guid Id,
    string HolderKind,
    Guid? HolderOwnerId,
    string HolderName,
    string PortalName,
    string PortalCountry,
    int PeriodYear,
    int PeriodMonth,
    DateOnly DueDate,
    string? Number,
    DateOnly? IssueDate,
    string Status,
    decimal TaxableBase,
    decimal VatAmount,
    decimal Total,
    string Currency,
    string SdiStatus)
{
    public static SelfBillingListItemDto From(SelfBillingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SelfBillingListItemDto(
            row.Id,
            row.HolderKind.ToString(),
            row.HolderOwnerId,
            row.HolderName,
            row.PortalName,
            row.PortalCountry,
            row.PeriodYear,
            row.PeriodMonth,
            row.DueDate,
            row.Number,
            row.IssueDate,
            row.Status.ToString(),
            row.TaxableBase,
            row.VatAmount,
            row.Total,
            row.Currency,
            row.SdiStatus);
    }
}

/// <summary>A TD17 with its lines (one for each property): the row, the version of the settings it was made with, the rate of the VAT integrated and whether the XML exists.</summary>
public sealed record SelfBillingDto(
    SelfBillingListItemDto Invoice,
    string? RuleVersion,
    decimal? VatRatePercent,
    bool HasXml,
    IReadOnlyList<SelfBillingLineDto> Lines)
{
    public static SelfBillingDto From(SelfBillingDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var document = detail.Document;
        return new SelfBillingDto(
            SelfBillingListItemDto.From(detail.Row),
            document.RuleVersion,
            document.VatRatePercent,
            !string.IsNullOrEmpty(document.FatturaPaXml),
            document.Lines.OrderBy(l => l.LineNumber).Select(SelfBillingLineDto.From).ToList());
    }
}

/// <summary>A line of a TD17: the commission of one property, its net, the rate and the VAT integrated on it.</summary>
public sealed record SelfBillingLineDto(int LineNumber, string Description, decimal TotalPrice, decimal VatRatePercent, decimal? VatAmount)
{
    public static SelfBillingLineDto From(FiscalDocumentLine line) =>
        new(line.LineNumber, line.Description, line.TotalPrice, line.VatRatePercent, line.VatAmount);
}

/// <summary><c>POST api/fiscal/self-billing/drafts</c>: the month (<c>yyyy-MM</c>) whose drafts are made.</summary>
public sealed record GenerateSelfBillingRequest
{
    public string? Period { get; init; }
}

/// <summary>
/// What the drafts of a month did: how many were made, made again, left alone because they were issued already and dropped because the ledger no longer supports them, one row for
/// each TD17 (with what it is and the estimated part of its amount), the holders that need one and cannot get one yet, and the commissions that are not self-billed with the reason.
/// </summary>
public sealed record SelfBillingGenerationDto(
    int Year,
    int Month,
    int Created,
    int Updated,
    int AlreadyIssued,
    int Discarded,
    IReadOnlyList<SelfBillingOutcomeDto> Drafts,
    IReadOnlyList<SelfBillingFailureDto> Failed,
    IReadOnlyList<SelfBillingNotDueDto> NotDue)
{
    public static SelfBillingGenerationDto From(SelfBillingGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return new SelfBillingGenerationDto(
            generation.Year,
            generation.Month,
            generation.Created,
            generation.Updated,
            generation.AlreadyIssued,
            generation.Discarded,
            generation.Drafts.Select(d => new SelfBillingOutcomeDto(d.DocumentId, d.HolderKind.ToString(), d.HolderOwnerId, d.PortalName, d.Result, d.TaxableBase, d.EstimatedAmount, d.ChangedSinceIssue)).ToList(),
            generation.Failed.Select(f => new SelfBillingFailureDto(f.HolderKind.ToString(), f.HolderOwnerId, f.PortalName, f.Code, f.Fields)).ToList(),
            generation.NotDue.Select(n => new SelfBillingNotDueDto(n.Reason, n.Channel, n.Stays, n.Amount, n.Currency)).ToList());
    }
}

/// <summary>One TD17 of a generation: <c>result</c> is <c>created</c>, <c>updated</c> or <c>already_issued</c>.</summary>
public sealed record SelfBillingOutcomeDto(
    Guid DocumentId,
    string HolderKind,
    Guid? HolderOwnerId,
    string PortalName,
    string Result,
    decimal TaxableBase,
    decimal EstimatedAmount,
    bool ChangedSinceIssue);

/// <summary>A holder that needs a TD17 and cannot get one: the code of the reason and the fields its issuer profile misses.</summary>
public sealed record SelfBillingFailureDto(string HolderKind, Guid? HolderOwnerId, string PortalName, string Code, IReadOnlyList<string> Fields);

/// <summary>Commissions of the month that are not self-billed: the reason, the channel, the number of stays and their commission.</summary>
public sealed record SelfBillingNotDueDto(string Reason, string Channel, int Stays, decimal Amount, string Currency);

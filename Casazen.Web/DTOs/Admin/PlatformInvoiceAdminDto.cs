using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Admin;

/// <summary>
/// A CasaZen SaaS invoice in the admin e-invoicing queue (PL-13): the tax computed by Stripe Tax, the review flag and
/// the SDI status, with the org's e-invoice data. Name and address of the customer are on the Stripe invoice.
/// </summary>
public class PlatformInvoiceAdminDto
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string OrgName { get; set; } = string.Empty;
    public string StripeInvoiceId { get; set; } = string.Empty;
    public string? StripeInvoiceNumber { get; set; }
    public string? Currency { get; set; }
    public decimal AmountExVat { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string VatTreatment { get; set; } = string.Empty;
    public bool OssApplied { get; set; }
    public string? CustomerCountry { get; set; }
    public string? TaxCountry { get; set; }
    public decimal? VatRatePercent { get; set; }
    public string? TaxabilityReasons { get; set; }
    public string? CustomerVatIdVerification { get; set; }
    public string? TaxReviewReason { get; set; }
    public string SdiStatus { get; set; } = string.Empty;
    public string? SdiTransmissionId { get; set; }
    public string? SdiError { get; set; }
    public DateTime? SdiStatusUpdatedAt { get; set; }
    public string? VatId { get; set; }
    public string? SdiRecipientCode { get; set; }
    public string? PecEmail { get; set; }
    public string? FiscalCode { get; set; }
    public DateTime CreatedAt { get; set; }

    public static PlatformInvoiceAdminDto From(PlatformInvoice invoice) => new()
    {
        Id = invoice.Id,
        OrgId = invoice.OrgId,
        OrgName = invoice.Org?.Name ?? string.Empty,
        StripeInvoiceId = invoice.StripeInvoiceId,
        StripeInvoiceNumber = invoice.StripeInvoiceNumber,
        Currency = invoice.Currency,
        AmountExVat = invoice.AmountExVat,
        VatAmount = invoice.VatAmount,
        TotalAmount = invoice.TotalAmount,
        VatTreatment = invoice.VatTreatment,
        OssApplied = invoice.OssApplied,
        CustomerCountry = invoice.CustomerCountry,
        TaxCountry = invoice.TaxCountry,
        VatRatePercent = invoice.VatRatePercent,
        TaxabilityReasons = invoice.TaxabilityReasons,
        CustomerVatIdVerification = invoice.CustomerVatIdVerification,
        TaxReviewReason = invoice.TaxReviewReason,
        SdiStatus = invoice.SdiStatus,
        SdiTransmissionId = invoice.SdiTransmissionId,
        SdiError = invoice.SdiError,
        SdiStatusUpdatedAt = invoice.SdiStatusUpdatedAt,
        VatId = invoice.Org?.VatId,
        SdiRecipientCode = invoice.Org?.BillingSdiRecipientCode,
        PecEmail = invoice.Org?.BillingPecEmail,
        FiscalCode = invoice.Org?.BillingFiscalCode,
        CreatedAt = invoice.CreatedAt,
    };
}

/// <summary>Body of <c>POST /api/admin/platform-invoices/{id}/sdi-manual-issued</c>.</summary>
public class MarkSdiIssuedManuallyRequest
{
    /// <summary>Reference of the e-invoice issued by hand (SDI file name, invoice number): required, at most 255 characters.</summary>
    public string Reference { get; set; } = string.Empty;
}

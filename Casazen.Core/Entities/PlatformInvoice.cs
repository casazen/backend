using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A paid CasaZen SaaS invoice (Stripe <c>invoice.paid</c>). Since PL-13 (A1-08) every tax figure is copied from the
/// Stripe invoice computed by Stripe Tax: CasaZen stores what was actually charged and never applies a rate of its own.
/// </summary>
[Table("PlatformInvoices")]
public class PlatformInvoice : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(255)]
    public string StripeInvoiceId { get; set; } = string.Empty;

    /// <summary>Stripe invoice number (shown on the Stripe PDF), when finalized.</summary>
    [MaxLength(100)]
    public string? StripeInvoiceNumber { get; set; }

    /// <summary>ISO currency of the amounts (lowercase, as Stripe sends it).</summary>
    [MaxLength(3)]
    public string? Currency { get; set; }

    /// <summary>Taxable amount: Stripe <c>total_excluding_tax</c> (after discounts, before tax).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountExVat { get; set; }

    /// <summary>Tax charged: sum of Stripe <c>total_taxes</c>.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    /// <summary>Stripe <c>total</c>: what the customer paid.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    /// <summary>One of <c>PlatformInvoiceVatTreatments</c> (legacy rows: <c>IT_22</c>, <c>EU_OSS</c>, ...).</summary>
    [Required, MaxLength(32)]
    public string VatTreatment { get; set; } = string.Empty;

    /// <summary>True when Stripe Tax charged the VAT of an EU country other than Italy (OSS).</summary>
    public bool OssApplied { get; set; }

    /// <summary>Country of the customer's billing address on the invoice (ISO 3166-1 alpha-2).</summary>
    [MaxLength(2)]
    public string? CustomerCountry { get; set; }

    /// <summary>Country of the tax rate Stripe applied (single rate only).</summary>
    [MaxLength(2)]
    public string? TaxCountry { get; set; }

    /// <summary>Effective rate (percent) Stripe applied, copied from its tax rate (single rate only).</summary>
    [Column(TypeName = "decimal(7,4)")]
    public decimal? VatRatePercent { get; set; }

    /// <summary>Stripe taxability reasons of the invoice taxes, comma separated (e.g. <c>standard_rated</c>, <c>reverse_charge</c>).</summary>
    [MaxLength(200)]
    public string? TaxabilityReasons { get; set; }

    /// <summary>VIES status of the customer's EU VAT id on Stripe at recording time (reverse charge only).</summary>
    [MaxLength(32)]
    public string? CustomerVatIdVerification { get; set; }

    /// <summary>Set when the invoice needs a check (one of <c>PlatformInvoiceTaxReviewReasons</c>).</summary>
    [MaxLength(64)]
    public string? TaxReviewReason { get; set; }

    /// <summary>One of <c>PlatformInvoiceSdiStatuses</c>. Rows written before PL-13 may still read <c>pending</c>.</summary>
    [Required, MaxLength(32)]
    public string SdiStatus { get; set; } = "manual_required";

    /// <summary>Id returned by the SDI provider, or the reference of an e-invoice issued by hand.</summary>
    [MaxLength(255)]
    public string? SdiTransmissionId { get; set; }

    /// <summary>Last SDI provider error (no PII).</summary>
    [MaxLength(500)]
    public string? SdiError { get; set; }

    public DateTime? SdiStatusUpdatedAt { get; set; }

    [MaxLength(1000)]
    public string? FatturaPaXmlUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Org Org { get; set; } = null!;
}

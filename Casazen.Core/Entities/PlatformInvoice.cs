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

    [MaxLength(100)]
    public string? StripeInvoiceNumber { get; set; }

    [MaxLength(3)]
    public string? Currency { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountExVat { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Required, MaxLength(32)]
    public string VatTreatment { get; set; } = string.Empty;

    /// <summary>True when Stripe Tax charged the VAT of an EU country other than Italy (OSS).</summary>
    public bool OssApplied { get; set; }

    [MaxLength(2)]
    public string? CustomerCountry { get; set; }

    [MaxLength(2)]
    public string? TaxCountry { get; set; }

    [Column(TypeName = "decimal(7,4)")]
    public decimal? VatRatePercent { get; set; }

    [MaxLength(200)]
    public string? TaxabilityReasons { get; set; }

    /// <summary>VIES status of the customer's EU VAT id on Stripe at recording time (reverse charge only).</summary>
    [MaxLength(32)]
    public string? CustomerVatIdVerification { get; set; }

    [MaxLength(64)]
    public string? TaxReviewReason { get; set; }

    [Required, MaxLength(32)]
    public string SdiStatus { get; set; } = "manual_required";

    /// <summary>Id returned by the SDI provider, or the reference of an e-invoice issued by hand.</summary>
    [MaxLength(255)]
    public string? SdiTransmissionId { get; set; }

    [MaxLength(500)]
    public string? SdiError { get; set; }

    public DateTime? SdiStatusUpdatedAt { get; set; }

    [MaxLength(1000)]
    public string? FatturaPaXmlUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Org Org { get; set; } = null!;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// FatturaPA invoice issued by an agency to a property owner for monthly commission (ADR-019).
/// One invoice per property owner per month (art. 21 DPR 633/72: single supplier per invoice).
/// Per-agency numbering with series and no gaps enforced by unique index and row lock.
/// </summary>
[Table("AgencyInvoices")]
[Index(nameof(OrgId), nameof(Series), nameof(InvoiceNumber), IsUnique = true)]
public class AgencyInvoice : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey(nameof(PropertyOwner))]
    public Guid OwnerId { get; set; }
    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Series { get; set; } = string.Empty;

    public int Month { get; set; }

    public int Year { get; set; }

    [Precision(18, 2)]
    public decimal TaxableBase { get; set; }

    [Precision(5, 2)]
    public decimal VatRate { get; set; }

    [Precision(18, 2)]
    public decimal VatAmount { get; set; }

    /// <summary>
    /// VAT exemption code (Natura IVA, e.g., "N2.2" for non-taxable art. 7-ter DPR 633/72).
    /// Null when VAT is charged normally; required when VatRate is 0.
    /// </summary>
    [MaxLength(10)]
    public string? VatExemptionCode { get; set; }

    /// <summary>
    /// Stamp duty (bollo) amount. €2.00 if taxable base exceeds €77.47 and no VAT charged, otherwise 0.
    /// </summary>
    [Precision(18, 2)]
    public decimal StampDuty { get; set; }

    [Precision(18, 2)]
    public decimal Total { get; set; }

    /// <summary>
    /// Invoice lifecycle status: Draft (editable), ToBeIssuedManually (ready for manual SDI submission),
    /// Issued (transmitted to SDI, manually or automatically in phase 2).
    /// </summary>
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    /// <summary>
    /// FatturaPA XML content (validated against official AdE XSD, ADR-019-R6).
    /// Encrypted at rest (contains PII: owner name, address, tax code).
    /// Null while Status = Draft; populated when invoice is finalized.
    /// </summary>
    [MaxLength]
    public string? FatturapaXml { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(AgencyInvoiceCompliance.MaxLength)]
    public string ComplianceStatus { get; set; } = AgencyInvoiceCompliance.LegacyUnverified;
}

public static class AgencyInvoiceCompliance
{
    public const int MaxLength = 32;

    public const string LegacyUnverified = "legacy_unverified";
}

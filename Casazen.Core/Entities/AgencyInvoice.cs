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

    /// <summary>Issuer agency (tenant key). Inherited from the property owner; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>Recipient property owner.</summary>
    public Guid OwnerId { get; set; }
    public virtual PropertyOwner PropertyOwner { get; set; } = null!;

    /// <summary>
    /// Sequential invoice number within the series (no gaps, ADR-019-R4).
    /// Together with Series identifies the invoice uniquely within the agency.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Invoice series (configurable per agency, e.g., "2026", "COMM", empty string for default).
    /// Together with InvoiceNumber identifies the invoice uniquely within the agency.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Series { get; set; } = string.Empty;

    /// <summary>Invoice period: month (1-12).</summary>
    public int Month { get; set; }

    /// <summary>Invoice period: year (e.g., 2026).</summary>
    public int Year { get; set; }

    /// <summary>
    /// Taxable base (imponibile): commission amount before VAT. Full precision (ADR-019-R10).
    /// </summary>
    [Precision(18, 2)]
    public decimal TaxableBase { get; set; }

    /// <summary>
    /// VAT rate as a percentage (e.g., 22.00 for 22%). May be 0.00 for VAT exemption.
    /// </summary>
    [Precision(5, 2)]
    public decimal VatRate { get; set; }

    /// <summary>
    /// VAT amount (VatRate * TaxableBase / 100). Full precision.
    /// </summary>
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

    /// <summary>
    /// Total invoice amount (TaxableBase + VatAmount + StampDuty). Full precision rounded to 2 decimals.
    /// </summary>
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

    /// <summary>When this invoice was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this invoice was last updated (UTC). Updated on recalculation or status change.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

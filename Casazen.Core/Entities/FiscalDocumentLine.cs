using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// A line of a fiscal document CasaZen issues (FIS-03, E06/F01/US02/T01, E11/F01/US01/T01): lodging, cleaning, an extra service, the tourist tax, a
/// commission... (<see cref="LineType"/>). It goes with its document (cascade), which is itself never removed once issued; <see cref="LineNumber"/> is
/// unique within the document.
/// </summary>
[Table("FiscalDocumentLines")]
public class FiscalDocumentLine : ITenantOwned
{
    /// <summary>Unique (<see cref="DocumentId"/>, <see cref="LineNumber"/>).</summary>
    public const string DocumentLineIndexName = "UIX_FiscalDocumentLines_DocumentId_LineNumber";

    public const int DescriptionMaxLength = 500;
    public const int VatNatureCodeMaxLength = 4;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the document.</summary>
    public Guid OrgId { get; set; }

    public Guid DocumentId { get; set; }

    /// <summary>The number of the line within the document, from 1.</summary>
    public int LineNumber { get; set; }

    /// <summary>What the line is: its type decides the VAT of the tax rule. The lines written before the types existed were migrated (lodging, or tourist tax).</summary>
    public FiscalLineType LineType { get; set; } = FiscalLineType.Accommodation;

    [Required, MaxLength(DescriptionMaxLength)]
    public string Description { get; set; } = string.Empty;

    [Precision(10, 2)]
    public decimal Quantity { get; set; } = 1m;

    [Precision(18, 2)]
    public decimal UnitPrice { get; set; }

    [Precision(18, 2)]
    public decimal TotalPrice { get; set; }

    /// <summary>The VAT rate of the line in percent; 0 for an operation without VAT (then <see cref="VatNatureCode"/> says why).</summary>
    [Precision(5, 2)]
    public decimal VatRatePercent { get; set; }

    [MaxLength(VatNatureCodeMaxLength)]
    public string? VatNatureCode { get; set; }

    /// <summary>
    /// The VAT of the line as it was computed (<c>FiscalDocumentTotals</c>): the lines of one rate add up to the VAT of that rate, so the summaries of the
    /// PDF and of the XML are the stored amounts and never <c>taxable × rate</c> again. <c>null</c> for the lines written before it was stored.
    /// </summary>
    [Precision(18, 2)]
    public decimal? VatAmount { get; set; }
}

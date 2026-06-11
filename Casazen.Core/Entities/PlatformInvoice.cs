using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PlatformInvoices")]
public class PlatformInvoice
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(255)]
    public string StripeInvoiceId { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountExVat { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    [Required, MaxLength(32)]
    public string VatTreatment { get; set; } = string.Empty;

    public bool OssApplied { get; set; }

    [Required, MaxLength(32)]
    public string SdiStatus { get; set; } = "pending";

    [MaxLength(255)]
    public string? SdiTransmissionId { get; set; }

    [MaxLength(1000)]
    public string? FatturaPaXmlUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Org Org { get; set; } = null!;
}

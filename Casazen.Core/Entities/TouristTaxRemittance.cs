using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A payment of the tourist tax that a host made to a comune, with the receipt of the payment (NF-100, REP-05, CHK-08). The tax OWED is
/// stored nowhere else: it is the amount the tourist tax engine recorded on the stays (<c>Booking.TouristTaxAmount</c>, BK-03) and the
/// fiscal report adds up per comune. This row is the other half, what was PAID, so that the summary can set one against the other
/// (<c>ITouristTaxSummaryService</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>One row is one payment: the comune, the period it declares and pays (<see cref="PeriodFrom"/> to <see cref="PeriodTo"/>, both
/// included, Europe/Rome calendar dates), the amount to the cent, the day it was paid, the reference the host wrote and an optional
/// receipt in the private bucket. A comune can be paid in instalments, so several rows may share a comune and a period: there is no
/// unique index.</item>
/// <item><see cref="ComuneKey"/> is the comparable form of the comune name that the tourist tax report groups its stays by
/// (<c>TouristTaxComune.NormalizeName</c>: case, accents, apostrophes and spaces do not count), so a payment typed "forli" meets the
/// stays of "Forlì". <see cref="Comune"/> keeps what the host typed.</item>
/// <item>Nothing here is about a guest: no name, no booking. The receipt is the host's own file, read only through an authenticated
/// endpoint that checks the org; its address in the bucket is made by the server and never supplied by the client.</item>
/// <item>Tenant (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the org of the caller, set by the server. The foreign key to the org
/// is <c>RESTRICT</c>: a fiscal proof is not lost by deleting an org row.</item>
/// </list>
/// </remarks>
[Table("TouristTaxRemittances")]
public class TouristTaxRemittance : ITenantOwned
{
    public const int ComuneMaxLength = 200;

    public const int PaymentReferenceMaxLength = 200;

    public const int NotesMaxLength = 500;

    public const int ReceiptStorageKeyMaxLength = 500;

    public const int ReceiptFileNameMaxLength = 240;

    public const int ReceiptContentTypeMaxLength = 100;

    public const int RecordedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(ComuneMaxLength)]
    public string Comune { get; set; } = string.Empty;

    [Required, MaxLength(ComuneMaxLength)]
    public string ComuneKey { get; set; } = string.Empty;

    public DateOnly PeriodFrom { get; set; }

    public DateOnly PeriodTo { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    public DateOnly PaidOn { get; set; }

    [MaxLength(PaymentReferenceMaxLength)]
    public string? PaymentReference { get; set; }

    [MaxLength(NotesMaxLength)]
    public string? Notes { get; set; }

    [MaxLength(ReceiptStorageKeyMaxLength)]
    public string? ReceiptStorageKey { get; set; }

    [MaxLength(ReceiptFileNameMaxLength)]
    public string? ReceiptFileName { get; set; }

    [MaxLength(ReceiptContentTypeMaxLength)]
    public string? ReceiptContentType { get; set; }

    public long? ReceiptSizeBytes { get; set; }

    [Required, MaxLength(RecordedByUserIdMaxLength)]
    public string RecordedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

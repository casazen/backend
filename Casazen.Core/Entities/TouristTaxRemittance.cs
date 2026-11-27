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
    /// <summary>Column length of <see cref="Comune"/> and of <see cref="ComuneKey"/>.</summary>
    public const int ComuneMaxLength = 200;

    /// <summary>Column length of <see cref="PaymentReference"/>.</summary>
    public const int PaymentReferenceMaxLength = 200;

    /// <summary>Column length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 500;

    /// <summary>Column length of <see cref="ReceiptStorageKey"/>.</summary>
    public const int ReceiptStorageKeyMaxLength = 500;

    /// <summary>Column length of <see cref="ReceiptFileName"/>.</summary>
    public const int ReceiptFileNameMaxLength = 240;

    /// <summary>Column length of <see cref="ReceiptContentType"/>.</summary>
    public const int ReceiptContentTypeMaxLength = 100;

    /// <summary>Column length of <see cref="RecordedByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int RecordedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the org of the caller; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The comune as the host typed it (trimmed).</summary>
    [Required, MaxLength(ComuneMaxLength)]
    public string Comune { get; set; } = string.Empty;

    /// <summary>
    /// The comparable form of <see cref="Comune"/> (<c>TouristTaxComune.NormalizeName</c>), the key the tourist tax report groups by: a
    /// payment and the stays it pays meet on it.
    /// </summary>
    [Required, MaxLength(ComuneMaxLength)]
    public string ComuneKey { get; set; } = string.Empty;

    /// <summary>First day of the period the payment declares and pays (Europe/Rome calendar date).</summary>
    public DateOnly PeriodFrom { get; set; }

    /// <summary>Last day of that period, included.</summary>
    public DateOnly PeriodTo { get; set; }

    /// <summary>What was paid, in euros, to the cent. Always above zero.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    /// <summary>The day the payment was made (Europe/Rome calendar date), never after the day it was recorded.</summary>
    public DateOnly PaidOn { get; set; }

    /// <summary>The reference of the payment as the host wrote it (a bank transfer code, a receipt number); <c>null</c> when none.</summary>
    [MaxLength(PaymentReferenceMaxLength)]
    public string? PaymentReference { get; set; }

    /// <summary>A note of the host; <c>null</c> when none.</summary>
    [MaxLength(NotesMaxLength)]
    public string? Notes { get; set; }

    /// <summary>Key of the receipt in the private bucket (<c>tourist-tax/{orgId}/{id}/{file}</c>); <c>null</c> when the payment has none.</summary>
    [MaxLength(ReceiptStorageKeyMaxLength)]
    public string? ReceiptStorageKey { get; set; }

    /// <summary>The name the receipt is downloaded under (the host's file name, with the extension of what the file really is).</summary>
    [MaxLength(ReceiptFileNameMaxLength)]
    public string? ReceiptFileName { get; set; }

    /// <summary>What the receipt really is, read from its content: <c>application/pdf</c>, <c>image/jpeg</c> or <c>image/png</c>.</summary>
    [MaxLength(ReceiptContentTypeMaxLength)]
    public string? ReceiptContentType { get; set; }

    /// <summary>Size of the receipt in bytes.</summary>
    public long? ReceiptSizeBytes { get; set; }

    /// <summary>The user who recorded the payment (<c>User.Id</c>, the Auth0 subject).</summary>
    [Required, MaxLength(RecordedByUserIdMaxLength)]
    public string RecordedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

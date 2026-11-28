using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// One line of the log of what was sent to the authorities, when and by whom (E03/F02/US01/T01, NF-47, CHK-08, AC-NF47-2): a successful
/// send of the Alloggiati guests of a stay, of an ISTAT period or of a tourist tax period, by CasaZen, by the channel manager or by the
/// host by hand. A failed send writes no line. Append only.
/// </summary>
/// <remarks>
/// No guest data: the line names the property, the stay when there is one, the kind, the sender, the time and the person, and a
/// <see cref="Reference"/> that is a receipt or confirmation id, never a name, a document or an address.
/// </remarks>
[Table("ComplianceSendLogEntries")]
[Index(nameof(OrgId), nameof(PropertyId), nameof(SentAt))]
public class ComplianceSendLogEntry : ITenantOwned
{
    public const int ReferenceMaxLength = 100;

    public const int UserIdMaxLength = 255;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>The stay the send was about; <c>null</c> for the period flows (ISTAT, tourist tax), which have none.</summary>
    public Guid? BookingId { get; set; }

    public ComplianceSendKind Kind { get; set; }

    public ComplianceSender Sender { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? ByUserId { get; set; }

    [MaxLength(ReferenceMaxLength)]
    public string? Reference { get; set; }
}

public enum ComplianceSendKind
{
    AlloggiatiGuests = 1,

    IstatPeriod = 2,

    TouristTaxPeriod = 3,

    /// <summary>
    /// The documents of the guests of a stay (the photos of identity documents and the legacy scan) were deleted because the stay is covered by an archived Alloggiati receipt
    /// (E20/F02/US01/T02, NF-121, decision O4): not a send but the line of the stay that says what CasaZen did after the receipt, written by CasaZen with the date and no
    /// reference. The document data of the guests are not deleted by it.
    /// </summary>
    DocumentsDeleted = 4,
}

public enum ComplianceSender
{
    CasaZen = 1,

    ChannelManager = 2,

    Manual = 3,
}

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
    /// <summary>Longest <see cref="Reference"/>.</summary>
    public const int ReferenceMaxLength = 100;

    /// <summary>Longest <see cref="ByUserId"/> (the one of <c>User.Id</c>).</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>The stay the send was about; <c>null</c> for the period flows (ISTAT, tourist tax), which have none.</summary>
    public Guid? BookingId { get; set; }

    public ComplianceSendKind Kind { get; set; }

    public ComplianceSender Sender { get; set; }

    /// <summary>When the send happened (UTC).</summary>
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary>The person who sent it (<c>User.Id</c>); <c>null</c> for a job or for the channel manager's own run.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? ByUserId { get; set; }

    /// <summary>A receipt or confirmation id of the send; <c>null</c> when there is none. Never guest data.</summary>
    [MaxLength(ReferenceMaxLength)]
    public string? Reference { get; set; }
}

/// <summary>What was sent (stored as an integer, never renumbered).</summary>
public enum ComplianceSendKind
{
    /// <summary>The Alloggiati Web communication of the guests of a stay.</summary>
    AlloggiatiGuests = 1,

    /// <summary>The ISTAT communication of a period.</summary>
    IstatPeriod = 2,

    /// <summary>The tourist tax declaration of a period.</summary>
    TouristTaxPeriod = 3,
}

/// <summary>
/// Who sent it, as the log keeps it (stored as an integer, never renumbered). Its own numbers: it is not the sender chosen for a property
/// (<see cref="Enums.ComplianceSenderType"/>, whose numbers differ), and whoever writes a line maps one to the other by name.
/// </summary>
public enum ComplianceSender
{
    /// <summary>CasaZen transmitted it with the Questura account of the property.</summary>
    CasaZen = 1,

    /// <summary>The channel manager (Octorate) sent it.</summary>
    ChannelManager = 2,

    /// <summary>The host sent it by hand and declared it.</summary>
    Manual = 3,
}

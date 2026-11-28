using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The copy in CasaZen of one conversation of a guest with a channel (Airbnb, Booking.com, Vrbo, Expedia), read from the channel manager
/// (E05/F01/US01/T01, ADR-032, PO decision Q-A30): one row per Octorate thread, kept apart from the direct conversations
/// (<see cref="GuestThread"/>); the hub of messages shows the two together through one read model.
/// </summary>
/// <remarks>
/// <para><b>Unassigned.</b> <see cref="PropertyId"/> is <c>null</c> while the listing the thread belongs to has no confirmed mapping to a
/// property (OCT-04): the thread then belongs to the organization and to no property, and only a caller whose reach covers every property
/// sees it. It is never guessed and never given to a property of another organization; the sync gives it its property at the first pass
/// after a person confirms the mapping, and takes it away again if the mapping stops being confirmed.</para>
/// <para><b>Personal data.</b> <see cref="GuestDisplayName"/> and the text and names of the messages are personal data: never logged, the
/// text encrypted at rest, and anonymized with the guest (<see cref="AnonymizedAt"/>, ADR-032-R13).</para>
/// </remarks>
[Table("OtaMessageThreads")]
public class OtaMessageThread : ITenantOwned
{
    public const string OrgThreadIndexName = "UIX_OtaMessageThreads_OrgId_OctorateThreadId";

    public const int OctorateIdMaxLength = 64;

    public const int OctorateReservationIdMaxLength = Booking.OctorateReservationIdMaxLength;

    public const int GuestDisplayNameMaxLength = 200;

    public const int DefaultProcessorMaxLength = 32;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public virtual Property? Property { get; set; }

    public Guid? BookingId { get; set; }

    public virtual Booking? Booking { get; set; }

    [Required]
    [MaxLength(OctorateIdMaxLength)]
    public string OctorateThreadId { get; set; } = string.Empty;

    [Required]
    [MaxLength(OctorateIdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    [MaxLength(OctorateReservationIdMaxLength)]
    public string? OctorateReservationId { get; set; }

    public OtaChannel ChannelSource { get; set; }

    public OtaThreadKind Kind { get; set; }

    [MaxLength(GuestDisplayNameMaxLength)]
    public string? GuestDisplayName { get; set; }

    public DateTime LastMessageAt { get; set; }

    public OtaMessageDirection? LastMessageDirection { get; set; }

    public int UnreadCount { get; set; }

    public bool IsArchived { get; set; }

    [MaxLength(DefaultProcessorMaxLength)]
    public string? DefaultProcessor { get; set; }

    public bool? AiAssistantOn { get; set; }

    public DateTime? AnonymizedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

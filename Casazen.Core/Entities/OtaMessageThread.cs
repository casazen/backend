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
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="OctorateThreadId"/>).</summary>
    public const string OrgThreadIndexName = "UIX_OtaMessageThreads_OrgId_OctorateThreadId";

    /// <summary>Longest <see cref="OctorateThreadId"/> and <see cref="OctorateAccommodationId"/>.</summary>
    public const int OctorateIdMaxLength = 64;

    /// <summary>Longest <see cref="OctorateReservationId"/>: the one of <see cref="Booking.OctorateReservationId"/>.</summary>
    public const int OctorateReservationIdMaxLength = Booking.OctorateReservationIdMaxLength;

    public const int GuestDisplayNameMaxLength = 200;

    public const int DefaultProcessorMaxLength = 32;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the organization whose connection the thread was read through. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The property of the thread: the one the confirmed mapping of <see cref="OctorateAccommodationId"/> gives. <c>null</c> for an unassigned
    /// thread (see the remarks of the class). FK, SET NULL.
    /// </summary>
    public Guid? PropertyId { get; set; }

    public virtual Property? Property { get; set; }

    /// <summary>The booking of the reservation the thread is about, <c>null</c> for an inquiry or while CasaZen does not hold the booking yet. FK, SET NULL.</summary>
    public Guid? BookingId { get; set; }

    public virtual Booking? Booking { get; set; }

    /// <summary>The thread on Octorate (a number, kept as text). Unique in the organization.</summary>
    [Required]
    [MaxLength(OctorateIdMaxLength)]
    public string OctorateThreadId { get; set; } = string.Empty;

    /// <summary>The accommodation the Octorate chat is scoped to.</summary>
    [Required]
    [MaxLength(OctorateIdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    /// <summary>
    /// The reservation Octorate links the thread to, as it gave it. How a thread whose booking CasaZen imports later is linked without a call
    /// to Octorate (<see cref="BookingId"/>); <c>null</c> for an inquiry.
    /// </summary>
    [MaxLength(OctorateReservationIdMaxLength)]
    public string? OctorateReservationId { get; set; }

    public OtaChannel ChannelSource { get; set; }

    public OtaThreadKind Kind { get; set; }

    /// <summary>The name of the guest as the channel gives it. Personal data; <c>null</c> when the thread does not say.</summary>
    [MaxLength(GuestDisplayNameMaxLength)]
    public string? GuestDisplayName { get; set; }

    /// <summary>When the newest message of the thread was sent (UTC).</summary>
    public DateTime LastMessageAt { get; set; }

    /// <summary>Who wrote the newest message; <c>null</c> while the thread holds none.</summary>
    public OtaMessageDirection? LastMessageDirection { get; set; }

    /// <summary>The inbound messages nobody read yet (<see cref="OtaMessage.ReadAt"/> is <c>null</c>).</summary>
    public int UnreadCount { get; set; }

    /// <summary>The thread is archived on the channel manager (<c>archiveTime</c>).</summary>
    public bool IsArchived { get; set; }

    /// <summary>The default processor of the thread on Octorate (<c>DIRECT</c>, <c>CHANNEL</c>, ...), as it spells it.</summary>
    [MaxLength(DefaultProcessorMaxLength)]
    public string? DefaultProcessor { get; set; }

    /// <summary>
    /// What Octorate reports for its own assistant on this thread (<c>aiDriven</c>): <c>true</c> on, <c>false</c> off, <c>null</c> the thread
    /// does not say. The guard of E05/F05 verifies it is off (OCT-11).
    /// </summary>
    public bool? AiAssistantOn { get; set; }

    /// <summary>
    /// When the words and the names of the thread were anonymized with the guest (ADR-032-R13); <c>null</c> until then. A thread with a value
    /// here gets no personal data from a later read of the channel manager.
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

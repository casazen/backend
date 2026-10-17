using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The conversation between the guest of a direct booking and the host (DB-14a): one per booking (<see cref="BookingId"/> is unique),
/// created by the first message of either side. It is open from the confirmation of the booking until the end of the seventh day after
/// the check-out (<c>GuestMessageRules.ClosesAtUtc</c>, Europe/Rome); after that it is read-only. Nothing about the openness is stored:
/// it follows the booking, so a booking that moves its dates moves the end of the thread.
/// </summary>
/// <remarks>
/// <para><b>Personal data.</b> The text of the messages is free text and can hold personal data. It is never written to a log, never
/// put in an email to the host, and goes away with the guest when the guest is erased or anonymized (<c>GuestDataEraser</c>).</para>
/// <para>The columns below the ids are kept up to date by one atomic <c>UPDATE</c> per message (never read and written back by the
/// request), so two messages at the same moment cannot undo each other. They answer the questions of the host's screens without
/// reading the messages: whether the guest is waiting for an answer, how many messages the host has not read, whether an email to
/// the other side is due.</para>
/// </remarks>
[Table("GuestThreads")]
[Index(nameof(BookingId), IsUnique = true, Name = "UIX_GuestThreads_BookingId")]
[Index(nameof(OrgId), nameof(LastMessageAt), Name = "IX_GuestThreads_OrgId_LastMessageAt")]
public class GuestThread : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the booking (TN-2). Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property of the booking: the host's reach on properties (<c>InScope</c>) applies to it.</summary>
    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>When the last message of either side was written (UTC).</summary>
    public DateTime LastMessageAt { get; set; }

    /// <summary>When the guest wrote last; <c>null</c> if the guest never wrote.</summary>
    public DateTime? LastGuestMessageAt { get; set; }

    /// <summary>When the host wrote last; <c>null</c> if the host never wrote.</summary>
    public DateTime? LastHostMessageAt { get; set; }

    /// <summary>
    /// The host has read the messages of the guest written up to this instant (the <see cref="GuestMessage.CreatedAt"/> of the last
    /// one it read); the ones after it are the unread ones. <c>null</c> until the host reads or answers.
    /// </summary>
    public DateTime? HostReadAt { get; set; }

    /// <summary>When the last email to the host about a message of the guest was queued: at most one every ten minutes per thread.</summary>
    public DateTime? HostNotifiedAt { get; set; }

    /// <summary>When the last email to the guest about an answer of the host was queued: at most one every ten minutes per thread.</summary>
    public DateTime? GuestNotifiedAt { get; set; }
}

/// <summary>
/// A message of a <see cref="GuestThread"/> (DB-14a). Plain text, up to <c>GuestMessageRules.TextMaxLength</c> characters; the text of the
/// guest is stored with card numbers and IBANs already replaced by «[rimosso]». Messages are never edited or deleted by a user.
/// </summary>
[Table("GuestMessages")]
[Index(nameof(ThreadId), nameof(CreatedAt), Name = "IX_GuestMessages_ThreadId_CreatedAt")]
[Index(nameof(ThreadId), nameof(IdempotencyKey), IsUnique = true, Name = "UIX_GuestMessages_ThreadId_IdempotencyKey")]
[Index(nameof(OrgId), Name = "IX_GuestMessages_OrgId")]
public class GuestMessage : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Thread))]
    public Guid ThreadId { get; set; }
    public virtual GuestThread Thread { get; set; } = null!;

    public GuestMessageSender Sender { get; set; }

    /// <summary>What was written, as stored (normalized; redacted for the guest). Never logged.</summary>
    [Required, MaxLength(2000)]
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The key the sender's screen made for this message: the same key twice in a thread is the same message (a retry of a send that
    /// did not seem to arrive), so a bad network cannot write it twice. The unique index lets nulls through.
    /// </summary>
    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }

    /// <summary>The user who wrote it, for a message of the host (<c>User.Id</c>), an opaque id with no foreign key; <c>null</c> for the guest.</summary>
    [MaxLength(255)]
    public string? AuthorUserId { get; set; }
}

/// <summary>Who wrote a <see cref="GuestMessage"/>. Stored as an integer.</summary>
public enum GuestMessageSender
{
    Guest = 0,
    Host = 1,
}

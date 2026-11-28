using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
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

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime LastMessageAt { get; set; }

    public DateTime? LastGuestMessageAt { get; set; }

    public DateTime? LastHostMessageAt { get; set; }

    public DateTime? HostReadAt { get; set; }

    public DateTime? HostNotifiedAt { get; set; }

    public DateTime? GuestNotifiedAt { get; set; }
}

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

    [Required, MaxLength(2000)]
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The key the sender's screen made for this message: the same key twice in a thread is the same message (a retry of a send that
    /// did not seem to arrive), so a bad network cannot write it twice. The unique index lets nulls through.
    /// </summary>
    [MaxLength(64)]
    public string? IdempotencyKey { get; set; }

    [MaxLength(255)]
    public string? AuthorUserId { get; set; }

    public const int RuleCodeMaxLength = 64;

    public MessageOrigin Origin { get; set; }

    [MaxLength(RuleCodeMaxLength)]
    public string? RuleCode { get; set; }
}

public enum GuestMessageSender
{
    Guest = 0,
    Host = 1,
}

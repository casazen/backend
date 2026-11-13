using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A stay of the owner in its own property (E07/F07/US01/T01, NF-111). It is <b>not a guest booking</b>: no guest, no price, no message to a guest, no
/// invoice and no tourist tax. It starts as a request; once approved it holds one manual calendar block (<see cref="CalendarBlockId"/>, reason
/// <c>Owner</c>, created through <c>ICalendarBlockService</c>, the path every block of CasaZen takes towards the channel manager), and a withdrawal or a
/// cancellation gives back that block and nothing else.
/// </summary>
/// <remarks>
/// <para><b>Dates.</b> <see cref="StartDate"/> is the arrival (the first night), <see cref="EndDate"/> the departure (not a night), Europe/Rome dates, as a
/// booking and a block count them.</para>
/// <para><b>Sync.</b> <see cref="SyncState"/> and <see cref="ReleaseSyncState"/> are the last states the reconciliation recorded (with a line of the
/// activity log at every change); the reads derive the live state from the calendar change itself, so a stay is never shown confirmed before the
/// channel manager accepted its block.</para>
/// </remarks>
[Table("OwnerStayRequests")]
public class OwnerStayRequest : ITenantOwned
{
    /// <summary>The longest id of a person of the agency (<c>User.Id</c>) or of an actor written as <c>owner:{id}</c>.</summary>
    public const int ActorMaxLength = 255;

    /// <summary>The longest <see cref="SyncErrorCode"/>: the column length of the codes of a calendar change.</summary>
    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the property. Server-set.</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The owner profile of the agency the stay is for (<c>PropertyOwner.Id</c>), also when a delegated contact asked for it.</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>The person of the portal who asked (<c>OwnerUser.Id</c>): the owner, or a contact it delegated.</summary>
    public Guid RequestedByOwnerUserId { get; set; }

    /// <summary>The delegation the request was made through (<c>OwnerPortalContact.Id</c>); <c>null</c> when the owner asked.</summary>
    public Guid? RequestedByContactId { get; set; }

    /// <summary>The arrival: the first night.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>The departure: the day after the last night.</summary>
    public DateOnly EndDate { get; set; }

    public int Nights { get; set; }

    public OwnerStayStatus Status { get; set; } = OwnerStayStatus.Requested;

    /// <summary>The rule of the property did not ask for a person: CasaZen approves the request in the background once it still obeys the rules.</summary>
    public bool AutoApprove { get; set; }

    /// <summary>The block of the approved stay (<c>CalendarBlock.Id</c>, the same value as <see cref="Id"/>): kept after the release, for the history.</summary>
    public Guid? CalendarBlockId { get; set; }

    /// <summary>The calendar change that sent the block to the channel manager; <c>null</c> when nothing records the changes.</summary>
    public Guid? BlockChangeProcessId { get; set; }

    /// <summary>The last state of the block the reconciliation recorded.</summary>
    public OwnerStaySyncState SyncState { get; set; } = OwnerStaySyncState.None;

    /// <summary>When the block was removed (the stay was withdrawn or cancelled after its approval); <c>null</c> while it holds the dates.</summary>
    public DateTime? BlockReleasedAt { get; set; }

    /// <summary>The calendar change that opened the nights again on the channel manager.</summary>
    public Guid? ReleaseChangeProcessId { get; set; }

    /// <summary>The last state of the release the reconciliation recorded.</summary>
    public OwnerStaySyncState ReleaseSyncState { get; set; } = OwnerStaySyncState.None;

    /// <summary>The stable code of the last failure of the block or of its release (a code of the calendar change, never a text of the provider).</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? SyncErrorCode { get; set; }

    /// <summary>When the reconciliation last compared the stay with its calendar change.</summary>
    public DateTime? SyncCheckedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>When the agency (or the automatic approval) decided.</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>Who decided: <c>User.Id</c> of the agency; <c>null</c> for the automatic approval.</summary>
    [MaxLength(ActorMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime? CancelledAt { get; set; }

    /// <summary>Who withdrew or cancelled: <c>User.Id</c> of the agency, or <c>owner:{OwnerUser.Id}</c>.</summary>
    [MaxLength(ActorMaxLength)]
    public string? CancelledBy { get; set; }
}

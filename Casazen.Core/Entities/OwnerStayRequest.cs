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
    public const int ActorMaxLength = 255;

    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public Guid PropertyOwnerId { get; set; }

    public Guid RequestedByOwnerUserId { get; set; }

    public Guid? RequestedByContactId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public int Nights { get; set; }

    public OwnerStayStatus Status { get; set; } = OwnerStayStatus.Requested;

    public bool AutoApprove { get; set; }

    public Guid? CalendarBlockId { get; set; }

    public Guid? BlockChangeProcessId { get; set; }

    public OwnerStaySyncState SyncState { get; set; } = OwnerStaySyncState.None;

    public DateTime? BlockReleasedAt { get; set; }

    public Guid? ReleaseChangeProcessId { get; set; }

    public OwnerStaySyncState ReleaseSyncState { get; set; } = OwnerStaySyncState.None;

    [MaxLength(ErrorCodeMaxLength)]
    public string? SyncErrorCode { get; set; }

    public DateTime? SyncCheckedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    [MaxLength(ActorMaxLength)]
    public string? DecidedByUserId { get; set; }

    public DateTime? CancelledAt { get; set; }

    [MaxLength(ActorMaxLength)]
    public string? CancelledBy { get; set; }
}

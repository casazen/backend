using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One change of the calendar that a host asked for (E08/F05/US01/T01, NF-36): what was asked, how many properties and days it touches, and
/// where it stands towards the channel manager. The process is the thing the host follows («saved», «sending», «sent», «confirmed»); the
/// work itself is its <see cref="CalendarChangeChunk"/> rows.
/// </summary>
/// <remarks>
/// <para><b>No personal data.</b> <see cref="SummaryJson"/> keeps what was asked as fields, actions and values (the property ids, the dates, the
/// price or the rule written), never a name, an address or a guest. <see cref="ErrorCode"/> is one of CasaZen's stable codes, never a text of the
/// provider.</para>
/// <para><b>State.</b> <see cref="State"/> is derived from the chunks by whoever sends them; <see cref="CalendarChangeState.Sent"/> means the
/// channel manager accepted every chunk, not that a portal shows the new price. <see cref="NextAttemptAt"/> is when the job tries the process again
/// (the index on (<see cref="State"/>, <see cref="NextAttemptAt"/>) is the job's queue).</para>
/// </remarks>
[Table("CalendarChangeProcesses")]
public class CalendarChangeProcess : ITenantOwned
{
    public const int UserIdMaxLength = 255;

    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public CalendarChangeKind Kind { get; set; } = CalendarChangeKind.DayRules;

    public CalendarChangeState State { get; set; } = CalendarChangeState.Applied;

    [MaxLength(UserIdMaxLength)]
    public string? RequestedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public int PropertyCount { get; set; }

    public int DayCount { get; set; }

    public string SummaryJson { get; set; } = "{}";

    public int QuotaUnitsEstimated { get; set; }

    /// <summary>Why the process is not whole: a stable code of CasaZen, never a text of the provider; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptAt { get; set; }
}

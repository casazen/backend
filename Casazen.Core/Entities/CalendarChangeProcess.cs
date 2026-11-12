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
    /// <summary>Longest <see cref="RequestedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Longest <see cref="ErrorCode"/> (varchar 64).</summary>
    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the properties changed (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public CalendarChangeKind Kind { get; set; } = CalendarChangeKind.DayRules;

    public CalendarChangeState State { get; set; } = CalendarChangeState.Applied;

    /// <summary>Who asked for the change: the Auth0 subject of the account; <c>null</c> for a change of the system.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? RequestedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the first chunk was sent (UTC); <c>null</c> until then.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the last chunk was settled (UTC); <c>null</c> while some chunk is still to send or to confirm.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>How many properties the change touches.</summary>
    public int PropertyCount { get; set; }

    /// <summary>How many calendar days (of all the properties together) the change touches.</summary>
    public int DayCount { get; set; }

    /// <summary>What was asked, as fields, actions and values (jsonb). No personal data.</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>The calls to the channel manager the change is expected to use, an estimate made before it was sent.</summary>
    public int QuotaUnitsEstimated { get; set; }

    /// <summary>Why the process is not whole: a stable code of CasaZen, never a text of the provider; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>How many times the job tried to send the process.</summary>
    public int Attempts { get; set; }

    /// <summary>When the job tries again (UTC); <c>null</c> when nothing is waiting.</summary>
    public DateTime? NextAttemptAt { get; set; }
}

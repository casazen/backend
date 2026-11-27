using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One range of dates of one property that a change of the calendar sends to the channel manager in a single call (E08/F05/US01/T01, NF-36). It
/// keeps the id of the process the channel manager runs for the call, so the host can follow the change until the channel manager confirms it.
/// </summary>
/// <remarks>No personal data: ids, dates, a state, counters and a stable error code of CasaZen (never a text of the provider).</remarks>
[Table("CalendarChangeChunks")]
public class CalendarChangeChunk : ITenantOwned
{
    /// <summary>Longest <see cref="OctorateProcessId"/> (varchar 100).</summary>
    public const int OctorateProcessIdMaxLength = 100;

    /// <summary>Longest <see cref="ErrorCode"/> (varchar 64).</summary>
    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the process (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The change this chunk belongs to (FK, CASCADE: the chunks go with it).</summary>
    public Guid ProcessId { get; set; }

    /// <summary>The property whose dates the chunk sends (FK, CASCADE).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>First calendar day of the range, included.</summary>
    public DateOnly FromDate { get; set; }

    /// <summary>Last calendar day of the range, included.</summary>
    public DateOnly ToDate { get; set; }

    public CalendarChunkState State { get; set; } = CalendarChunkState.Pending;

    /// <summary>The id the channel manager returns for the call (the bulk process it runs); <c>null</c> until it accepted the call.</summary>
    [MaxLength(OctorateProcessIdMaxLength)]
    public string? OctorateProcessId { get; set; }

    /// <summary>How many times the chunk was sent.</summary>
    public int Attempts { get; set; }

    /// <summary>When the chunk was last sent (UTC); <c>null</c> until it was.</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Why the chunk failed: a stable code of CasaZen, never a text of the provider; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

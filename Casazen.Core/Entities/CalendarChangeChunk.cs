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
    public const int OctorateProcessIdMaxLength = 100;

    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid ProcessId { get; set; }

    public Guid PropertyId { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public CalendarChunkState State { get; set; } = CalendarChunkState.Pending;

    [MaxLength(OctorateProcessIdMaxLength)]
    public string? OctorateProcessId { get; set; }

    public int Attempts { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Why the chunk failed: a stable code of CasaZen, never a text of the provider; <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

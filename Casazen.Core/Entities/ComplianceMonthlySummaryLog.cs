using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The monthly compliance summary email already sent to an org for a month (E03/F02/US01/T01, NF-52, CHK-12 point 6): one row per org and
/// month, so the email is idempotent. The unique index <see cref="OrgMonthIndexName"/> makes a second run, a retry or a concurrent run
/// send nothing more: the job inserts the row in the same transaction as the enqueue and the loser of the race finds it there.
/// </summary>
/// <remarks>No personal data: the month, the outcome and how many people the email went to.</remarks>
[Table("ComplianceMonthlySummaryLogs")]
public class ComplianceMonthlySummaryLog : ITenantOwned
{
    /// <summary>Length of <see cref="YearMonth"/> (<c>yyyy-MM</c>).</summary>
    public const int YearMonthLength = 7;

    /// <summary>Longest <see cref="Outcome"/>.</summary>
    public const int OutcomeMaxLength = 20;

    /// <summary>The <see cref="Outcome"/> of a month in which every applicable item resolved to green.</summary>
    public const string AllCompliant = "AllCompliant";

    /// <summary>The <see cref="Outcome"/> of a month with at least one item that is not green.</summary>
    public const string ItemsMissing = "ItemsMissing";

    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="YearMonth"/>).</summary>
    public const string OrgMonthIndexName = "UIX_ComplianceMonthlySummaryLogs_OrgId_YearMonth";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org the summary was sent to (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The month the summary is about, <c>yyyy-MM</c> (Europe/Rome).</summary>
    [Required, MaxLength(YearMonthLength)]
    public string YearMonth { get; set; } = string.Empty;

    /// <summary>When the summary was handed to the email queue (UTC).</summary>
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary><see cref="AllCompliant"/> or <see cref="ItemsMissing"/>.</summary>
    [Required, MaxLength(OutcomeMaxLength)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>How many addresses the summary went to.</summary>
    public int RecipientCount { get; set; }
}

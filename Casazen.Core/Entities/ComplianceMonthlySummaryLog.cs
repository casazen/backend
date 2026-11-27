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
    public const int YearMonthLength = 7;

    public const int OutcomeMaxLength = 20;

    public const string AllCompliant = "AllCompliant";

    public const string ItemsMissing = "ItemsMissing";

    public const string OrgMonthIndexName = "UIX_ComplianceMonthlySummaryLogs_OrgId_YearMonth";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(YearMonthLength)]
    public string YearMonth { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(OutcomeMaxLength)]
    public string Outcome { get; set; } = string.Empty;

    public int RecipientCount { get; set; }
}

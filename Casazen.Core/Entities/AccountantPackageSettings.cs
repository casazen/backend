using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// How the package of the accountant is sent to the accountant of an organization (E15/F01/US02/T01, NF-101, D11, decision of 2026-11-14): who receives it, whether it is sent every month, on
/// which day, with which folders, and what the last send ended in. One row for each organization (unique <see cref="OrgId"/>), written by the organization and by the monthly job
/// (E15/F01/US02/T02), never by the accountant, who only receives.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Privacy.</b> <see cref="AccountantEmail"/> is the address of a third party: it is encrypted at rest (<c>EncryptedColumns</c>, a purpose of its own, the column has no length: the length is checked on
/// input), read by the settings of the organization and used by the send, never searched and never written to a log. The row holds no name and no text of the accountant.</item>
/// <item><b>The month is sent once.</b> <see cref="LastSendPeriod"/> with <see cref="LastSendResult"/> <see cref="AccountantPackageSendResult.Sent"/> says which month the accountant already has: the job sends
/// a month only when it is not that one, and records the send in the same step that queues the e-mail, under the lock of the organization. A failed attempt never replaces a send that went out.</item>
/// <item><b>The package that waits for its link.</b> The package of a send is kept in the private storage until its link expires (<see cref="StoredPackagePeriod"/>, <see cref="StoredPackageAt"/>): the row says
/// which one it is, so the nightly run deletes it when it is old and a newer one replaces it, and nothing stays in the bucket without a row that names it.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the organization the row is of, and every read and write names it.</item>
/// </list>
/// </remarks>
[Table("AccountantPackageSettings")]
public class AccountantPackageSettings : ITenantOwned
{
    public const string OrgIndexName = "UIX_AccountantPackageSettings_OrgId";

    public const int EmailMaxLength = 254;

    public const int PeriodLength = 7;

    public const int SendErrorMaxLength = 60;

    public const int UpdatedByUserIdMaxLength = 255;

    public const int MinSendDay = 1;

    public const int MaxSendDay = 28;

    public const int DefaultSendDay = 3;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public string? AccountantEmail { get; set; }

    public bool MonthlySendEnabled { get; set; }

    public int SendDayOfMonth { get; set; } = DefaultSendDay;

    public AccountantPackageFolders Contents { get; set; } = AccountantPackageFolders.All;

    [MaxLength(PeriodLength)]
    public string? LastSendPeriod { get; set; }

    public DateTime? LastSendAt { get; set; }

    public AccountantPackageSendResult? LastSendResult { get; set; }

    [MaxLength(SendErrorMaxLength)]
    public string? LastSendError { get; set; }

    [MaxLength(PeriodLength)]
    public string? StoredPackagePeriod { get; set; }

    public DateTime? StoredPackageAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}

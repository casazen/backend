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
    /// <summary>Name of the unique index on <see cref="OrgId"/>: one row for each organization.</summary>
    public const string OrgIndexName = "UIX_AccountantPackageSettings_OrgId";

    /// <summary>The longest address accepted (RFC 5321): the column is encrypted and has no length, so it is checked on input.</summary>
    public const int EmailMaxLength = 254;

    /// <summary>Length of a period of the sends: <c>yyyy-MM</c>.</summary>
    public const int PeriodLength = 7;

    /// <summary>Longest code of <see cref="LastSendError"/>.</summary>
    public const int SendErrorMaxLength = 60;

    public const int UpdatedByUserIdMaxLength = 255;

    /// <summary>The first day of the month the package can be sent on.</summary>
    public const int MinSendDay = 1;

    /// <summary>The last day of the month the package can be sent on: 28, so every month has the day.</summary>
    public const int MaxSendDay = 28;

    /// <summary>The day of the month the package is sent on when the organization does not choose one.</summary>
    public const int DefaultSendDay = 3;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key (TN-2): the organization the settings are of. Unique.</summary>
    public Guid OrgId { get; set; }

    /// <summary>
    /// The address the package is sent to (lower case, trimmed), encrypted at rest; <c>null</c> when the organization has none. Never <c>""</c>: an empty address is stored as <c>null</c>, so a query that asks
    /// for a stored address asks for <c>IS NOT NULL</c> and never compares an encrypted column with a text.
    /// </summary>
    public string? AccountantEmail { get; set; }

    /// <summary>True when the package of the previous month is sent every month, from <see cref="SendDayOfMonth"/>. Off by default: nothing is sent to a third party until the organization says so.</summary>
    public bool MonthlySendEnabled { get; set; }

    /// <summary>
    /// The day of the month the send is due, <see cref="MinSendDay"/> to <see cref="MaxSendDay"/> (Europe/Rome). The job sends the previous month on the first run from that day on, until it is sent: a run that failed
    /// or a service that was down on the day does not lose the month.
    /// </summary>
    public int SendDayOfMonth { get; set; } = DefaultSendDay;

    /// <summary>The folders of the package that is sent (<see cref="AccountantPackageFolders.All"/> by default; never empty). What the screens download does not depend on it.</summary>
    public AccountantPackageFolders Contents { get; set; } = AccountantPackageFolders.All;

    /// <summary>The month (<c>yyyy-MM</c>) of the last send attempt; <c>null</c> before the first.</summary>
    [MaxLength(PeriodLength)]
    public string? LastSendPeriod { get; set; }

    /// <summary>When the last attempt ended (UTC); <c>null</c> before the first.</summary>
    public DateTime? LastSendAt { get; set; }

    /// <summary>What the last attempt ended in; <c>null</c> before the first.</summary>
    public AccountantPackageSendResult? LastSendResult { get; set; }

    /// <summary>The stable code of why the last attempt failed (<c>package_too_large</c>, <c>link_not_configured</c>, <c>email_not_queued</c>...): never a message, an address or a name. <c>null</c> after a send.</summary>
    [MaxLength(SendErrorMaxLength)]
    public string? LastSendError { get; set; }

    /// <summary>The month (<c>yyyy-MM</c>) of the package that waits in the private storage for the link of its e-mail; <c>null</c> when none does.</summary>
    [MaxLength(PeriodLength)]
    public string? StoredPackagePeriod { get; set; }

    /// <summary>When that package was stored (UTC); the nightly run deletes it once its link has expired.</summary>
    public DateTime? StoredPackageAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the settings were last saved by a person (UTC); a send does not change it.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who saved the settings last (an id, never a name).</summary>
    [MaxLength(UpdatedByUserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}

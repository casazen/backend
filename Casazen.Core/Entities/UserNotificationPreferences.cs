using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// How one user wants to be alerted (ADR-025, NF-08, NF-22): the quiet hours during which the popup alerts that are not legal wait for
/// the morning. One row per user and org, created with the defaults the first time an alert is routed to the user. The per-event settings
/// of the notifications (E05/F03) are added next to these fields, joined by <c>(OrgId, UserId)</c>.
/// </summary>
/// <remarks>
/// <para><b>Quiet hours</b> are wall-clock times of Europe/Rome (<c>RomeCalendar</c>): 22:00 stays 22:00 all year, also on the two days the
/// clock changes. A window that ends before it starts (22:00-08:00) runs over midnight; start equal to end is an empty window (no quiet
/// hours).</para>
/// <para><b>Tenant</b> (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the host org of the user. The row goes with the user and with
/// the org (foreign keys <c>ON DELETE CASCADE</c>).</para>
/// </remarks>
[Table("UserNotificationPreferences")]
public class UserNotificationPreferences : ITenantOwned
{
    /// <summary>The quiet hours start at 22:00 (Europe/Rome) unless the user chose otherwise.</summary>
    public static readonly TimeOnly DefaultQuietHoursStart = new(22, 0);

    /// <summary>The quiet hours end at 08:00 (Europe/Rome) unless the user chose otherwise.</summary>
    public static readonly TimeOnly DefaultQuietHoursEnd = new(8, 0);

    public const string OncePerUserIndexName = "UIX_UserNotificationPreferences_OrgId_UserId";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(PopupAlert.UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    public TimeOnly QuietHoursStart { get; set; } = DefaultQuietHoursStart;

    public TimeOnly QuietHoursEnd { get; set; } = DefaultQuietHoursEnd;

    /// <summary>
    /// A legal deadline alert (Alloggiati, ISTAT) overrides the quiet hours. Default <c>true</c>. The router lets the legal alerts
    /// through whatever this says (ADR-025-R3: "legal alerts ALWAYS bypass quiet hours"); the field is the place a future setting
    /// of the notifications page reads and writes.
    /// </summary>
    public bool LegalAlertsOverrideQuietHours { get; set; } = true;
}

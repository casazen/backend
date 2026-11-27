using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// What a user chose for one kind of event (E05/F03, NF-65): through which channels it wants to be alerted. One row per user and
/// event type, written by <c>PUT api/me/notification-preferences</c>; the quiet hours and the legal-override flag stay in
/// <see cref="UserNotificationPreferences"/>, joined by <c>(OrgId, UserId)</c>.
/// </summary>
/// <remarks>
/// <para><b>No row means the default.</b> The defaults of PO-10 are computed in code (<c>NotificationPreferenceRules</c>), not stored, so a
/// user who never opens the settings costs no rows and a default changed later applies to everybody who did not choose.</para>
/// <para><b>Tenant</b> (<see cref="ITenantOwned"/>): <see cref="OrgId"/> is the host org of the user. The row goes with the user and with
/// the org (foreign keys <c>ON DELETE CASCADE</c>). It holds a user id, an event type and a set of channels: no personal data beyond the id.</para>
/// </remarks>
[Table("UserNotificationChannelSettings")]
public class UserNotificationChannelSetting : ITenantOwned
{
    public const int EventTypeMaxLength = 64;

    public const string OncePerUserAndEventIndexName = "UIX_UserNotificationChannelSettings_OrgId_UserId_EventType";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(PopupAlert.UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    public NotificationEventType EventType { get; set; }

    public NotificationChannels Channels { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

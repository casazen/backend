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
    /// <summary>Column length of <see cref="EventType"/> (the member name).</summary>
    public const int EventTypeMaxLength = 64;

    /// <summary>Name of the unique index on <c>(OrgId, UserId, EventType)</c>: a 23505 on it means the row already exists.</summary>
    public const string OncePerUserAndEventIndexName = "UIX_UserNotificationChannelSettings_OrgId_UserId_EventType";

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the host org of the user. Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The user the choice belongs to (<c>User.Id</c>, the Auth0 subject). Foreign key with <c>ON DELETE CASCADE</c>.</summary>
    [Required, MaxLength(PopupAlert.UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The kind of event the choice is about. Stored by name.</summary>
    public NotificationEventType EventType { get; set; }

    /// <summary>The channels chosen, as the bits of <see cref="NotificationChannels"/> (an integer in the column). <c>None</c> = nothing at all.</summary>
    public NotificationChannels Channels { get; set; }

    /// <summary>UTC instant of the last save.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("GuestCheckInSessions")]
public class GuestCheckInSession : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The booking this session belongs to.</summary>
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Tenant key — copied from booking at creation time.</summary>
    public Guid OrgId { get; set; }

    /// <summary>SHA-256 hex hash of the raw token sent to the guest.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// End of validity of the link (creation + <c>CheckIn:SessionLifetimeDays</c>). An open session past it is expired even
    /// before the send job marks it <see cref="GuestCheckInSessionStatus.Scaduto"/> (<see cref="EffectiveStatus"/>, A5-27).
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    public GuestCheckInSessionStatus Status { get; set; } = GuestCheckInSessionStatus.Inviato;

    /// <summary>
    /// When the email with the link was handed to the email provider (CO-09); null while it is not (link generated to
    /// copy, email queued or failed). Sessions created before CO-09 hold their creation time.
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Delivery of the link by email (CO-09, A5-26): never requested (link to copy), queued, handed to the provider or
    /// failed. Null for the sessions created before CO-09 (unknown).
    /// </summary>
    public GuestCheckInLinkEmailStatus? LinkEmailStatus { get; set; }

    /// <summary>Stable reason of a <see cref="GuestCheckInLinkEmailStatus.Failed"/> email (<see cref="GuestCheckInLinkEmailErrors"/>).</summary>
    [MaxLength(50)]
    public string? LinkEmailError { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The link can still be opened by the guest, unless <see cref="ExpiresAt"/> has passed.</summary>
    public bool IsOpen => Status is GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione;

    /// <summary>The guest has submitted the data.</summary>
    public bool IsCompleted => Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato;

    /// <summary>The status at <paramref name="nowUtc"/>: an open session past <see cref="ExpiresAt"/> is expired.</summary>
    public GuestCheckInSessionStatus EffectiveStatus(DateTime nowUtc) =>
        IsOpen && ExpiresAt < nowUtc ? GuestCheckInSessionStatus.Scaduto : Status;
}

public enum GuestCheckInSessionStatus
{
    /// <summary>Link issued (by email or to copy), not yet opened by the guest.</summary>
    Inviato,
    /// <summary>Guest opened the link; form in progress.</summary>
    InCompilazione,
    /// <summary>Guest submitted all data. The session stays here until Alloggiati Web returns a real receipt.</summary>
    Completo,
    /// <summary>
    /// The Alloggiati Web communication of the booking has a real receipt (CO-13). Never set on queueing: CO-11
    /// moved the sessions set that way back to <see cref="Completo"/>.
    /// </summary>
    AlloggiatiInviato,
    /// <summary>Token expired (<see cref="GuestCheckInSession.ExpiresAt"/> passed) or replaced by a new link.</summary>
    Scaduto,
}

/// <summary>Delivery of the check-in link by email (CO-09). Stored as an integer: never renumber.</summary>
public enum GuestCheckInLinkEmailStatus
{
    /// <summary>The host generated the link to copy it: no email requested.</summary>
    NotRequested = 0,

    /// <summary>Queued for delivery (Hangfire), not handed to the provider yet.</summary>
    Queued = 1,

    /// <summary>Handed to the email provider (<see cref="GuestCheckInSession.SentAt"/>).</summary>
    Sent = 2,

    /// <summary>Not sent: see <see cref="GuestCheckInSession.LinkEmailError"/>. The link itself stays valid.</summary>
    Failed = 3,
}

/// <summary>Stable reasons of a failed check-in link email (<see cref="GuestCheckInSession.LinkEmailError"/>), shown to the host.</summary>
public static class GuestCheckInLinkEmailErrors
{
    /// <summary>The guest of the booking has no email address.</summary>
    public const string NoRecipient = "no_recipient";

    /// <summary>The email provider is not configured (<c>Email__ApiKey</c>, <c>Email__FromAddress</c>).</summary>
    public const string ProviderNotConfigured = "provider_not_configured";

    /// <summary>The email could not be queued (Hangfire storage error).</summary>
    public const string QueueFailed = "queue_failed";

    /// <summary>The provider refused the email (e.g. invalid address): not retried.</summary>
    public const string Rejected = "rejected";

    /// <summary>Temporary provider errors on every attempt.</summary>
    public const string NotDelivered = "not_delivered";

    /// <summary>The public site URL (<c>App:PublicSiteBaseUrl</c>) is missing: no link can be built.</summary>
    public const string LinkUnavailable = "link_unavailable";

    /// <summary>The link expired or was replaced before the email left: nothing was sent.</summary>
    public const string LinkNotUsable = "link_not_usable";
}

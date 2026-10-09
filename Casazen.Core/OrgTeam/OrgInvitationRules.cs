using Casazen.Core.Entities.Enums;

namespace Casazen.Core.OrgTeam;

/// <summary>
/// The fixed numbers and small rules of the org invitations (AM-02), in one place so the services, the job and the
/// tests read the same thing.
/// </summary>
public static class OrgInvitationRules
{
    /// <summary>How long an invitation can be accepted, from when it is sent (or sent again).</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    /// <summary>When the reminder goes out, from when the invitation was sent: the third day.</summary>
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromDays(3);

    /// <summary>
    /// Days after which the personal data of a closed invitation (accepted, revoked, expired) is deleted with its row.
    /// Configurable as <c>OrgTeam:InvitationRetentionDays</c>.
    /// </summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>Configuration key of the retention.</summary>
    public const string RetentionDaysConfigKey = "OrgTeam:InvitationRetentionDays";

    public const int MaxEmailLength = 255;
    public const int MaxNameLength = 200;

    /// <summary>Languages of the invitation emails. The first is the default.</summary>
    public static readonly IReadOnlyList<string> Languages = ["it", "en"];

    /// <summary>The form an email is stored and compared in: trimmed and lowercase.</summary>
    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The language to use for <paramref name="language"/>: itself when supported (any case), the default otherwise.</summary>
    public static string NormalizeLanguage(string? language)
    {
        var value = language?.Trim().ToLowerInvariant();
        return value is not null && Languages.Contains(value) ? value : Languages[0];
    }

    /// <summary>The instant an invitation expiring at <paramref name="expiresAt"/> is due its reminder (three days after it was sent).</summary>
    public static DateTime ReminderDueAt(DateTime expiresAt) => expiresAt - (Validity - ReminderAfter);

    /// <summary>
    /// True when the invitation can be accepted at <paramref name="now"/>: still pending and not past its expiry. A pending
    /// row past its expiry is already "expired" for every reader, before the job marks it.
    /// </summary>
    public static bool IsOpen(OrgInvitationStatus status, DateTime expiresAt, DateTime now) =>
        status == OrgInvitationStatus.Pending && expiresAt > now;
}

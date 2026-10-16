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

    /// <summary>Configuration key of the cap on the invitation emails of an org in <see cref="DailyWindow"/> (AM-02c, decision AM-D15).</summary>
    public const string DailyLimitConfigKey = "OrgTeam:InvitationDailyLimit";

    /// <summary>
    /// Invitation emails an org may send in <see cref="DailyWindow"/>, creations and «reinvia» together, when the configuration
    /// does not say (AM-D15: 10 in 24 hours). A value that is not a positive number is ignored.
    /// </summary>
    public const int DefaultDailyLimit = 10;

    /// <summary>The window of the cap: the last 24 hours, sliding.</summary>
    public static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    /// <summary>Configuration key of the wait between two «reinvia» of the same invitation, in minutes (AM-02c, decision AM-D15).</summary>
    public const string ResendMinutesConfigKey = "OrgTeam:ResendMinutes";

    /// <summary>Minutes between two «reinvia» of the same invitation when the configuration does not say (AM-D15: one an hour). A value that is not a positive number is ignored.</summary>
    public const int DefaultResendMinutes = 60;

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

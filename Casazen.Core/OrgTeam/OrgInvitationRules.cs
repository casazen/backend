using Casazen.Core.Entities.Enums;

namespace Casazen.Core.OrgTeam;

public static class OrgInvitationRules
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    public static readonly TimeSpan ReminderAfter = TimeSpan.FromDays(3);

    public const int DefaultRetentionDays = 30;

    public const string RetentionDaysConfigKey = "OrgTeam:InvitationRetentionDays";

    public const string DailyLimitConfigKey = "OrgTeam:InvitationDailyLimit";

    public const int DefaultDailyLimit = 10;

    public static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    public const string ResendMinutesConfigKey = "OrgTeam:ResendMinutes";

    public const int DefaultResendMinutes = 60;

    public const int MaxEmailLength = 255;
    public const int MaxNameLength = 200;

    public const string DefaultLanguage = "it";

    public static readonly IReadOnlyList<string> Languages = [DefaultLanguage, "en"];

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The language to use for <paramref name="language"/>: itself when supported (any case), the default otherwise.</summary>
    public static string NormalizeLanguage(string? language)
    {
        var value = language?.Trim().ToLowerInvariant();
        return value is not null && Languages.Contains(value) ? value : Languages[0];
    }

    public static DateTime ReminderDueAt(DateTime expiresAt) => expiresAt - (Validity - ReminderAfter);

    public static bool IsOpen(OrgInvitationStatus status, DateTime expiresAt, DateTime now) =>
        status == OrgInvitationStatus.Pending && expiresAt > now;
}

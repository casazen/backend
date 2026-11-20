using System.Globalization;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The alerts about the 24 hours of the notification of the guests of a Ticino property to the cantonal police (E30/F02/US01/T01, NF-127, D9), as e-mail and as
/// push text: the property, the arrival day, the deadline and what the data of the day miss as <b>types of field</b> (the codes of
/// <see cref="TicinoMissing"/> and <see cref="TicinoAlertMissing"/>, translated), never the name of a guest, a value, a document or a booking code. The
/// authority may be named («Polizia cantonale»); no supplier is. Texts in Italian and English (<c>EmailTexts.resx</c>, <c>EmailTexts.en.resx</c>): like every e-mail
/// to a host they are not translated into the guest languages (<c>EmailTextsParityTests</c>).
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string TicinoNotificationAlert = "ticino-notification-alert";
    }

    /// <summary>The order the types of field that are missing are listed in; a code that is not here follows, in alphabetical order.</summary>
    private static readonly string[] TicinoMissingOrder =
    [
        TicinoAlertMissing.StructureId,
        TicinoMissing.Guests,
        TicinoMissing.FirstName,
        TicinoMissing.LastName,
        TicinoMissing.BirthDate,
        TicinoMissing.Nationality,
        TicinoMissing.DocumentType,
    ];

    /// <summary>
    /// A Ticino arrival day still to notify, to the host (<see cref="ComplianceAlertKind.TicinoFileToPrepare"/>: the day of the arrival;
    /// <see cref="ComplianceAlertKind.TicinoDeadlineApproaching"/>: the deadline is near; <see cref="ComplianceAlertKind.TicinoOverdue"/>: it is over, with the
    /// number of the reminder). The button leads to the compliance page of the console when its link is known.
    /// </summary>
    public static EmailContent TicinoNotificationAlert(CultureInfo culture, ComplianceAlert alert, string propertyName, string? complianceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var phase = TicinoPhase(alert);
        var builder = new EmailHtmlBuilder(culture);
        var day = alert.ArrivalDay?.ToDateTime(TimeOnly.MinValue);
        var deadline = alert.DueAtUtc is { } due ? builder.FormatInstant(due) : string.Empty;

        builder = builder.Paragraph($"TicinoNotificationAlert_{phase}_Body", propertyName, day, deadline);
        if (TicinoMissingLabels(alert.MissingFields, culture) is { Length: > 0 } missing)
            builder = builder.Paragraph("TicinoNotificationAlert_Missing", missing);

        builder = builder.Paragraph("TicinoNotificationAlert_Action");
        if (alert.ReminderNumber > 0)
            builder = builder.Muted("AlloggiatiOverdue_Reminder", alert.ReminderNumber, Math.Max(alert.ReminderNumber, alert.MaxReminders));

        return WithComplianceLink(builder, complianceUrl).Build($"TicinoNotificationAlert_{phase}_Subject", propertyName, day);
    }

    /// <summary>
    /// Push text of a Ticino arrival day (E30/F02/US01/T01): the property and the day, the deadline while it is ahead, and the types of field that are missing.
    /// Plain text on a lock screen, no guest data.
    /// </summary>
    public static PushText TicinoNotificationPush(CultureInfo culture, ComplianceAlert alert, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var overdue = alert.Kind == ComplianceAlertKind.TicinoOverdue;
        var prefix = overdue ? "TicinoNotificationPush_Overdue" : "TicinoNotificationPush_Due";
        var day = alert.ArrivalDay?.ToDateTime(TimeOnly.MinValue).ToString(EmailTexts.Get("Format_Date", culture), culture) ?? string.Empty;
        var deadline = alert.DueAtUtc is { } due
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(due, DateTimeKind.Utc), RomeCalendar.TimeZone).ToString(EmailTexts.Get("Format_DateTime", culture), culture)
            : string.Empty;

        var body = string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), propertyName, day, deadline);
        if (TicinoMissingLabels(alert.MissingFields, culture) is { Length: > 0 } missing)
            body += " " + string.Format(culture, EmailTexts.Get("TicinoNotificationPush_Missing", culture), missing);

        return new PushText(EmailTexts.Get($"{prefix}_Title", culture), body);
    }

    private static string TicinoPhase(ComplianceAlert alert) => alert.Kind switch
    {
        ComplianceAlertKind.TicinoFileToPrepare => "FileToPrepare",
        ComplianceAlertKind.TicinoDeadlineApproaching => "Approaching",
        ComplianceAlertKind.TicinoOverdue => "Overdue",
        _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Kind, "Not an alert about a Ticino arrival day."),
    };

    /// <summary>
    /// The types of field that are missing as the texts write them, comma separated and in the order of <see cref="TicinoMissingOrder"/>: a code with no text of its
    /// own (one a later version of the file plan adds) is told as «other data», once. Empty when nothing is missing.
    /// </summary>
    internal static string TicinoMissingLabels(IReadOnlyList<string>? codes, CultureInfo culture)
    {
        if (codes is null || codes.Count == 0)
            return string.Empty;

        var labels = new List<string>();
        var other = false;
        foreach (var code in codes.Distinct(StringComparer.Ordinal).OrderBy(c => OrderOf(c)).ThenBy(c => c, StringComparer.Ordinal))
        {
            // A code is a word of letters and digits: only such a code is looked up as the key of a text.
            var text = code.Length > 0 && code.All(char.IsAsciiLetterOrDigit)
                ? EmailTexts.Resources.GetString($"TicinoMissing_{code}", culture)
                : null;
            if (text is null)
                other = true;
            else
                labels.Add(text);
        }

        if (other)
            labels.Add(EmailTexts.Get("TicinoMissing_other", culture));

        return string.Join(", ", labels);

        static int OrderOf(string code)
        {
            var index = Array.IndexOf(TicinoMissingOrder, code);
            return index < 0 ? TicinoMissingOrder.Length : index;
        }
    }
}

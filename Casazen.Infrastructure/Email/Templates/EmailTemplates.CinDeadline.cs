using System.Globalization;
using Casazen.Core.Regulatory;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Host alert about properties without a valid CIN (CO-20, A5-31).</summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string CinDeadlineAlert = "cin-deadline-alert";
    }

    /// <summary>
    /// Properties of the org still without a valid CIN, to the host. The first paragraph follows the phase of the
    /// configured deadline (<paramref name="deadline"/>): days left (never "today" once it has passed), the deadline day,
    /// deadline passed, or no date at all when none is configured. The deadline is shown as a date, never as a legal
    /// term (it is configuration, RS-2); the obligation and the penalties cite art. 13-ter D.L. 145/2023
    /// (<c>.claude/context/regulations/cin.md</c>). Link to the CIN compliance page of the console when known.
    /// </summary>
    public static EmailContent CinDeadlineAlert(
        CultureInfo culture,
        CinDeadlineStatus deadline,
        IReadOnlyCollection<string> propertyNames,
        string? complianceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        ArgumentNullException.ThrowIfNull(propertyNames);

        var date = deadline.Deadline?.ToDateTime(TimeOnly.MinValue);
        var builder = new EmailHtmlBuilder(culture);
        builder = deadline.Phase switch
        {
            CinDeadlinePhase.Upcoming when deadline.DaysUntilDeadline == 1 =>
                builder.Paragraph("CinDeadlineAlert_UpcomingOneDay_Body", date),
            CinDeadlinePhase.Upcoming => builder.Paragraph("CinDeadlineAlert_Upcoming_Body", deadline.DaysUntilDeadline, date),
            CinDeadlinePhase.DueToday => builder.Paragraph("CinDeadlineAlert_DueToday_Body", date),
            CinDeadlinePhase.Passed => builder.Paragraph("CinDeadlineAlert_Passed_Body", date),
            _ => builder.Paragraph("CinDeadlineAlert_NoDeadline_Body"),
        };

        builder = builder
            .ValueList(propertyNames)
            .Paragraph("CinDeadlineAlert_Obligation")
            .Paragraph("CinDeadlineAlert_Penalties")
            .Paragraph("CinDeadlineAlert_Action");
        if (!string.IsNullOrWhiteSpace(complianceUrl))
        {
            builder = builder
                .Button("CinDeadlineAlert_Cta", complianceUrl)
                .LinkFallback("Booking_LinkFallback", complianceUrl);
        }

        return deadline.Phase switch
        {
            CinDeadlinePhase.Upcoming => builder.Build("CinDeadlineAlert_Upcoming_Subject", date),
            CinDeadlinePhase.DueToday => builder.Build("CinDeadlineAlert_DueToday_Subject", date),
            CinDeadlinePhase.Passed => builder.Build("CinDeadlineAlert_Passed_Subject", date),
            _ => builder.Build("CinDeadlineAlert_NoDeadline_Subject"),
        };
    }
}

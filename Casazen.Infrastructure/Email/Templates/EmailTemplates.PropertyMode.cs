using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Host emails about the scheduled change of rental mode of a property (PM-02): programmed, applied, failed. Italian by
/// default, English; every dynamic value is HTML-encoded by <see cref="EmailHtmlBuilder"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string PropertyModeChangeScheduled = "property-mode-change-scheduled";
        public const string PropertyModeChangeApplied = "property-mode-change-applied";
        public const string PropertyModeChangeFailed = "property-mode-change-failed";
    }

    /// <summary>
    /// A change of mode was programmed, to the host: from when, what stays as it is until then, what closes (to long-term the
    /// calendar, from the night before the day), how to withdraw it. <paramref name="propertyUrl"/> is the property page of
    /// the area the property is in now.
    /// </summary>
    public static EmailContent PropertyModeChangeScheduled(
        CultureInfo culture,
        string propertyName,
        RentalMode toMode,
        DateTime effectiveDate,
        string? propertyUrl = null)
    {
        var toLong = toMode == RentalMode.Long;
        var prefix = toLong ? "PropertyModeScheduled_ToLong" : "PropertyModeScheduled_ToShort";
        var consequence = toLong ? "PropertyModeScheduled_ToLong_CalendarClosed" : "PropertyModeScheduled_ToShort_Requirements";
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph($"{prefix}_Body", propertyName, effectiveDate)
            .List(
            [
                ($"{prefix}_Until", Array.Empty<object?>()),
                (consequence, Array.Empty<object?>()),
            ])
            .Paragraph("PropertyModeScheduled_Cancel");
        if (!string.IsNullOrWhiteSpace(propertyUrl))
        {
            builder = builder
                .Button("PropertyModeScheduled_Cta", propertyUrl)
                .LinkFallback("Booking_LinkFallback", propertyUrl);
        }

        return builder.Build($"{prefix}_Subject", propertyName);
    }

    /// <summary>
    /// The property changed mode, to the host. To long-term: it is not published nor bookable any more, the dates stay closed
    /// until <paramref name="calendarClosedUntil"/>, the portals' listings are the host's to remove. To short stays: the dates
    /// closed by the change are free, the short-stay requirements are to check. <paramref name="actionUrl"/> is the
    /// property page (to long-term) or its activation wizard (to short stays).
    /// </summary>
    public static EmailContent PropertyModeChangeApplied(
        CultureInfo culture,
        string propertyName,
        RentalMode toMode,
        DateTime? calendarClosedUntil = null,
        string? actionUrl = null)
    {
        var toLong = toMode == RentalMode.Long;
        var prefix = toLong ? "PropertyModeApplied_ToLong" : "PropertyModeApplied_ToShort";
        var builder = new EmailHtmlBuilder(culture).Paragraph($"{prefix}_Body", propertyName);
        if (toLong)
        {
            var closed = calendarClosedUntil is { } until
                ? ("PropertyModeApplied_ToLong_CalendarClosed", new object?[] { until })
                : ("PropertyModeApplied_ToLong_CalendarClosedNoDate", Array.Empty<object?>());
            builder = builder.List(
            [
                closed,
                ("PropertyModeApplied_ToLong_Listings", Array.Empty<object?>()),
            ]);
        }
        else
        {
            builder = builder.List(
            [
                ("PropertyModeApplied_ToShort_CalendarOpen", Array.Empty<object?>()),
                ("PropertyModeApplied_ToShort_Requirements", Array.Empty<object?>()),
            ]);
        }

        if (!string.IsNullOrWhiteSpace(actionUrl))
        {
            builder = builder
                .Button($"{prefix}_Cta", actionUrl)
                .LinkFallback("Booking_LinkFallback", actionUrl);
        }

        return builder.Build($"{prefix}_Subject", propertyName);
    }

    /// <summary>
    /// The change could not be applied on its day, to the host: the property kept its mode and why (<paramref name="reason"/>
    /// is a <see cref="PropertyModeChange.FailureReason"/>: stays or portal reservations, a lease, a draft lease, or anything
    /// else). With <paramref name="earliestDate"/> it suggests the first day that is free now.
    /// </summary>
    public static EmailContent PropertyModeChangeFailed(
        CultureInfo culture,
        string propertyName,
        RentalMode toMode,
        DateTime effectiveDate,
        string? reason,
        DateTime? earliestDate = null,
        string? propertyUrl = null)
    {
        var target = PropertyModeLabel(culture, toMode);
        var current = PropertyModeLabel(culture, PropertyModeRules.Opposite(toMode));
        var reasonKey = reason switch
        {
            PropertyModeErrorCodes.BlockedByBookings => "PropertyModeFailed_Reason_Bookings",
            PropertyModeErrorCodes.BlockedByLease => "PropertyModeFailed_Reason_Lease",
            PropertyModeErrorCodes.BlockedByDraftLease => "PropertyModeFailed_Reason_DraftLease",
            _ => "PropertyModeFailed_Reason_Other",
        };
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("PropertyModeFailed_Body", propertyName, target, effectiveDate, current)
            .Paragraph(reasonKey);

        // The calendar the change had closed (to long-term) is open again when the stays stood in its way.
        if (toMode == RentalMode.Long && reason is PropertyModeErrorCodes.BlockedByBookings)
            builder = builder.Paragraph("PropertyModeFailed_CalendarReopened");

        builder = earliestDate is { } earliest
            ? builder.Paragraph("PropertyModeFailed_Earliest", earliest)
            : builder.Paragraph("PropertyModeFailed_Retry");
        if (!string.IsNullOrWhiteSpace(propertyUrl))
        {
            builder = builder
                .Button("PropertyModeFailed_Cta", propertyUrl)
                .LinkFallback("Booking_LinkFallback", propertyUrl);
        }

        return builder.Build("PropertyModeFailed_Subject", propertyName);
    }

    /// <summary>Localized name of a rental mode ("affitto breve", "affitto lungo"), for the sentences that name two modes.</summary>
    private static string PropertyModeLabel(CultureInfo culture, RentalMode mode) =>
        EmailTexts.Get(mode == RentalMode.Long ? "PropertyModeLabel_Long" : "PropertyModeLabel_Short", culture);
}

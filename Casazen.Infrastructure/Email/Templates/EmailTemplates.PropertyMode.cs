using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string PropertyModeChangeScheduled = "property-mode-change-scheduled";
        public const string PropertyModeChangeApplied = "property-mode-change-applied";
        public const string PropertyModeChangeFailed = "property-mode-change-failed";
    }

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

    private static string PropertyModeLabel(CultureInfo culture, RentalMode mode) =>
        EmailTexts.Get(mode == RentalMode.Long ? "PropertyModeLabel_Long" : "PropertyModeLabel_Short", culture);
}

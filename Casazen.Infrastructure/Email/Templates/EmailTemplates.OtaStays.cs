using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string HostOtaStayReview = "host-ota-stay-review";
    }

    public static EmailContent HostOtaStayReview(
        CultureInfo culture,
        OtaStayReviewReason reason,
        string guestName,
        string propertyName,
        string channelName,
        DateTime checkInDate,
        DateTime checkOutDate,
        DateTime? channelCheckIn = null,
        DateTime? channelCheckOut = null,
        string? bookingUrl = null)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder = reason == OtaStayReviewReason.BlockDatesChanged && channelCheckIn is { } newIn && channelCheckOut is { } newOut
            ? builder.Paragraph(
                "OtaStayReview_DatesChanged_Body", guestName, propertyName, channelName, checkInDate, checkOutDate, newIn, newOut)
            : builder.Paragraph(
                reason == OtaStayReviewReason.BlockDatesChanged ? "OtaStayReview_DatesChangedUnknown_Body" : "OtaStayReview_Removed_Body",
                guestName,
                propertyName,
                channelName,
                checkInDate,
                checkOutDate);
        builder = WithBookingLink(builder.Paragraph("OtaStayReview_NothingChanged").Paragraph("OtaStayReview_Action"), bookingUrl);
        return builder.Build("OtaStayReview_Subject", channelName, propertyName, checkInDate);
    }

    public static PushText OtaStayReviewPush(
        CultureInfo culture,
        OtaStayReviewReason reason,
        string propertyName,
        string channelName,
        DateTime checkInDate)
    {
        var date = checkInDate.ToString(EmailTexts.Get("Format_Date", culture), culture);
        var body = reason == OtaStayReviewReason.BlockDatesChanged
            ? "OtaStayReviewPush_DatesChanged_Body"
            : "OtaStayReviewPush_Removed_Body";
        return new PushText(
            EmailTexts.Get("OtaStayReviewPush_Title", culture),
            string.Format(culture, EmailTexts.Get(body, culture), propertyName, channelName, date));
    }

    public static string OtaChannelName(BookingSource source, string? feedLabel)
    {
        var name = source switch
        {
            BookingSource.BookingCom => "Booking.com",
            BookingSource.TripAdvisor => "Tripadvisor",
            _ => source.ToString(),
        };
        return string.IsNullOrWhiteSpace(feedLabel) ? name : $"{name} ({feedLabel.Trim()})";
    }
}

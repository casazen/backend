using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Host alerts for biometric verification queue (NF-12, ADR-013): manual verification required and
/// new photo request to guest.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string VerificationRequired = "verification-required";
        public const string GuestNewPhotoRequest = "guest-new-photo-request";
    }

    /// <summary>
    /// Biometric verification requires operator decision (NF-12, AC-NF12-1), to the host: the guest's selfie
    /// escalated, reached max attempts, or consent was declined. Links to the booking in the console.
    /// </summary>
    public static EmailContent HostVerificationRequired(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string? bookingUrl = null) =>
        WithBookingLink(
                new EmailHtmlBuilder(culture)
                    .Paragraph("VerificationRequired_Body", guestName, propertyName, checkInDate)
                    .Paragraph("VerificationRequired_Action"),
                bookingUrl)
            .Build("VerificationRequired_Subject", propertyName);

    /// <summary>
    /// Operator requested a new selfie photo (NF-12, AC-NF12-5), to the guest: explains why and provides
    /// the check-in link with the extended session.
    /// </summary>
    public static EmailContent GuestNewPhotoRequest(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string checkInLink) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestNewPhotoRequest_Greeting", guestName)
            .Paragraph("GuestNewPhotoRequest_Body", propertyName, checkInDate)
            .Paragraph("GuestNewPhotoRequest_Action")
            .Button("GuestNewPhotoRequest_Cta", checkInLink)
            .LinkFallback("GuestNewPhotoRequest_LinkFallback", checkInLink)
            .Build("GuestNewPhotoRequest_Subject", propertyName);

    /// <summary>
    /// Push text for verification required alert (NF-12): short notification to property hosts.
    /// </summary>
    public static PushText VerificationRequiredPush(CultureInfo culture, string guestName, string propertyName)
    {
        return new PushText(
            EmailTexts.Get("VerificationRequiredPush_Title", culture),
            string.Format(culture, EmailTexts.Get("VerificationRequiredPush_Body", culture), guestName, propertyName));
    }
}

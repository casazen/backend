using System.Globalization;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Host alerts for biometric verification queue (NF-12, ADR-013): manual verification required and
/// new photo request to guest. Since E03/F04/US02/T03 also the alert to the operators (no guest name, no document data) and the e-mail
/// that gives the guest the lock code after a passed verification.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string VerificationRequired = "verification-required";
        public const string GuestNewPhotoRequest = "guest-new-photo-request";
        public const string VerificationOperatorAlert = "verification-operator-alert";
        public const string GuestAccessCode = "guest-access-code";
        public const string GuestVideoCall = "guest-video-call";
    }

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

    public static EmailContent GuestNewPhotoRequest(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string checkInLink,
        GuestEmailBranding? branding = null) =>
        new EmailHtmlBuilder(culture, branding)
            .Paragraph("GuestNewPhotoRequest_Greeting", guestName)
            .Paragraph("GuestNewPhotoRequest_Body", propertyName, checkInDate)
            .Paragraph("GuestNewPhotoRequest_Action")
            .Button("GuestNewPhotoRequest_Cta", checkInLink)
            .LinkFallback("GuestNewPhotoRequest_LinkFallback", checkInLink)
            .Build("GuestNewPhotoRequest_Subject", propertyName);

    public static PushText VerificationRequiredPush(CultureInfo culture, string guestName, string propertyName)
    {
        return new PushText(
            EmailTexts.Get("VerificationRequiredPush_Title", culture),
            string.Format(culture, EmailTexts.Get("VerificationRequiredPush_Body", culture), guestName, propertyName));
    }

    public static EmailContent VerificationOperatorAlert(
        CultureInfo culture,
        string propertyName,
        DateTime checkInDate,
        string reasonCode,
        string? bookingUrl = null) =>
        WithBookingLink(
                new EmailHtmlBuilder(culture)
                    .Paragraph("VerificationOperatorAlert_Body", propertyName, checkInDate)
                    .Paragraph(VerificationOperatorAlertReasonKey(reasonCode))
                    .Muted("VerificationOperatorAlert_ReasonCode", reasonCode)
                    .Paragraph("VerificationOperatorAlert_Action"),
                bookingUrl)
            .Build("VerificationOperatorAlert_Subject", propertyName);

    public static PushText VerificationOperatorAlertPush(CultureInfo culture, string propertyName) =>
        new(
            EmailTexts.Get("VerificationOperatorAlertPush_Title", culture),
            string.Format(culture, EmailTexts.Get("VerificationOperatorAlertPush_Body", culture), propertyName));

    public static EmailContent GuestAccessCode(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string lockCode,
        GuestEmailBranding? branding = null) =>
        new EmailHtmlBuilder(culture, branding)
            .Paragraph("GuestAccessCode_Greeting", guestName)
            .Paragraph("GuestAccessCode_Body", propertyName, checkInDate)
            .Code(lockCode)
            .Muted("GuestAccessCode_Keep")
            .Build("GuestAccessCode_Subject", propertyName);

    /// <summary>
    /// An operator proposes a video call for the check-in (E22/F01/US01/T03), to the guest: «Videochiamata per il check-in». Why, what to have at hand and the
    /// link of the meeting the operator created, as a button and as plain text for the clients that do not render one. Written in the language the guest
    /// chose on the check-in page (Italian, English, German, French or Spanish: the five of <see cref="Casazen.Core.Utilities.GuestLanguages"/>). Every value, the
    /// link included, is HTML-encoded by the builder. CasaZen keeps the link in no table and no log; it stays only in the body of the queued message (the
    /// Hangfire job of <c>EmailDeliveryJob</c>) until that job expires, 24 hours by default after a successful send.
    /// </summary>
    public static EmailContent GuestVideoCall(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string meetingUrl,
        GuestEmailBranding? branding = null)
    {
        return new EmailHtmlBuilder(culture, branding)
            .Paragraph("GuestVideoCall_Greeting", guestName)
            .Paragraph("GuestVideoCall_Body", propertyName, checkInDate)
            .Paragraph("GuestVideoCall_Instructions")
            .Button("GuestVideoCall_Cta", meetingUrl)
            .LinkFallback("GuestVideoCall_LinkFallback", meetingUrl)
            .Muted("GuestVideoCall_Hint")
            .Build("GuestVideoCall_Subject", propertyName);
    }

    public static CultureInfo GuestAccessCodeCulture(string? sessionLanguage) =>
        string.IsNullOrWhiteSpace(sessionLanguage) || string.Equals(sessionLanguage.Trim(), "it", StringComparison.OrdinalIgnoreCase)
            ? DefaultCulture
            : CultureInfo.GetCultureInfo("en");

    private static string VerificationOperatorAlertReasonKey(string reasonCode) => reasonCode switch
    {
        VerificationAlertReasons.Review => "VerificationOperatorAlert_Reason_Review",
        VerificationAlertReasons.AttemptsExhausted => "VerificationOperatorAlert_Reason_AttemptsExhausted",
        VerificationAlertReasons.ConsentRefused => "VerificationOperatorAlert_Reason_ConsentRefused",
        VerificationAlertReasons.TechnicalError => "VerificationOperatorAlert_Reason_TechnicalError",
        VerificationAlertReasons.GuestRequest => "VerificationOperatorAlert_Reason_GuestRequest",
        VerificationAlertReasons.BiometricsDisabled => "VerificationOperatorAlert_Reason_BiometricsDisabled",
        VerificationAlertReasons.MinorExcluded => "VerificationOperatorAlert_Reason_MinorExcluded",
        VerificationAlertReasons.LegalTextNotApproved => "VerificationOperatorAlert_Reason_LegalTextNotApproved",
        _ => "VerificationOperatorAlert_Reason_Other",
    };
}

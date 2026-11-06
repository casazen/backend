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

    /// <summary>
    /// The identity of a guest waits for an operator (E03/F04/US02/T03, AC-NF50-3), to each user who decides the verifications of the property:
    /// the property, the arrival date, why (a sentence and the code of <see cref="VerificationAlertReasons"/>) and the booking in the console.
    /// No guest name, no image, nothing read on the document. Every dynamic value is HTML-encoded by the builder.
    /// </summary>
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

    /// <summary>The push of <see cref="VerificationOperatorAlert"/>: the property only (a lock screen never shows a guest name).</summary>
    public static PushText VerificationOperatorAlertPush(CultureInfo culture, string propertyName) =>
        new(
            EmailTexts.Get("VerificationOperatorAlertPush_Title", culture),
            string.Format(culture, EmailTexts.Get("VerificationOperatorAlertPush_Body", culture), propertyName));

    /// <summary>
    /// The lock code of the property, to the guest, after a passed verification (E03/F04/US02/T03 and T04, AC-NF50-2, AC-NF50-4): sent only when
    /// the agent said ok or an operator approved. The code is HTML-encoded like every value; it is never logged.
    /// </summary>
    public static EmailContent GuestAccessCode(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string lockCode) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestAccessCode_Greeting", guestName)
            .Paragraph("GuestAccessCode_Body", propertyName, checkInDate)
            .Code(lockCode)
            .Muted("GuestAccessCode_Keep")
            .Build("GuestAccessCode_Subject", propertyName);

    /// <summary>
    /// The culture of <see cref="GuestAccessCode"/> for the language the guest chose on the check-in page: Italian for <c>it</c> (and when none
    /// was chosen), English for every other language (the code e-mail is written in Italian and English only).
    /// </summary>
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
        _ => "VerificationOperatorAlert_Reason_Other",
    };
}

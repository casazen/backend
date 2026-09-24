using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Every transactional email of the application, rendered with <see cref="EmailHtmlBuilder"/> in Italian (default)
/// or English. Links are passed in already built by <see cref="PublicSiteLinks"/>.
/// </summary>
public static class EmailTemplates
{
    /// <summary>Recipients have no language preference yet: emails are sent in Italian.</summary>
    public static CultureInfo DefaultCulture { get; } = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Template names used in logs and queued jobs.</summary>
    public static class Names
    {
        public const string ServiceRequestCreated = "service-request-created";
        public const string ServiceRequestStatusChanged = "service-request-status-changed";
        public const string SupplierInvite = "supplier-invite";
        public const string GuestCheckInLink = "guest-checkin-link";
        public const string GuestCheckInIncomplete = "guest-checkin-incomplete";
        public const string AlloggiatiDeadline = "alloggiati-deadline";
        public const string GuestRefundConfirmed = "guest-refund-confirmed";
        public const string GuestBookingCancelled = "guest-booking-cancelled";
        public const string GuestLatePaymentConfirmed = "guest-late-payment-confirmed";
        public const string GuestPaymentRefundedDatesUnavailable = "guest-payment-refunded-dates-unavailable";
        public const string RliDeadlineReminder = "rli-deadline-reminder";
        public const string RliDeadlineOverdue = "rli-deadline-overdue";
        public const string RliExtraEuNotice = "rli-extra-eu-notice";
        public const string OnSiteRequestReceived = "onsite-request-received";
        public const string OnSiteRequestToHost = "onsite-request-to-host";
        public const string OnSiteRequestAccepted = "onsite-request-accepted";
        public const string OnSiteRequestDeclined = "onsite-request-declined";
        public const string OnSiteRequestExpired = "onsite-request-expired";
        public const string GuestDeferredChargeFailed = "guest-deferred-charge-failed";
        public const string HostDeferredChargeFailed = "host-deferred-charge-failed";
        public const string GuestDeferredChargeCancelled = "guest-deferred-charge-cancelled";
        public const string HostDeferredChargeCancelled = "host-deferred-charge-cancelled";
    }

    /// <summary>EmailTexts key of the label of a <see cref="ServiceCategories"/> code.</summary>
    public static string ServiceCategoryKey(string code) => $"ServiceCategory_{code}";

    /// <summary>
    /// Label of a service category code in <paramref name="culture"/> (SU-03). A value that is not a known code (an
    /// old value kept by the migration) is shown as it is; the builder HTML-encodes it like any dynamic value.
    /// </summary>
    public static string ServiceCategoryLabel(CultureInfo culture, string category) =>
        ServiceCategories.IsKnown(category)
            ? EmailTexts.Get(ServiceCategoryKey(category), culture)
            : category;

    /// <summary>New service request, to the supplier.</summary>
    public static EmailContent ServiceRequestCreated(
        CultureInfo culture,
        string supplierName,
        string category,
        string propertyName,
        string? notes,
        string inboxUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("ServiceRequestCreated_Greeting", supplierName)
            .Paragraph("ServiceRequestCreated_Body", ServiceCategoryLabel(culture, category), propertyName)
            .Quote("ServiceRequestCreated_NotesLabel", notes)
            .Button("ServiceRequestCreated_Cta", inboxUrl)
            .Build("ServiceRequestCreated_Subject", propertyName);

    /// <summary>Service request taken, completed or rejected by the supplier, to the host.</summary>
    public static EmailContent ServiceRequestStatusChanged(
        CultureInfo culture,
        ServiceRequestStatus status,
        string category,
        string propertyName,
        string? rejectionReason = null)
    {
        var prefix = status switch
        {
            ServiceRequestStatus.PresoInCarico => "ServiceRequestTaken",
            ServiceRequestStatus.Completato => "ServiceRequestCompleted",
            ServiceRequestStatus.Rifiutato => "ServiceRequestRejected",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No email for this service request status."),
        };

        var builder = new EmailHtmlBuilder(culture).Paragraph($"{prefix}_Body", ServiceCategoryLabel(culture, category), propertyName);
        if (status == ServiceRequestStatus.Rifiutato)
            builder.Quote("ServiceRequestRejected_ReasonLabel", rejectionReason);

        return builder.Build($"{prefix}_Subject", propertyName);
    }

    /// <summary>
    /// Invitation of a prospective supplier by a platform admin. <paramref name="comune"/> is the text shown for the
    /// comune ("Name (code)" when the name is known, else the code); the expiry is shown in Italian time (Europe/Rome).
    /// </summary>
    public static EmailContent SupplierInvite(
        CultureInfo culture,
        string email,
        string comune,
        string? message,
        string signupUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Heading("SupplierInvite_Title")
            .Paragraph("SupplierInvite_Body", comune)
            .Muted("SupplierInvite_EmailHint", email)
            .Paragraph("SupplierInvite_NextSteps")
            .Quote("SupplierInvite_MessageLabel", message)
            .Button("SupplierInvite_Cta", signupUrl)
            .LinkFallback("SupplierInvite_LinkFallback", signupUrl)
            .Muted("SupplierInvite_Expires", builder.FormatInstant(expiresAtUtc))
            .Build("SupplierInvite_Subject");
    }

    /// <summary>Tokenized self check-in link, to the guest.</summary>
    public static EmailContent GuestCheckInLink(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string checkInUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestCheckInLink_Greeting", guestName)
            .Paragraph("GuestCheckInLink_Body", propertyName, checkInDate)
            .Paragraph("GuestCheckInLink_Instructions")
            .Button("GuestCheckInLink_Cta", checkInUrl)
            .Muted("GuestCheckInLink_Validity")
            .Build("GuestCheckInLink_Subject", propertyName);

    /// <summary>Guest check-in still incomplete close to arrival, to the host.</summary>
    public static EmailContent GuestCheckInIncomplete(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestCheckInIncomplete_Body", guestName, propertyName, checkInDate)
            .Paragraph("GuestCheckInIncomplete_Action")
            .Build("GuestCheckInIncomplete_Subject", propertyName, checkInDate);

    /// <summary>Alloggiati Web report close to its deadline, to the host.</summary>
    public static EmailContent AlloggiatiDeadline(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("AlloggiatiDeadline_Body", guestName, propertyName, checkInDate)
            .Paragraph("AlloggiatiDeadline_Action")
            .Build("AlloggiatiDeadline_Subject", propertyName, checkInDate);

    /// <summary>A refund confirmed by Stripe, to the guest (BK-02). <paramref name="amountEur"/> is in euro.</summary>
    public static EmailContent GuestRefundConfirmed(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        decimal amountEur) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestRefundConfirmed_Greeting", guestName)
            .Paragraph("GuestRefundConfirmed_Body", amountEur.ToString("N2", culture), propertyName, checkInDate)
            .Paragraph("GuestRefundConfirmed_Method")
            .Muted("GuestRefundConfirmed_Timing")
            .Build("GuestRefundConfirmed_Subject", propertyName);

    /// <summary>
    /// A payment that arrived after the checkout hold had expired, when the dates were still free: the booking is
    /// confirmed again (BK-04, A3-04), to the guest. <paramref name="bookingCode"/> is what "Le mie prenotazioni" asks.
    /// </summary>
    public static EmailContent GuestLatePaymentConfirmed(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string bookingCode) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestLatePaymentConfirmed_Greeting", guestName)
            .Paragraph("GuestLatePaymentConfirmed_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestLatePaymentConfirmed_Late")
            .Muted("GuestLatePaymentConfirmed_Code", bookingCode)
            .Build("GuestLatePaymentConfirmed_Subject", propertyName);

    /// <summary>
    /// A payment that arrived when the dates were no longer available, refunded in full once Stripe confirmed the
    /// refund (BK-04, A3-04), to the guest. <paramref name="amountEur"/> is in euro.
    /// </summary>
    public static EmailContent GuestPaymentRefundedDatesUnavailable(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amountEur) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("GuestPaymentRefundedDatesUnavailable_Greeting", guestName)
            .Paragraph("GuestPaymentRefundedDatesUnavailable_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestPaymentRefundedDatesUnavailable_Refund", amountEur.ToString("N2", culture))
            .Paragraph("GuestRefundConfirmed_Method")
            .Muted("GuestRefundConfirmed_Timing")
            .Build("GuestPaymentRefundedDatesUnavailable_Subject", propertyName);

    /// <summary>
    /// The host cancelled the booking, to the guest (PC-07, A2-08). <paramref name="refundStartedEur"/> is the refund
    /// sent to Stripe, in euro, when there is one: its confirmation is <see cref="GuestRefundConfirmed"/>.
    /// </summary>
    public static EmailContent GuestBookingCancelled(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal? refundStartedEur)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("GuestBookingCancelled_Greeting", guestName)
            .Paragraph("GuestBookingCancelled_Body", propertyName, checkInDate, checkOutDate);
        if (refundStartedEur is > 0m)
            builder.Paragraph("GuestBookingCancelled_Refund", refundStartedEur.Value.ToString("N2", culture));
        return builder
            .Muted("GuestBookingCancelled_Contact")
            .Build("GuestBookingCancelled_Subject", propertyName);
    }

    /// <summary>
    /// RLI registration deadline approaching, to the landlord (LT-11, A7-26). <paramref name="contractNotSignedYet"/>:
    /// the contract is not signed by every party yet and its start date has passed, so the deadline counts from the
    /// start date (LT-04).
    /// </summary>
    public static EmailContent RliDeadlineReminder(
        CultureInfo culture,
        string propertyName,
        DateTime registrationDeadline,
        int daysRemaining,
        bool contractNotSignedYet = false)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("RliDeadlineReminder_Body", propertyName, registrationDeadline, daysRemaining);
        if (contractNotSignedYet)
            builder.Paragraph("RliDeadline_NotSignedYet");
        return builder
            .Paragraph("RliDeadline_Responsibility")
            .Build("RliDeadlineReminder_Subject", propertyName, registrationDeadline);
    }

    /// <summary>
    /// RLI registration deadline passed without a completed registration, to the landlord. See
    /// <see cref="RliDeadlineReminder"/> for <paramref name="contractNotSignedYet"/>.
    /// </summary>
    public static EmailContent RliDeadlineOverdue(
        CultureInfo culture,
        string propertyName,
        DateTime registrationDeadline,
        bool contractNotSignedYet = false)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("RliDeadlineOverdue_Body", propertyName, registrationDeadline);
        if (contractNotSignedYet)
            builder.Paragraph("RliDeadline_NotSignedYet");
        return builder
            .Paragraph("RliDeadline_Responsibility")
            .Build("RliDeadlineOverdue_Subject", propertyName);
    }

    /// <summary>
    /// "Pay at the property" request received, to the guest (BK-06, D5): the link confirms the email address and sends the
    /// request to the host; without it the request is cancelled at <paramref name="confirmByUtc"/> (shown in Italian time).
    /// </summary>
    public static EmailContent OnSiteRequestReceived(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amountEur,
        string confirmUrl,
        DateTime confirmByUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("OnSiteRequest_Greeting", guestName)
            .Paragraph("OnSiteRequestReceived_Body", propertyName, checkInDate, checkOutDate, amountEur.ToString("N2", culture))
            .Paragraph("OnSiteRequestReceived_Action", builder.FormatInstant(confirmByUtc))
            .Button("OnSiteRequestReceived_Cta", confirmUrl)
            .LinkFallback("OnSiteRequest_LinkFallback", confirmUrl)
            .Muted("OnSiteRequestReceived_NotConfirmedYet")
            .Muted("OnSiteRequestReceived_NotYou")
            .Build("OnSiteRequestReceived_Subject", propertyName);
    }

    /// <summary>
    /// New "pay at the property" request to accept or decline, to the host (BK-06, D5). The answer is due by
    /// <paramref name="answerByUtc"/> (shown in Italian time); <paramref name="requestsUrl"/> opens the console.
    /// </summary>
    public static EmailContent OnSiteRequestToHost(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        int guests,
        decimal amountEur,
        DateTime answerByUtc,
        string requestsUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph(
                "OnSiteRequestToHost_Body",
                guestName,
                propertyName,
                checkInDate,
                checkOutDate,
                guests,
                amountEur.ToString("N2", culture))
            .Paragraph("OnSiteRequestToHost_Action", builder.FormatInstant(answerByUtc))
            .Muted("OnSiteRequestToHost_NotConfirmedYet")
            .Button("OnSiteRequestToHost_Cta", requestsUrl)
            .LinkFallback("OnSiteRequest_LinkFallback", requestsUrl)
            .Build("OnSiteRequestToHost_Subject", propertyName);
    }

    /// <summary>"Pay at the property" request accepted by the host: the booking is confirmed, to the guest (BK-06, D5).</summary>
    public static EmailContent OnSiteRequestAccepted(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amountEur,
        string bookingReference) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OnSiteRequest_Greeting", guestName)
            .Paragraph("OnSiteRequestAccepted_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("OnSiteRequestAccepted_Payment", amountEur.ToString("N2", culture))
            .Muted("OnSiteRequestAccepted_Reference", bookingReference)
            .Build("OnSiteRequestAccepted_Subject", propertyName);

    /// <summary>"Pay at the property" request declined by the host, to the guest (BK-06), with the host's optional message.</summary>
    public static EmailContent OnSiteRequestDeclined(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string? hostMessage) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OnSiteRequest_Greeting", guestName)
            .Paragraph("OnSiteRequestDeclined_Body", propertyName, checkInDate, checkOutDate)
            .Quote("OnSiteRequestDeclined_MessageLabel", hostMessage)
            .Paragraph("OnSiteRequest_NoCharge")
            .Build("OnSiteRequestDeclined_Subject", propertyName);

    /// <summary>"Pay at the property" request not answered by the host in time, to the guest (BK-06).</summary>
    public static EmailContent OnSiteRequestExpired(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OnSiteRequest_Greeting", guestName)
            .Paragraph("OnSiteRequestExpired_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("OnSiteRequest_NoCharge")
            .Build("OnSiteRequestExpired_Subject", propertyName);

    /// <summary>
    /// "Paga alla scadenza": the deferred charge failed (authentication required, card declined), to the guest (BK-08,
    /// A3-14). <paramref name="payUrl"/> opens the checkout outcome page with a new checkout token, where the guest pays the
    /// same PaymentIntent. <paramref name="payByDay"/> is the last day to pay before the automatic cancellation, when one
    /// applies. <paramref name="amountEur"/> is in euro.
    /// </summary>
    public static EmailContent GuestDeferredChargeFailed(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amountEur,
        string payUrl,
        DateTime? payByDay)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("DeferredChargeGuest_Greeting", guestName)
            .Paragraph("GuestDeferredChargeFailed_Body", amountEur.ToString("N2", culture), propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestDeferredChargeFailed_Action");
        if (payByDay is { } day)
            builder.Paragraph("GuestDeferredChargeFailed_Deadline", day);

        return builder
            .Button("GuestDeferredChargeFailed_Cta", payUrl)
            .LinkFallback("GuestDeferredChargeFailed_LinkFallback", payUrl)
            .Muted("GuestDeferredChargeFailed_Personal")
            .Build("GuestDeferredChargeFailed_Subject", propertyName);
    }

    /// <summary>
    /// "Paga alla scadenza": the deferred charge failed, to the host (BK-08, A3-14). <paramref name="guestAsked"/>: the
    /// guest got the link to pay (otherwise Stripe could not attempt the charge and the host must contact the guest).
    /// <paramref name="cancelOnDay"/>: the day of the automatic cancellation, when one applies.
    /// </summary>
    public static EmailContent HostDeferredChargeFailed(
        CultureInfo culture,
        string guestFullName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amountEur,
        bool guestAsked,
        DateTime? cancelOnDay,
        string bookingUrl)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph(
                "HostDeferredChargeFailed_Body",
                amountEur.ToString("N2", culture),
                guestFullName,
                propertyName,
                checkInDate,
                checkOutDate)
            .Paragraph(guestAsked ? "HostDeferredChargeFailed_GuestAsked" : "HostDeferredChargeFailed_ContactGuest");
        if (cancelOnDay is { } day)
            builder.Paragraph("HostDeferredChargeFailed_Cancellation", day);
        else
            builder.Paragraph("HostDeferredChargeFailed_NoCancellation");

        return builder
            .Button("HostDeferredCharge_Cta", bookingUrl)
            .Build("HostDeferredChargeFailed_Subject", propertyName, checkInDate);
    }

    /// <summary>
    /// "Paga alla scadenza" not paid in time: the booking was cancelled and nothing was collected, to the guest (BK-08).
    /// </summary>
    public static EmailContent GuestDeferredChargeCancelled(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("DeferredChargeGuest_Greeting", guestName)
            .Paragraph("GuestDeferredChargeCancelled_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestDeferredChargeCancelled_NoCharge")
            .Muted("GuestDeferredChargeCancelled_Contact")
            .Build("GuestDeferredChargeCancelled_Subject", propertyName);

    /// <summary>"Paga alla scadenza" not paid in time: the booking was cancelled and its dates released, to the host (BK-08).</summary>
    public static EmailContent HostDeferredChargeCancelled(
        CultureInfo culture,
        string guestFullName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string bookingUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("HostDeferredChargeCancelled_Body", guestFullName, propertyName, checkInDate, checkOutDate)
            .Paragraph("HostDeferredChargeCancelled_Dates")
            .Button("HostDeferredCharge_Cta", bookingUrl)
            .Build("HostDeferredChargeCancelled_Subject", propertyName, checkInDate);

    /// <summary>Lease with an extra-EU tenant: check the Questura communication, to the landlord.</summary>
    public static EmailContent RliExtraEuNotice(CultureInfo culture, string propertyName) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("RliExtraEuNotice_Body", propertyName)
            .Paragraph("RliExtraEuNotice_Disclaimer")
            .Build("RliExtraEuNotice_Subject", propertyName);
}

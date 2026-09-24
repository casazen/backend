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
        public const string GuestBookingConfirmed = "guest-booking-confirmed";
        public const string HostBookingConfirmed = "host-booking-confirmed";
        public const string GuestPaymentRefundedDatesUnavailable = "guest-payment-refunded-dates-unavailable";
        public const string RliDeadlineReminder = "rli-deadline-reminder";
        public const string RliDeadlineOverdue = "rli-deadline-overdue";
        public const string RliExtraEuNotice = "rli-extra-eu-notice";
        public const string OnSiteRequestReceived = "onsite-request-received";
        public const string OnSiteRequestToHost = "onsite-request-to-host";
        public const string OnSiteRequestDeclined = "onsite-request-declined";
        public const string OnSiteRequestExpired = "onsite-request-expired";
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
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph("GuestRefundConfirmed_Body", amountEur.ToString("N2", culture), propertyName, checkInDate)
            .Paragraph("GuestRefundConfirmed_Method")
            .Muted("GuestRefundConfirmed_Timing")
            .Build("GuestRefundConfirmed_Subject", propertyName);

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
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph("GuestPaymentRefundedDatesUnavailable_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestPaymentRefundedDatesUnavailable_Refund", amountEur.ToString("N2", culture))
            .Paragraph("GuestRefundConfirmed_Method")
            .Muted("GuestRefundConfirmed_Timing")
            .Build("GuestPaymentRefundedDatesUnavailable_Subject", propertyName);

    /// <summary>
    /// The host cancelled the booking, to the guest (PC-07, A2-08; one email with the refund, BK-10).
    /// <paramref name="refundedEur"/> is what Stripe already confirmed at the cancellation: that refund gets no separate
    /// <see cref="GuestRefundConfirmed"/>. <paramref name="refundStartedEur"/> is what Stripe has not confirmed yet: its
    /// <see cref="GuestRefundConfirmed"/> follows. Amounts in euro; the host's reason is never passed here.
    /// </summary>
    public static EmailContent GuestBookingCancelled(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal refundedEur,
        decimal refundStartedEur,
        BookingHostContact hostContact)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph("GuestBookingCancelled_Body", propertyName, checkInDate, checkOutDate);
        if (refundedEur > 0m)
        {
            builder
                .Paragraph("GuestBookingCancelled_Refunded", Amount(refundedEur, culture))
                .Paragraph("GuestRefundConfirmed_Method")
                .Muted("GuestRefundConfirmed_Timing");
        }

        if (refundStartedEur > 0m)
            builder.Paragraph("GuestBookingCancelled_Refund", Amount(refundStartedEur, culture));

        return ContactHost(builder, hostContact).Build("GuestBookingCancelled_Subject", propertyName);
    }

    /// <summary>
    /// The booking is confirmed, to the guest (BK-10, A3-11, #58): booking code, stay, amounts with the tourist tax
    /// (BK-03), what was paid or how it will be paid, link to "Le mie prenotazioni" and the host's public contact.
    /// One template for every way a booking becomes confirmed (<paramref name="kind"/>): it replaces the BK-04 late
    /// payment and the BK-06 "request accepted" emails. <paramref name="paidEur"/> is what Stripe collected;
    /// <paramref name="deferredChargeDate"/> the day of the deferred charge, when known.
    /// </summary>
    public static EmailContent GuestBookingConfirmed(
        CultureInfo culture,
        string guestName,
        BookingEmailSummary booking,
        BookingConfirmationKind kind,
        decimal paidEur,
        DateTime? deferredChargeDate,
        BookingHostContact hostContact,
        string myBookingsUrl)
    {
        ArgumentNullException.ThrowIfNull(booking);
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph(
                kind == BookingConfirmationKind.OnSite ? "GuestBookingConfirmed_BodyOnSite" : "GuestBookingConfirmed_Body",
                booking.PropertyName,
                booking.CheckInDate,
                booking.CheckOutDate);
        if (kind == BookingConfirmationKind.PaidOnlineLate)
            builder.Paragraph("GuestBookingConfirmed_Late");

        builder.Paragraph("Booking_Code", booking.BookingCode).List(SummaryLines(booking, culture));
        switch (kind)
        {
            case BookingConfirmationKind.PaidOnline or BookingConfirmationKind.PaidOnlineLate:
                if (paidEur > 0m)
                    builder.Paragraph("GuestBookingConfirmed_Paid", Amount(paidEur, culture));
                break;
            case BookingConfirmationKind.DeferredCharge:
                builder.Paragraph(DeferredKey("GuestBookingConfirmed", deferredChargeDate), Amount(booking.Total, culture), deferredChargeDate);
                break;
            case BookingConfirmationKind.OnSite:
                builder.Paragraph("GuestBookingConfirmed_OnSite", Amount(booking.Total, culture));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown confirmation kind.");
        }

        builder
            .Button("Booking_MyBookingsCta", myBookingsUrl)
            .LinkFallback("Booking_LinkFallback", myBookingsUrl)
            .Muted("Booking_FindIt");
        ContactHost(builder, hostContact);
        if ((kind is BookingConfirmationKind.PaidOnline or BookingConfirmationKind.PaidOnlineLate) && paidEur > 0m)
            builder.Muted("GuestBookingConfirmed_NotFiscal");

        return builder.Build("GuestBookingConfirmed_Subject", booking.PropertyName);
    }

    /// <summary>
    /// A new booking confirmed without the host's action (payment or saved card at the checkout), to the host (BK-10,
    /// A3-11): guest name, stay, amounts, payment and a link to the booking in the console. A "pay at the property"
    /// request is accepted by the host, who is not emailed about it.
    /// </summary>
    public static EmailContent HostBookingConfirmed(
        CultureInfo culture,
        string guestFullName,
        BookingEmailSummary booking,
        BookingConfirmationKind kind,
        decimal paidEur,
        DateTime? deferredChargeDate,
        string bookingUrl)
    {
        ArgumentNullException.ThrowIfNull(booking);
        if (kind is not (BookingConfirmationKind.PaidOnline or BookingConfirmationKind.PaidOnlineLate or BookingConfirmationKind.DeferredCharge))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The host confirmed this booking: no host email.");

        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("HostBookingConfirmed_Body", guestFullName, booking.PropertyName, booking.CheckInDate, booking.CheckOutDate);
        if (kind == BookingConfirmationKind.PaidOnlineLate)
            builder.Paragraph("HostBookingConfirmed_Late");

        builder.Paragraph("Booking_Code", booking.BookingCode).List(SummaryLines(booking, culture));
        if (kind == BookingConfirmationKind.DeferredCharge)
            builder.Paragraph(DeferredKey("HostBookingConfirmed", deferredChargeDate), Amount(booking.Total, culture), deferredChargeDate);
        else if (paidEur > 0m)
            builder.Paragraph("HostBookingConfirmed_Paid", Amount(paidEur, culture));

        return builder
            .Button("HostBookingConfirmed_Cta", bookingUrl)
            .LinkFallback("Booking_LinkFallback", bookingUrl)
            .Build("HostBookingConfirmed_Subject", booking.PropertyName, booking.CheckInDate);
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
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph("OnSiteRequestReceived_Body", propertyName, checkInDate, checkOutDate, amountEur.ToString("N2", culture))
            .Paragraph("OnSiteRequestReceived_Action", builder.FormatInstant(confirmByUtc))
            .Button("OnSiteRequestReceived_Cta", confirmUrl)
            .LinkFallback("Booking_LinkFallback", confirmUrl)
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
            .LinkFallback("Booking_LinkFallback", requestsUrl)
            .Build("OnSiteRequestToHost_Subject", propertyName);
    }

    /// <summary>"Pay at the property" request declined by the host, to the guest (BK-06), with the host's optional message.</summary>
    public static EmailContent OnSiteRequestDeclined(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string? hostMessage) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("Booking_Greeting", guestName)
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
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph("OnSiteRequestExpired_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("OnSiteRequest_NoCharge")
            .Build("OnSiteRequestExpired_Subject", propertyName);

    /// <summary>Lease with an extra-EU tenant: check the Questura communication, to the landlord.</summary>
    public static EmailContent RliExtraEuNotice(CultureInfo culture, string propertyName) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("RliExtraEuNotice_Body", propertyName)
            .Paragraph("RliExtraEuNotice_Disclaimer")
            .Build("RliExtraEuNotice_Subject", propertyName);

    /// <summary>Nights, guests, lodging, cleaning and tourist tax (only when charged) and total of a booking.</summary>
    private static IEnumerable<(string Key, object?[] Args)> SummaryLines(BookingEmailSummary booking, CultureInfo culture)
    {
        yield return ("Booking_Summary_Nights", [booking.Nights]);
        yield return ("Booking_Summary_Guests", [booking.Guests]);
        yield return ("Booking_Summary_Lodging", [Amount(booking.Lodging, culture)]);
        if (booking.CleaningFee > 0m)
            yield return ("Booking_Summary_Cleaning", [Amount(booking.CleaningFee, culture)]);
        if (booking.TouristTax > 0m)
            yield return ("Booking_Summary_TouristTax", [Amount(booking.TouristTax, culture)]);
        yield return ("Booking_Summary_Total", [Amount(booking.Total, culture)]);
    }

    /// <summary>
    /// The host's contact shown on the booking site (name and email of the site footer); without an email the guest is
    /// told to contact the host of the property. Nothing else is invented.
    /// </summary>
    private static EmailHtmlBuilder ContactHost(EmailHtmlBuilder builder, BookingHostContact contact)
    {
        if (string.IsNullOrWhiteSpace(contact.Email))
            return builder.Muted("Booking_ContactHostDirectly");

        return string.IsNullOrWhiteSpace(contact.Name)
            ? builder.Muted("Booking_ContactHostEmail", contact.Email.Trim())
            : builder.Muted("Booking_ContactHost", contact.Name.Trim(), contact.Email.Trim());
    }

    private static string DeferredKey(string prefix, DateTime? chargeDate) =>
        chargeDate is null ? $"{prefix}_Deferred" : $"{prefix}_DeferredOn";

    private static string Amount(decimal amountEur, CultureInfo culture) => amountEur.ToString("N2", culture);
}

/// <summary>How a booking became confirmed (BK-10): decides the payment paragraph and whether the host is emailed.</summary>
public enum BookingConfirmationKind
{
    /// <summary>Paid online at the checkout, within the hold (<c>payment_intent.succeeded</c>).</summary>
    PaidOnline,

    /// <summary>Paid online after the hold had ended, with the dates still free: confirmed again (BK-04).</summary>
    PaidOnlineLate,

    /// <summary>Card saved at the checkout (<c>setup_intent.succeeded</c>), charged at the free-cancellation deadline.</summary>
    DeferredCharge,

    /// <summary>"Pay at the property" request accepted by the host (BK-06, D5): nothing paid online.</summary>
    OnSite,
}

/// <summary>
/// What the booking emails show of a booking (BK-10), in euro as recorded on it: <see cref="Lodging"/> is the nightly
/// part (<c>BasePrice - CleaningFee</c>), <see cref="Total"/> includes cleaning and the tourist tax (BK-03).
/// <see cref="BookingCode"/> is the code "Le mie prenotazioni" asks for (<c>Booking.BookingCode</c>, formatted, BK-11).
/// </summary>
public sealed record BookingEmailSummary(
    string BookingCode,
    string PropertyName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Guests,
    decimal Lodging,
    decimal CleaningFee,
    decimal TouristTax,
    decimal Total)
{
    public int Nights => Math.Max(0, (CheckOutDate.Date - CheckInDate.Date).Days);
}

/// <summary>
/// The host's contact the guest emails may show: the name and email that the booking site already shows publicly
/// (<c>Org.DisplayName</c>, <c>Org.ContactEmail</c>, site footer). Either may be empty.
/// </summary>
public sealed record BookingHostContact(string? Name, string? Email)
{
    public static BookingHostContact None { get; } = new(null, null);
}

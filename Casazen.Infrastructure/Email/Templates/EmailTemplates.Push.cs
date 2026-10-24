using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Push texts of the service requests, the new booking and the check-out reminder (MO-04), one title and body per kind of
/// event, Italian and English in <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys <c>Push_*</c>). Plain text shown
/// on the lock screen: property name, category and dates only, never a guest name. The Alloggiati alerts have their own
/// texts (<see cref="StayAlertPush"/>).
/// </summary>
public static partial class EmailTemplates
{
    /// <summary>
    /// New service request, to the supplier. It names the comune, not the property: before the take the supplier does not know
    /// the property (decision D9, SP-04).
    /// </summary>
    public static PushText ServiceRequestCreatedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestCreated", ServiceCategoryLabel(culture, category), comune);

    /// <summary>
    /// A new service request that was accepted automatically for a regular host (SP-04d), to the supplier: it takes the place of
    /// <see cref="ServiceRequestCreatedPush"/>. It names the comune, never the property or the host.
    /// </summary>
    public static PushText ServiceRequestAutoAcceptedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestAutoAccepted", ServiceCategoryLabel(culture, category), comune);

    /// <summary>
    /// What happened to a service request, to the host: taken, started, completed, rejected by the supplier, or cancelled by the
    /// supplier or by CasaZen (no answer in time). <c>null</c> for a status that has no push to the host (the caller then sends
    /// nothing): see <see cref="ServiceRequestStatusChanged"/>.
    /// </summary>
    public static PushText? ServiceRequestStatusPush(
        CultureInfo culture,
        ServiceRequestStatus status,
        string category,
        string propertyName,
        ServiceRequestActorParty? cancelledBy = null)
    {
        var prefix = status switch
        {
            ServiceRequestStatus.PresoInCarico => "Push_ServiceRequestTaken",
            ServiceRequestStatus.InCorso => "Push_ServiceRequestStarted",
            ServiceRequestStatus.Completato => "Push_ServiceRequestCompleted",
            ServiceRequestStatus.Rifiutato => "Push_ServiceRequestRejected",
            ServiceRequestStatus.Annullato when cancelledBy == ServiceRequestActorParty.Supplier => "Push_ServiceRequestCancelledBySupplier",
            ServiceRequestStatus.Annullato when cancelledBy == ServiceRequestActorParty.System => "Push_ServiceRequestCancelledNoResponse",
            _ => null,
        };
        return prefix is null ? null : Push(culture, prefix, ServiceCategoryLabel(culture, category), propertyName);
    }

    /// <summary>A request cancelled by the host, or by CasaZen when nobody answered in time, to the supplier (comune only).</summary>
    public static PushText ServiceRequestCancelledToSupplierPush(
        CultureInfo culture,
        string category,
        string comune,
        ServiceRequestActorParty cancelledBy) =>
        Push(
            culture,
            cancelledBy == ServiceRequestActorParty.System ? "Push_ServiceRequestExpired" : "Push_ServiceRequestCancelledByHost",
            ServiceCategoryLabel(culture, category),
            comune);

    /// <summary>The host reminds the supplier to answer (SP-04), to the supplier.</summary>
    public static PushText ServiceRequestReminderPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestReminder", ServiceCategoryLabel(culture, category), comune);

    /// <summary>The supplier proposes another time (SP-04), to the host.</summary>
    public static PushText ServiceRequestTimeProposedPush(CultureInfo culture, string category, string propertyName) =>
        Push(culture, "Push_ServiceRequestTimeProposed", ServiceCategoryLabel(culture, category), propertyName);

    /// <summary>The host accepted or declined the proposed time (SP-04), to the supplier.</summary>
    public static PushText ServiceRequestProposalAnsweredPush(CultureInfo culture, string category, string comune, bool accepted) =>
        Push(
            culture,
            accepted ? "Push_ServiceRequestProposalAccepted" : "Push_ServiceRequestProposalRejected",
            ServiceCategoryLabel(culture, category),
            comune);

    /// <summary>The customer of the public showcase cancelled its request (SP-11), to the supplier (comune only).</summary>
    public static PushText ShowcaseCancelledByCustomerPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ShowcaseCancelledByCustomer", ServiceCategoryLabel(culture, category), comune);

    /// <summary>The customer of the public showcase moved a new request to another time (SP-11), to the supplier (comune only).</summary>
    public static PushText ShowcaseRescheduledByCustomerPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ShowcaseRescheduledByCustomer", ServiceCategoryLabel(culture, category), comune);

    /// <summary>The customer of the public showcase accepted or turned down the proposed time (SP-11), to the supplier (comune only).</summary>
    public static PushText ShowcaseProposalAnsweredPush(CultureInfo culture, string category, string comune, bool accepted) =>
        Push(
            culture,
            accepted ? "Push_ShowcaseProposalAccepted" : "Push_ShowcaseProposalRejected",
            ServiceCategoryLabel(culture, category),
            comune);

    /// <summary>The customer of the public showcase did not answer the proposed time and the request was cancelled (SP-11), to the supplier.</summary>
    public static PushText ShowcaseProposalLapsedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ShowcaseProposalLapsed", ServiceCategoryLabel(culture, category), comune);

    /// <summary>Service request marked as paid by the host, to the supplier (SU-09).</summary>
    public static PushText ServiceRequestPaidPush(CultureInfo culture, string category, string propertyName) =>
        Push(culture, "Push_ServiceRequestPaid", ServiceCategoryLabel(culture, category), propertyName);

    /// <summary>A booking confirmed without the host's action (payment, saved card), to the host.</summary>
    public static PushText NewBookingPush(
        CultureInfo culture,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        int guests)
    {
        var dateFormat = EmailTexts.Get("Format_Date", culture);
        return Push(
            culture,
            "Push_NewBooking",
            propertyName,
            checkInDate.ToString(dateFormat, culture),
            checkOutDate.ToString(dateFormat, culture),
            guests.ToString(culture));
    }

    /// <summary>
    /// A message from the guest of a direct booking, to the host: the property only. Neither the guest's name nor the text of
    /// the message reach the lock screen.
    /// </summary>
    public static PushText GuestMessagePush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_GuestMessage", propertyName);

    // ─── Events of the booking, the property, the rent and the service payments (every host-facing event tells the app) ───

    private static PushText BookingDatesPush(CultureInfo culture, string prefix, string propertyName, DateTime checkInDate, DateTime checkOutDate)
    {
        var dateFormat = EmailTexts.Get("Format_Date", culture);
        return Push(culture, prefix, propertyName, checkInDate.ToString(dateFormat, culture), checkOutDate.ToString(dateFormat, culture));
    }

    /// <summary>A pay-at-the-property request waits for the host: property, dates and guests, no guest name.</summary>
    public static PushText BookingRequestPush(CultureInfo culture, string propertyName, DateTime checkInDate, DateTime checkOutDate, int guests)
    {
        var dateFormat = EmailTexts.Get("Format_Date", culture);
        return Push(
            culture,
            "Push_BookingRequest",
            propertyName,
            checkInDate.ToString(dateFormat, culture),
            checkOutDate.ToString(dateFormat, culture),
            guests.ToString(culture));
    }

    /// <summary>The guest cancelled: property and dates only.</summary>
    public static PushText BookingCancelledByGuestPush(CultureInfo culture, string propertyName, DateTime checkInDate, DateTime checkOutDate) =>
        BookingDatesPush(culture, "Push_BookingCancelledByGuest", propertyName, checkInDate, checkOutDate);

    /// <summary>The deferred charge failed or was not attempted: property and dates only.</summary>
    public static PushText DeferredChargeFailedPush(CultureInfo culture, string propertyName, DateTime checkInDate, DateTime checkOutDate) =>
        BookingDatesPush(culture, "Push_DeferredChargeFailed", propertyName, checkInDate, checkOutDate);

    /// <summary>The system cancelled the unpaid booking: property and dates only.</summary>
    public static PushText DeferredChargeCancelledPush(CultureInfo culture, string propertyName, DateTime checkInDate, DateTime checkOutDate) =>
        BookingDatesPush(culture, "Push_DeferredChargeCancelled", propertyName, checkInDate, checkOutDate);

    /// <summary>A change of rental mode (<c>Scheduled</c>, <c>Applied</c> or <c>Failed</c> as <paramref name="stage"/>): the property only.</summary>
    public static PushText PropertyModeChangePush(CultureInfo culture, string stage, string propertyName) =>
        Push(culture, $"Push_PropertyModeChange{stage}", propertyName);

    /// <summary>The property was suspended for compliance: the property only.</summary>
    public static PushText PropertyComplianceSuspendedPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_PropertyComplianceSuspended", propertyName);

    /// <summary>The CIN deadline: the property and the date; without a deadline, a reminder with the property only.</summary>
    public static PushText CinDeadlinePush(CultureInfo culture, string propertyName, DateOnly? deadline) =>
        deadline is { } date
            ? Push(culture, "Push_CinDeadline", propertyName, date.ToString(EmailTexts.Get("Format_Date", culture), culture))
            : Push(culture, "Push_CinReminder", propertyName);

    /// <summary>A rent installment was paid: the property only (no tenant, no amount).</summary>
    public static PushText RentPaymentReceivedPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_RentPaymentReceived", propertyName);

    /// <summary>
    /// A push about the payment of a service (<paramref name="prefix"/> one of <c>Push_ServicePaymentRequested</c>, <c>…Failed</c>,
    /// <c>…Received</c>, <c>…Refunded</c>, <c>…OfflineRecorded</c>): the property only, no supplier, no amount.
    /// </summary>
    public static PushText ServicePaymentPush(CultureInfo culture, string prefix, string propertyName) =>
        Push(culture, prefix, propertyName);

    /// <summary>Check-out day of a stay, to the host (CO-10 stay alert).</summary>
    public static PushText CheckoutReminderPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_CheckoutReminder", propertyName);

    private static PushText Push(CultureInfo culture, string prefix, params object[] values) =>
        new(
            EmailTexts.Get($"{prefix}_Title", culture),
            string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), values));
}

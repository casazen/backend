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

    /// <summary>Check-out day of a stay, to the host (CO-10 stay alert).</summary>
    public static PushText CheckoutReminderPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_CheckoutReminder", propertyName);

    private static PushText Push(CultureInfo culture, string prefix, params object[] values) =>
        new(
            EmailTexts.Get($"{prefix}_Title", culture),
            string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), values));
}

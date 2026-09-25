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
    /// <summary>New service request, to the supplier.</summary>
    public static PushText ServiceRequestCreatedPush(CultureInfo culture, string category, string propertyName) =>
        Push(culture, "Push_ServiceRequestCreated", ServiceCategoryLabel(culture, category), propertyName);

    /// <summary>Service request taken, completed or rejected by the supplier, to the host.</summary>
    public static PushText ServiceRequestStatusPush(
        CultureInfo culture,
        ServiceRequestStatus status,
        string category,
        string propertyName)
    {
        var prefix = status switch
        {
            ServiceRequestStatus.PresoInCarico => "Push_ServiceRequestTaken",
            ServiceRequestStatus.Completato => "Push_ServiceRequestCompleted",
            ServiceRequestStatus.Rifiutato => "Push_ServiceRequestRejected",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No push for this service request status."),
        };
        return Push(culture, prefix, ServiceCategoryLabel(culture, category), propertyName);
    }

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

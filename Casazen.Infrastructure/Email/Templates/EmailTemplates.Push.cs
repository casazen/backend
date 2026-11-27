using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Push texts of the service requests, the new booking and the check-out reminder (MO-04), one title and body per kind of
/// event, Italian and English in <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys <c>Push_*</c>). Plain text shown
/// on the lock screen: property name, category and dates only, never a guest name. The Alloggiati alerts have their own
/// texts (<see cref="StayAlertPush"/>).
/// </summary>
public static partial class EmailTemplates
{
    public static PushText BookingFlowStepPush(CultureInfo culture, Casazen.Core.Services.BookingFlowAutomaticStep step, string propertyName) =>
        Push(culture, $"Push_BookingFlow{step}", propertyName);

    public static PushText ServiceRequestCreatedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestCreated", ServiceCategoryLabel(culture, category), comune);

    public static PushText ServiceRequestAutoAcceptedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestAutoAccepted", ServiceCategoryLabel(culture, category), comune);

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

    public static PushText ServiceRequestReminderPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestReminder", ServiceCategoryLabel(culture, category), comune);

    public static PushText ServiceRequestTimeProposedPush(CultureInfo culture, string category, string propertyName) =>
        Push(culture, "Push_ServiceRequestTimeProposed", ServiceCategoryLabel(culture, category), propertyName);

    public static PushText ServiceRequestProposalAnsweredPush(CultureInfo culture, string category, string comune, bool accepted) =>
        Push(
            culture,
            accepted ? "Push_ServiceRequestProposalAccepted" : "Push_ServiceRequestProposalRejected",
            ServiceCategoryLabel(culture, category),
            comune);

    public static PushText ServiceRequestQuoteSentPush(CultureInfo culture, string category, string propertyName) =>
        Push(culture, "Push_ServiceRequestQuoteSent", ServiceCategoryLabel(culture, category), propertyName);

    public static PushText ServiceRequestQuoteAnsweredPush(CultureInfo culture, string category, string comune, bool accepted) =>
        Push(
            culture,
            accepted ? "Push_ServiceRequestQuoteAccepted" : "Push_ServiceRequestQuoteRejected",
            ServiceCategoryLabel(culture, category),
            comune);

    public static PushText ServiceRequestQuoteExpiredPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ServiceRequestQuoteExpired", ServiceCategoryLabel(culture, category), comune);

    public static PushText ShowcaseCancelledByCustomerPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ShowcaseCancelledByCustomer", ServiceCategoryLabel(culture, category), comune);

    public static PushText ShowcaseRescheduledByCustomerPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ShowcaseRescheduledByCustomer", ServiceCategoryLabel(culture, category), comune);

    public static PushText ShowcaseProposalAnsweredPush(CultureInfo culture, string category, string comune, bool accepted) =>
        Push(
            culture,
            accepted ? "Push_ShowcaseProposalAccepted" : "Push_ShowcaseProposalRejected",
            ServiceCategoryLabel(culture, category),
            comune);

    public static PushText ShowcaseProposalLapsedPush(CultureInfo culture, string category, string comune) =>
        Push(culture, "Push_ShowcaseProposalLapsed", ServiceCategoryLabel(culture, category), comune);

    public static PushText ShowcaseReplyReminderPush(CultureInfo culture, string category, string comune, DateTime respondByUtc, bool lastCall) =>
        Push(
            culture,
            lastCall ? "Push_ShowcaseReplyLastCall" : "Push_ShowcaseReplyReminder",
            ServiceCategoryLabel(culture, category),
            comune,
            RomeCalendar.ToRome(respondByUtc).ToString("HH:mm", culture));

    public static PushText ServiceRequestPaidPush(CultureInfo culture, string category, string propertyName) =>
        Push(culture, "Push_ServiceRequestPaid", ServiceCategoryLabel(culture, category), propertyName);

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
    /// A booking that arrived from a channel (E08/F06/US01/T02), to the host: the same copy as <see cref="NewBookingPush"/> with the
    /// name of the channel in the title ("Nuova prenotazione da Airbnb"). Property, dates and number of guests only, never the guest's name.
    /// </summary>
    public static PushText ChannelBookingPush(
        CultureInfo culture,
        string propertyName,
        string channelName,
        DateTime checkInDate,
        DateTime checkOutDate,
        int guests)
    {
        var dateFormat = EmailTexts.Get("Format_Date", culture);
        return new PushText(
            string.Format(culture, EmailTexts.Get("Push_ChannelBooking_Title", culture), channelName),
            string.Format(
                culture,
                EmailTexts.Get("Push_ChannelBooking_Body", culture),
                propertyName,
                checkInDate.ToString(dateFormat, culture),
                checkOutDate.ToString(dateFormat, culture),
                guests.ToString(culture)));
    }

    public static PushText GuestMessagePush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_GuestMessage", propertyName);

    public static PushText GuestCheckInCompletedPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_GuestCheckInCompleted", propertyName);

    public static PushText CheckInLinkFailedPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_CheckInLinkFailed", propertyName);

    public static PushText OtaReviewReceivedPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_OtaReviewReceived", propertyName);

    public static PushText OtaReviewActionFailedPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_OtaReviewActionFailed", propertyName);

    public static PushText CheckoutReminderPush(CultureInfo culture, string propertyName) =>
        Push(culture, "Push_CheckoutReminder", propertyName);

    public static PushText AssistVerifiedPush(CultureInfo culture) => Push(culture, "Push_AssistVerified");

    public static PushText AssistDeclaredPush(CultureInfo culture) => Push(culture, "Push_AssistDeclared");

    public static PushText DomainVerifiedPush(CultureInfo culture) => Push(culture, "Push_DomainVerified");

    public static PushText OwnerLoginReadyPush(CultureInfo culture) => Push(culture, "Push_OwnerLoginReady");

    /// <summary>The owner sign-in on the org's domain could not be enabled (E40/F01/US02/T01, NF-141), to the administrators of the org: they can retry. No domain name.</summary>
    public static PushText OwnerLoginFailedPush(CultureInfo culture) => Push(culture, "Push_OwnerLoginFailed");

    private static PushText Push(CultureInfo culture, string prefix, params object[] values) =>
        new(
            EmailTexts.Get($"{prefix}_Title", culture),
            string.Format(culture, EmailTexts.Get($"{prefix}_Body", culture), values));
}

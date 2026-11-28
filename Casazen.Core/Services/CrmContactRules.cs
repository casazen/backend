using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who may be written to, on which channel and why (E07/F01/US02/T02, NF-89, CRM-02): the only place of these rules. The contact service
/// stores the facts and asks these functions what they allow; the segments, the audiences and the campaigns ask them too, so there is one answer.
/// Pure: no clock, no database.
/// </summary>
/// <remarks>
/// The legal bases are ordered by strength, <see cref="GuestContactLegalBasis.None"/> &lt; <see cref="GuestContactLegalBasis.ServiceOnly"/> &lt;
/// <see cref="GuestContactLegalBasis.SoftOptIn"/> &lt; <see cref="GuestContactLegalBasis.Consent"/> (the numbers of the enum): a weaker basis
/// never overwrites a stronger one (<see cref="Stronger"/>). A suppression is never cleared by anything here.
/// </remarks>
public static class CrmContactRules
{
    public const string ReasonSuppressed = "suppressed";

    public const string ReasonOtaForward = "ota_forward";

    public const string ReasonNotConfirmed = "not_confirmed";

    public const string ReasonNoBasis = "no_basis";

    public const string ReasonChannelNotAvailable = "channel_not_available";

    /// <summary>
    /// <c>SuppressedSource</c> of a contact flagged because the address was already on the suppression list when the contact was made or read
    /// again: the list does not say how the person opted out to the code that reads it, only that they did.
    /// </summary>
    public const string SuppressionSourceList = "suppression_list";

    /// <summary>
    /// The basis a contact has by where it came from (CRM-02, PO-07). The origin decides, the caller only reports what it saw:
    /// <list type="bullet">
    /// <item><see cref="GuestContactOrigin.OtaForward"/> and <see cref="GuestContactOrigin.Manual"/>: service only.</item>
    /// <item><see cref="GuestContactOrigin.DirectBooking"/>: soft opt-in when the soft opt-in notice was recorded for the guest
    /// (<paramref name="noticeShown"/>) and the booking is a purchase (<paramref name="purchaseConfirmed"/>), else service only.</item>
    /// <item><see cref="GuestContactOrigin.OctorateContactInfo"/>: service only, and soft opt-in in the same case as a direct booking (the customer of
    /// the booking engine of the agency, only while <c>Crm:BookingEngineSoftOptIn</c> is on, which the contact service checks).</item>
    /// <item><see cref="GuestContactOrigin.CheckIn"/> and <see cref="GuestContactOrigin.GuestPage"/>: service only; they are a consent only
    /// through <c>RecordConsentAsync</c>.</item>
    /// <item><see cref="GuestContactOrigin.WelcomePage"/>: none, a person who left an address with no consent has given no basis; a consent only
    /// through <c>RecordConsentAsync</c>.</item>
    /// </list>
    /// Soft opt-in is for e-mail (art. 130 c. 4 Codice privacy): on another channel it is service only.
    /// </summary>
    public static GuestContactLegalBasis ResolveBasis(
        GuestContactOrigin origin,
        bool noticeShown,
        bool purchaseConfirmed,
        GuestContactChannel channel = GuestContactChannel.Email)
    {
        var basis = origin switch
        {
            GuestContactOrigin.OtaForward => GuestContactLegalBasis.ServiceOnly,
            GuestContactOrigin.Manual => GuestContactLegalBasis.ServiceOnly,
            GuestContactOrigin.OctorateContactInfo or GuestContactOrigin.DirectBooking =>
                noticeShown && purchaseConfirmed ? GuestContactLegalBasis.SoftOptIn : GuestContactLegalBasis.ServiceOnly,
            GuestContactOrigin.CheckIn or GuestContactOrigin.GuestPage => GuestContactLegalBasis.ServiceOnly,
            _ => GuestContactLegalBasis.None,
        };

        return basis == GuestContactLegalBasis.SoftOptIn && channel != GuestContactChannel.Email
            ? GuestContactLegalBasis.ServiceOnly
            : basis;
    }

    public static GuestContactLegalBasis Stronger(GuestContactLegalBasis first, GuestContactLegalBasis second) =>
        first >= second ? first : second;

    public static bool IsPurchase(BookingStatus status) =>
        status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut;

    /// <summary>
    /// The origin of a contact built from the guest record of a booking: an e-mail address that is an OTA forward (<paramref name="isForward"/>,
    /// see <c>OtaForwardAddress</c>) is <see cref="GuestContactOrigin.OtaForward"/>; otherwise a direct booking is
    /// <see cref="GuestContactOrigin.DirectBooking"/> and anything else (entered by the host, a block of a calendar, a guest with no booking) is
    /// <see cref="GuestContactOrigin.Manual"/>. Only an e-mail address is a forward: the phone number of an OTA guest is a number like another.
    /// </summary>
    public static GuestContactOrigin OriginOfBooking(BookingSource? source, bool isForward, GuestContactChannel channel)
    {
        if (channel == GuestContactChannel.Email && isForward)
            return GuestContactOrigin.OtaForward;

        return source == BookingSource.Direct ? GuestContactOrigin.DirectBooking : GuestContactOrigin.Manual;
    }

    public static readonly Expression<Func<GuestContact, bool>> MarketableEmail = contact =>
        contact.Channel == GuestContactChannel.Email
        && !contact.IsSuppressed
        && contact.Origin != GuestContactOrigin.OtaForward
        && (contact.Origin != GuestContactOrigin.Imported || contact.Confirmation == GuestContactConfirmation.Confirmed)
        && (contact.LegalBasis == GuestContactLegalBasis.SoftOptIn
            || (contact.LegalBasis == GuestContactLegalBasis.Consent && contact.Confirmation == GuestContactConfirmation.Confirmed));

    public static bool CanReceiveMarketing(GuestContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        if (contact.IsSuppressed)
            return false;

        return contact.Channel switch
        {
            GuestContactChannel.Email =>
                contact.Origin != GuestContactOrigin.OtaForward
                && (contact.Origin != GuestContactOrigin.Imported || contact.Confirmation == GuestContactConfirmation.Confirmed)
                && (contact.LegalBasis == GuestContactLegalBasis.SoftOptIn
                    || (contact.LegalBasis == GuestContactLegalBasis.Consent && contact.Confirmation == GuestContactConfirmation.Confirmed)),
            _ => contact.LegalBasis == GuestContactLegalBasis.Consent,
        };
    }

    public static bool CanReceiveService(GuestContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return contact.LegalBasis != GuestContactLegalBasis.None;
    }

    public static bool HasProvider(GuestContactChannel channel) => channel == GuestContactChannel.Email;

    public static string? ReachabilityReason(GuestContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        if (contact.IsSuppressed)
            return ReasonSuppressed;

        if (contact.Channel == GuestContactChannel.Email && contact.Origin == GuestContactOrigin.OtaForward)
            return ReasonOtaForward;

        if (!CanReceiveMarketing(contact))
        {
            return contact.Channel == GuestContactChannel.Email && (contact.LegalBasis == GuestContactLegalBasis.Consent
                || (contact.Origin == GuestContactOrigin.Imported && contact.LegalBasis == GuestContactLegalBasis.SoftOptIn))
                ? ReasonNotConfirmed
                : ReasonNoBasis;
        }

        return HasProvider(contact.Channel) ? null : ReasonChannelNotAvailable;
    }
}

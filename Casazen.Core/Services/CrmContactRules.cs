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
    /// <summary>Reachability reason: the person asked not to be written to on this channel.</summary>
    public const string ReasonSuppressed = "suppressed";

    /// <summary>Reachability reason: the address is one an online travel agency forwards the guest's mail through.</summary>
    public const string ReasonOtaForward = "ota_forward";

    /// <summary>Reachability reason: the e-mail address has the person's consent but the double opt-in is not done.</summary>
    public const string ReasonNotConfirmed = "not_confirmed";

    /// <summary>Reachability reason: no basis allows marketing on this channel (service only, or none).</summary>
    public const string ReasonNoBasis = "no_basis";

    /// <summary>Reachability reason: the person can be written to on this channel but nothing sends on it yet (WhatsApp and phone).</summary>
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

    /// <summary>The stronger of two bases: <see cref="GuestContactLegalBasis.Consent"/> over soft opt-in over service only over none.</summary>
    public static GuestContactLegalBasis Stronger(GuestContactLegalBasis first, GuestContactLegalBasis second) =>
        first >= second ? first : second;

    /// <summary>True for a booking that is a purchase: confirmed, checked in or checked out. A pending or cancelled one is not.</summary>
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

    /// <summary>
    /// Whether the contact may receive marketing, by the law and not by what is installed: an e-mail address needs to be not suppressed, not an OTA
    /// forward and either soft opt-in or a consent that was confirmed (double opt-in); a WhatsApp or phone number needs to be not suppressed and
    /// have the person's consent.
    /// </summary>
    public static bool CanReceiveMarketing(GuestContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        if (contact.IsSuppressed)
            return false;

        return contact.Channel switch
        {
            GuestContactChannel.Email =>
                contact.Origin != GuestContactOrigin.OtaForward
                && (contact.LegalBasis == GuestContactLegalBasis.SoftOptIn
                    || (contact.LegalBasis == GuestContactLegalBasis.Consent && contact.Confirmation == GuestContactConfirmation.Confirmed)),
            _ => contact.LegalBasis == GuestContactLegalBasis.Consent,
        };
    }

    /// <summary>
    /// Whether the contact may receive the messages of a stay (check-in, instructions, invoice): any basis other than none. A marketing opt-out
    /// does not stop them.
    /// </summary>
    public static bool CanReceiveService(GuestContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return contact.LegalBasis != GuestContactLegalBasis.None;
    }

    /// <summary>True when something sends on the channel: only e-mail, while no WhatsApp or phone provider exists.</summary>
    public static bool HasProvider(GuestContactChannel channel) => channel == GuestContactChannel.Email;

    /// <summary>
    /// Why the contact cannot be written to for marketing, as a code (<see cref="ReasonSuppressed"/>, <see cref="ReasonOtaForward"/>,
    /// <see cref="ReasonNotConfirmed"/>, <see cref="ReasonNoBasis"/>, <see cref="ReasonChannelNotAvailable"/>), or <c>null</c> when it can. The
    /// first reason that applies wins, in that order of the person's wishes and of the law before what is installed.
    /// </summary>
    public static string? ReachabilityReason(GuestContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        if (contact.IsSuppressed)
            return ReasonSuppressed;

        if (contact.Channel == GuestContactChannel.Email && contact.Origin == GuestContactOrigin.OtaForward)
            return ReasonOtaForward;

        if (!CanReceiveMarketing(contact))
        {
            return contact.Channel == GuestContactChannel.Email && contact.LegalBasis == GuestContactLegalBasis.Consent
                ? ReasonNotConfirmed
                : ReasonNoBasis;
        }

        return HasProvider(contact.Channel) ? null : ReasonChannelNotAvailable;
    }
}

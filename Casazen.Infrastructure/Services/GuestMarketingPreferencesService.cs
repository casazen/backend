using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestMarketingPreferencesService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which contacts are the guest's</b>: the contacts of the org on the channel that the guest's record of the booking gave
/// (<see cref="GuestContact.GuestId"/>) or that have the keyed hash of the address or of the phone number of the booking. Of those, the one the
/// guest acted on counts first: an active consent, then an active soft opt-in, then an opt-out, then service only.</item>
/// <item><b>Validation before writing</b>: the version of the consent text, the address and the number are checked for the whole request before
/// anything is stored, so a request that fails changes nothing.</item>
/// <item><b>Idempotent</b>: the same consent again, on the same text, adds nothing to the privacy history and sends no second e-mail unless none
/// was ever sent; a repeat is the job of <see cref="ResendConfirmationAsync"/>, with its limits.</item>
/// <item><b>Final opt-out</b>: turning a channel off suppresses every open contact of the guest on it (source <c>unsubscribe</c>, consent source
/// <see cref="GuestConsentSource.GuestPage"/>); an e-mail address that is no contact yet is made one and suppressed, so a notice or a booking that
/// comes later cannot make it reachable. Turning it on again stores the consent and lifts nothing.</item>
/// </list>
/// </remarks>
public class GuestMarketingPreferencesService(
    IGuestSessionService sessions,
    AppDbContext db,
    ICrmContactService contacts,
    ICrmContactIndex contactIndex,
    IMarketingConfirmationService confirmation,
    IOptions<GdprOptions> gdprOptions,
    IOptions<CrmOptions> crmOptions,
    TimeProvider timeProvider,
    ILogger<GuestMarketingPreferencesService> logger) : IGuestMarketingPreferencesService
{
    /// <summary>How an opt-out of the guest page is written on the contact and on the suppression list (the person unsubscribed).</summary>
    private const string WithdrawalSource = "unsubscribe";

    public async Task<GuestMarketingPreferences> GetAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        CancellationToken cancellationToken = default)
    {
        var booking = await sessions.FindBookingAsync(ticket, bookingCode, cancellationToken);
        return await ViewAsync(booking, cancellationToken);
    }

    public async Task<GuestMarketingPreferences> SetAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        GuestMarketingChange change,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        // The uniform 404 comes first: nothing in the body can tell a booking of another guest from one that does not exist.
        var booking = await sessions.FindBookingAsync(ticket, bookingCode, cancellationToken);

        var enableEmail = change.Email is { Enabled: true };
        var enableWhatsApp = change.WhatsApp is { Enabled: true };
        var disableEmail = change.Email is { Enabled: false };
        var disableWhatsApp = change.WhatsApp is { Enabled: false };
        if (!enableEmail && !enableWhatsApp && !disableEmail && !disableWhatsApp)
            return await ViewAsync(booking, cancellationToken);

        // A consent needs the text it rests on, and the version the page showed must be the one in force. Withdrawing needs neither.
        var version = GdprOptions.Normalize(gdprOptions.Value.MarketingConsentVersion);
        if (enableEmail || enableWhatsApp)
        {
            if (version is null)
                throw new DomainRuleException(GuestMarketingErrorCodes.ConsentUnavailable, "GuestMarketingConsentUnavailable");

            if (!string.Equals(GdprOptions.Normalize(change.ConsentVersion), version, StringComparison.Ordinal))
                throw new DomainConflictException(GuestMarketingErrorCodes.ConsentOutdated, "GuestMarketingConsentOutdated");
        }

        // Without the key of the contact index nothing can be stored or suppressed: say so instead of pretending the choice was kept.
        if (!contactIndex.IsConfigured)
            throw new DomainRuleException(GuestMarketingErrorCodes.ConsentUnavailable, "GuestMarketingConsentUnavailable");

        // Everything the request needs is worked out and checked before the first write.
        var address = enableEmail ? ResolveAddress(booking, change.Email?.Address) : null;
        var phone = enableWhatsApp ? ResolvePhone(booking, change.WhatsApp?.Phone) : null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var language = LanguageOf(booking, change.Language);
        var displayName = $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim();

        // Withdrawing first: a request that turns a channel off and on again ends with the consent stored on a contact that stays suppressed.
        if (disableEmail)
            await WithdrawAsync(booking, GuestContactChannel.Email, displayName, language, cancellationToken);

        if (disableWhatsApp)
            await WithdrawAsync(booking, GuestContactChannel.WhatsApp, displayName, language, cancellationToken);

        if (address is not null)
            await ConsentAsync(booking, GuestContactChannel.Email, address, version!, ipAddress, now, language, displayName, cancellationToken);

        if (phone is not null)
            await ConsentAsync(booking, GuestContactChannel.WhatsApp, phone, version!, ipAddress, now, language, displayName, cancellationToken);

        logger.LogInformation("The guest of booking {BookingId} changed the marketing choices of the guest page", booking.Id);
        return await ViewAsync(booking, cancellationToken);
    }

    public async Task<MarketingConfirmationSendOutcome> ResendConfirmationAsync(
        GuestSessionTicket ticket,
        string bookingCode,
        CancellationToken cancellationToken = default)
    {
        var booking = await sessions.FindBookingAsync(ticket, bookingCode, cancellationToken);
        var channels = await ReadAsync(booking, cancellationToken);
        var pending = channels.Email
            .Where(contact => StateOf(contact) == GuestMarketingStates.Pending)
            .OrderByDescending(contact => contact.UpdatedAt)
            .FirstOrDefault();

        return pending is null
            ? MarketingConfirmationSendOutcome.NotRequired
            : await confirmation.SendConfirmationAsync(booking.OrgId, pending.Id, cancellationToken);
    }

    // ---- reading ------------------------------------------------------------------------------------------------------------------

    private async Task<GuestMarketingPreferences> ViewAsync(Booking booking, CancellationToken cancellationToken)
    {
        var channels = await ReadAsync(booking, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var email = Pick(channels.Email);
        var emailState = StateOf(email);
        var maskedAddress = email is not null
            ? MarketingContactMasks.Email(email.Value)
            : channels.BookingAddressIsReal && channels.BookingAddress is { } bookingAddress ? MarketingContactMasks.Email(bookingAddress) : null;
        var canResend = email is not null
            && emailState == GuestMarketingStates.Pending
            && email.ConfirmationSentCount < crmOptions.Value.ConfirmationMaxSends
            && (email.ConfirmationSentAt is not { } sentAt || now - sentAt >= MarketingConfirmationService.MinimumInterval);

        var whatsApp = Pick(channels.WhatsApp);
        var maskedPhone = whatsApp is not null
            ? MarketingContactMasks.Phone(whatsApp.Value)
            : channels.BookingPhone is { } bookingPhone ? MarketingContactMasks.Phone(bookingPhone) : null;

        return new GuestMarketingPreferences(
            GdprOptions.Normalize(gdprOptions.Value.MarketingConsentVersion),
            GdprOptions.Normalize(gdprOptions.Value.SoftOptInNoticeVersion),
            new GuestMarketingEmailChoice(emailState, maskedAddress, canResend),
            new GuestMarketingWhatsAppChoice(StateOf(whatsApp), maskedPhone));
    }

    /// <summary>
    /// The contacts of the guest of the booking, per channel: the ones given by the guest's record of the booking and the ones with the keyed
    /// hash of its address or of its phone number. Every query names the org of the booking.
    /// </summary>
    private async Task<GuestChannels> ReadAsync(Booking booking, CancellationToken cancellationToken)
    {
        var guest = booking.Guest;
        var settings = crmOptions.Value;
        var address = CrmNormalizer.TryNormalizeEmail(guest.Email, out var normalizedAddress) ? normalizedAddress : null;
        var addressIsReal = address is not null && !OtaForwardAddress.IsForward(address, booking.Source, settings.OtaForwardDomains);
        var phone = CrmNormalizer.NormalizePhone(guest.PhoneNumber, settings.DefaultCallingCode);

        List<GuestContact> email = [];
        List<GuestContact> whatsApp = [];
        if (contactIndex.IsConfigured)
        {
            var orgId = booking.OrgId;
            var guestId = guest.Id;
            var addressHash = address is null ? null : contactIndex.Hash(address);
            var phoneHash = phone is null ? null : contactIndex.Hash(phone);
            var found = await db.GuestContacts.AsNoTracking()
                .Where(c => c.OrgId == orgId
                    && ((c.Channel == GuestContactChannel.Email && (c.GuestId == guestId || (addressHash != null && c.ValueHash == addressHash)))
                        || (c.Channel == GuestContactChannel.WhatsApp && (c.GuestId == guestId || (phoneHash != null && c.ValueHash == phoneHash)))))
                .ToListAsync(cancellationToken);
            email = found.Where(c => c.Channel == GuestContactChannel.Email).ToList();
            whatsApp = found.Where(c => c.Channel == GuestContactChannel.WhatsApp).ToList();
        }

        return new GuestChannels(email, whatsApp, address, addressIsReal, phone);
    }

    /// <summary>
    /// The contact the guest acted on: an active consent first, then an active soft opt-in, then an opt-out (a suppressed contact, whatever it
    /// holds), then the rest (service only); the latest changed among equals.
    /// </summary>
    private static GuestContact? Pick(IReadOnlyList<GuestContact> candidates) =>
        candidates.OrderBy(Rank).ThenByDescending(contact => contact.UpdatedAt).FirstOrDefault();

    private static int Rank(GuestContact contact)
    {
        if (contact.IsSuppressed)
            return 2;

        return contact.LegalBasis switch
        {
            GuestContactLegalBasis.Consent => 0,
            GuestContactLegalBasis.SoftOptIn => 1,
            _ => 3,
        };
    }

    /// <summary>The state of a channel (<see cref="GuestMarketingStates"/>): an opt-out wins over everything, then the basis the contact holds.</summary>
    private static string StateOf(GuestContact? contact)
    {
        if (contact is null)
            return GuestMarketingStates.None;

        if (contact.IsSuppressed)
            return GuestMarketingStates.Withdrawn;

        return contact.LegalBasis switch
        {
            GuestContactLegalBasis.Consent => contact.Channel == GuestContactChannel.Email && contact.Confirmation != GuestContactConfirmation.Confirmed
                ? GuestMarketingStates.Pending
                : GuestMarketingStates.Confirmed,
            GuestContactLegalBasis.SoftOptIn => GuestMarketingStates.SoftOptIn,
            GuestContactLegalBasis.ServiceOnly => GuestMarketingStates.ServiceOnly,
            _ => GuestMarketingStates.None,
        };
    }

    // ---- writing ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The address to consent with: the one the guest typed, which must be a plain address that is not a relay of an OTA, else the address of the
    /// booking when it is a real one. An OTA forward, or no address, has to be typed.
    /// </summary>
    private string ResolveAddress(Booking booking, string? typed)
    {
        var settings = crmOptions.Value;
        if (!string.IsNullOrWhiteSpace(typed))
        {
            if (!CrmNormalizer.TryNormalizeEmail(typed, out var address) || OtaForwardAddress.IsOnRelayDomain(address, settings.OtaForwardDomains))
                throw new DomainRuleException(GuestMarketingErrorCodes.EmailInvalid, "GuestMarketingEmailInvalid");

            return address;
        }

        if (CrmNormalizer.TryNormalizeEmail(booking.Guest.Email, out var bookingAddress)
            && !OtaForwardAddress.IsForward(bookingAddress, booking.Source, settings.OtaForwardDomains))
        {
            return bookingAddress;
        }

        throw new DomainRuleException(GuestMarketingErrorCodes.EmailInvalid, "GuestMarketingEmailInvalid");
    }

    /// <summary>The number to consent with: the one the guest typed, else the phone of the booking; it must be a number the CRM can store.</summary>
    private string ResolvePhone(Booking booking, string? typed)
    {
        var source = string.IsNullOrWhiteSpace(typed) ? booking.Guest.PhoneNumber : typed;
        return CrmNormalizer.NormalizePhone(source, crmOptions.Value.DefaultCallingCode)
            ?? throw new DomainRuleException(GuestMarketingErrorCodes.PhoneInvalid, "GuestMarketingPhoneInvalid");
    }

    /// <summary>
    /// The language of the contact: the one of the page (<c>it</c> or <c>en</c>), else the default language of the org of the booking.
    /// </summary>
    private static string LanguageOf(Booking booking, string? requested)
    {
        var language = requested?.Trim().ToLowerInvariant();
        if (language is "it" or "en")
            return language;

        return string.Equals(booking.Org?.DefaultLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
    }

    /// <summary>
    /// Records the consent of the guest on a channel as a contact (origin and source <c>GuestPage</c>, the version, the IP, the time, the
    /// language and the guest) and, for e-mail, queues the double opt-in message. The same consent again, on the same text, is left as it is.
    /// </summary>
    private async Task ConsentAsync(
        Booking booking,
        GuestContactChannel channel,
        string value,
        string version,
        string? ipAddress,
        DateTime now,
        string language,
        string displayName,
        CancellationToken cancellationToken)
    {
        var channels = await ReadAsync(booking, cancellationToken);
        var hash = contactIndex.Hash(value);
        var existing = (channel == GuestContactChannel.Email ? channels.Email : channels.WhatsApp).FirstOrDefault(c => c.ValueHash == hash);

        GuestContact? contact;
        var repeated = existing is { IsSuppressed: false, LegalBasis: GuestContactLegalBasis.Consent }
            && string.Equals(existing.BasisVersion, version, StringComparison.Ordinal);
        if (repeated)
        {
            contact = existing;
        }
        else
        {
            contact = await contacts.RecordConsentAsync(
                new CrmConsentInput(
                    booking.OrgId,
                    channel,
                    value,
                    GuestContactOrigin.GuestPage,
                    version,
                    GuestConsentSource.GuestPage,
                    ipAddress,
                    now,
                    language,
                    displayName,
                    booking.Guest.GuestCrmProfileId,
                    booking.Guest.Id,
                    booking.Source),
                cancellationToken);
        }

        // A consent that was already there is mailed again only if no message was ever sent for it: the rest is the job of the resend.
        var send = !repeated || (existing!.ConfirmationSentCount == 0 && existing.Confirmation != GuestContactConfirmation.Confirmed);
        if (channel == GuestContactChannel.Email && contact is not null && send)
            await confirmation.SendConfirmationAsync(booking.OrgId, contact.Id, cancellationToken);
    }

    /// <summary>
    /// Withdraws the guest from a channel: every open contact of the guest on it is suppressed (the address goes on the suppression list). An
    /// e-mail address that is no contact yet is made one and suppressed, so that a notice or a booking that comes later cannot make it reachable.
    /// </summary>
    private async Task WithdrawAsync(
        Booking booking,
        GuestContactChannel channel,
        string displayName,
        string language,
        CancellationToken cancellationToken)
    {
        var channels = await ReadAsync(booking, cancellationToken);
        var open = (channel == GuestContactChannel.Email ? channels.Email : channels.WhatsApp).Where(c => !c.IsSuppressed).ToList();

        if (open.Count == 0 && channel == GuestContactChannel.Email && channels.BookingAddress is { } bookingAddress)
        {
            var made = await contacts.UpsertContactAsync(
                new CrmContactInput(
                    booking.OrgId,
                    GuestContactChannel.Email,
                    bookingAddress,
                    GuestContactOrigin.DirectBooking,
                    booking.Guest.GuestCrmProfileId,
                    booking.Guest.Id,
                    displayName,
                    language,
                    booking.Source),
                cancellationToken);
            if (made is not null && !made.IsSuppressed)
                open.Add(made);
        }

        foreach (var contact in open)
        {
            await contacts.WithdrawAsync(
                booking.OrgId,
                contact.Id,
                WithdrawalSource,
                GuestConsentSource.GuestPage,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>The contacts of the guest on each channel, and the address and the phone of the booking in the form the CRM keys them by.</summary>
    private sealed record GuestChannels(
        IReadOnlyList<GuestContact> Email,
        IReadOnlyList<GuestContact> WhatsApp,
        string? BookingAddress,
        bool BookingAddressIsReal,
        string? BookingPhone);
}

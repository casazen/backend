using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Guest self check-in portal (US-020). Since CO-12 the guest registers every person staying (<see cref="StayGuest"/>):
/// the first one is the booker's registration, as before, and its data is also kept on the booker's <see cref="Guest"/>.
/// </summary>
public class GuestCheckInService(
    AppDbContext db,
    ILogger<GuestCheckInService> logger,
    IStayGuestService? stayGuestService = null,
    IAlloggiatiCodeTableService? codeTableService = null,
    IOptions<GuestCheckInOptions>? options = null,
    TimeProvider? timeProvider = null,
    IOptions<GdprOptions>? gdprOptions = null,
    IPushNotificationService? pushNotifications = null,
    ICrmContactService? crmContacts = null,
    IMarketingConfirmationService? marketingConfirmation = null,
    ICrmContactIndex? contactIndex = null,
    IOptions<CrmOptions>? crmOptions = null) : IGuestCheckInService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly GuestCheckInOptions _options = options?.Value ?? new GuestCheckInOptions();
    private readonly GdprOptions _gdpr = gdprOptions?.Value ?? new GdprOptions();

    private readonly IAlloggiatiCodeTableService _codeTables =
        codeTableService ?? new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance);

    private readonly IStayGuestService _stayGuests = stayGuestService
        ?? new StayGuestService(db, codeTableService ?? new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance));

    /// <summary>Attempts of <see cref="IssueLinkAsync"/> when another link of the booking is issued at the same time.</summary>
    private const int MaxIssueAttempts = 3;

    private const string DocumentNumberMask = "*****";
    private const int DocumentNumberVisibleChars = 3;
    private const int DocumentNumberMinLengthForVisibleChars = 6;

    /// <summary>Longest marketing e-mail stored: the column <see cref="MarketingConsentRecord.MarketingEmail"/>.</summary>
    private const int MarketingEmailMaxLength = 255;

    private static readonly GuestCheckInSessionStatus[] OpenLinkStatuses =
    [
        GuestCheckInSessionStatus.Inviato,
        GuestCheckInSessionStatus.InCompilazione,
    ];

    private static readonly GuestCheckInSessionStatus[] CompletedStatuses =
    [
        GuestCheckInSessionStatus.Completo,
        GuestCheckInSessionStatus.AlloggiatiInviato,
    ];

    public async Task<string> CreateSessionAsync(Guid bookingId, Guid orgId)
    {
        await MakeRoomForNewSentSessionAsync(bookingId);
        return (await AddSessionAsync(bookingId, orgId)).Token;
    }

    public async Task<IssuedCheckInLink> IssueLinkAsync(Guid bookingId, Guid orgId)
    {
        for (var attempt = 1; ; attempt++)
        {
            await ExpireOpenSessionsAsync(bookingId);
            try
            {
                return await AddSessionAsync(bookingId, orgId);
            }
            catch (DbUpdateException ex) when (
                attempt < MaxIssueAttempts &&
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another link of the booking was issued at the same time (host and send job): this one replaces it.
                logger.LogInformation("Check-in link of booking {BookingId} issued concurrently, replacing it", bookingId);
            }
        }
    }

    public async Task<int> ExpireStaleSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var stale = await db.GuestCheckInSessions
            .Where(s => OpenLinkStatuses.Contains(s.Status) && s.ExpiresAt < now)
            .ToListAsync(cancellationToken);

        foreach (var session in stale)
        {
            session.Status = GuestCheckInSessionStatus.Scaduto;
            session.UpdatedAt = now;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Expired {Count} guest check-in links past their validity", stale.Count);
        }

        return stale.Count;
    }

    /// <summary>SHA-256 hex of a raw token: the only form stored (<see cref="GuestCheckInSession.TokenHash"/>).</summary>
    public static string HashToken(string token) => ComputeSha256Hex(token);

    private async Task<IssuedCheckInLink> AddSessionAsync(Guid bookingId, Guid orgId)
    {
        var rawToken = GenerateToken();
        var now = UtcNow();

        var session = new GuestCheckInSession
        {
            BookingId = bookingId,
            OrgId = orgId,
            TokenHash = ComputeSha256Hex(rawToken),
            ExpiresAt = now.Add(_options.SessionLifetime),
            Status = GuestCheckInSessionStatus.Inviato,
            // Set when an email is actually handed to the provider (CO-09): a link to copy is not "sent".
            SentAt = null,
            LinkEmailStatus = GuestCheckInLinkEmailStatus.NotRequested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.GuestCheckInSessions.Add(session);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.Entry(session).State = EntityState.Detached;
            throw;
        }

        logger.LogInformation("Created guest check-in session {SessionId} for booking {BookingId}", session.Id, bookingId);
        return new IssuedCheckInLink(session.Id, rawToken, session.ExpiresAt);
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    public async Task<GuestCheckInSession?> GetSessionByTokenAsync(string token)
    {
        var session = await GetUsableSessionByTokenAsync(token);

        if (session is null)
            return null;

        if (IsCompleted(session.Status))
            return null;

        return session;
    }

    public async Task<GuestCheckInPublicView?> GetPublicViewAsync(string token)
    {
        var session = await GetUsableSessionByTokenAsync(token);
        if (session is null)
            return null;

        // After submission the link (possibly forwarded) shows only that the check-in is done (A5-28).
        if (IsCompleted(session.Status))
            return new GuestCheckInPublicView { Status = session.Status, IsCompleted = true };

        var booking = session.Booking;
        var guests = await _stayGuests.GetForBookingAsync(booking);
        var tables = await _codeTables.GetStatusAsync();
        return new GuestCheckInPublicView
        {
            Status = session.Status,
            SessionId = session.Id,
            PropertyName = booking.Property.Name,
            CheckInDate = booking.CheckInDate,
            CheckOutDate = booking.CheckOutDate,
            DeclaredGuests = Math.Max(1, booking.NumberOfGuests),
            Guests = guests.Select(ToPrefill).ToList(),
            AvailableCodeTables = tables.Where(t => t.RowCount > 0).Select(t => t.Table).ToList(),
            PrivacyNoticeVersion = GdprOptions.Normalize(_gdpr.PrivacyNoticeVersion),
            MarketingConsentVersion = GdprOptions.Normalize(_gdpr.MarketingConsentVersion),
        };
    }

    private static StayGuestPrefill ToPrefill(StayGuest guest) => new()
    {
        Type = guest.Type,
        FirstName = guest.FirstName,
        LastName = guest.LastName,
        Gender = guest.Gender,
        DateOfBirth = guest.DateOfBirth,
        BornInItaly = guest.BornInItaly,
        BirthComuneCode = guest.BirthComuneCode,
        BirthComuneName = guest.BirthComuneName,
        BirthProvince = guest.BirthProvince,
        BirthCountryCode = guest.BirthCountryCode,
        BirthCountryName = guest.BirthCountryName,
        CitizenshipCode = guest.CitizenshipCode,
        CitizenshipName = guest.CitizenshipName,
        DocumentType = guest.DocumentType,
        DocumentTypeCode = guest.DocumentTypeCode,
        DocumentNumberMasked = MaskDocumentNumber(guest.DocumentNumber),
        DocumentIssuePlaceCode = guest.DocumentIssuePlaceCode,
        DocumentIssuePlaceName = guest.DocumentIssuePlaceName,
    };

    /// <summary>
    /// Masks a document number for the public prefill: only the last <see cref="DocumentNumberVisibleChars"/>
    /// characters stay visible, and only when the number is long enough that they do not reveal most of it.
    /// The number of hidden characters is fixed, so the mask does not leak the length. Null when there is none.
    /// </summary>
    public static string? MaskDocumentNumber(string? documentNumber)
    {
        var value = documentNumber?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        var visible = value.Length >= DocumentNumberMinLengthForVisibleChars
            ? value[^DocumentNumberVisibleChars..]
            : string.Empty;
        return DocumentNumberMask + visible;
    }

    private static bool IsCompleted(GuestCheckInSessionStatus status) =>
        status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato;

    private async Task<GuestCheckInSession?> GetUsableSessionByTokenAsync(string token)
    {
        var tokenHash = ComputeSha256Hex(token);
        var session = await db.GuestCheckInSessions
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash);

        if (session is null)
            return null;

        if (session.ExpiresAt < UtcNow() || session.Status == GuestCheckInSessionStatus.Scaduto)
            return null;

        if (!IsBookingEligibleForPublicCheckIn(session.Booking.Status))
            return null;

        // Advance Inviato->InCompilazione on first open/submit.
        if (session.Status == GuestCheckInSessionStatus.Inviato)
        {
            session.Status = GuestCheckInSessionStatus.InCompilazione;
            session.UpdatedAt = UtcNow();
            await db.SaveChangesAsync();
        }

        return session;
    }

    public async Task<GuestCheckInSession?> GetSessionForBookingAsync(Guid bookingId)
    {
        return await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId)
            .OrderByDescending(s => CompletedStatuses.Contains(s.Status))
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<GuestCheckInSubmitResult> SubmitAsync(string token, GuestCheckInSubmitRequest request)
    {
        var session = await GetUsableSessionByTokenAsync(token);
        if (session is null)
            return new GuestCheckInSubmitResult { Success = false };

        if (IsCompleted(session.Status))
            return new GuestCheckInSubmitResult { Success = false, Duplicate = true, SessionId = session.Id };

        // The marketing consent is offered only with a versioned text (Gdpr:MarketingConsentVersion, CO-15). The per-channel
        // consent (NF-05) is checked here too, and its errors come with the ones of the guests: one answer, every error.
        var marketingVersion = GdprOptions.Normalize(_gdpr.MarketingConsentVersion);
        var (consentErrors, marketingEmail) = ValidateMarketingConsent(request, marketingVersion);

        // Every guest: kinds and order, document only for single guests and heads, shape of the codes.
        var guestErrors = await _stayGuests.ValidateAsync(request.Guests);
        if (consentErrors.Count + guestErrors.Count > 0)
            return new GuestCheckInSubmitResult { Success = false, ValidationErrors = [.. consentErrors, .. guestErrors] };

        var now = UtcNow();
        var guest = await EnsureBookingOwnsMutableGuestAsync(session, now);

        // Stages the rows: the session is completed in the same SaveChanges.
        var saved = await _stayGuests.ReplaceAsync(session.Booking, request.Guests, StayGuestAuthor.GuestPortal, save: false);
        if (!saved.Success)
            return new GuestCheckInSubmitResult { Success = false, ValidationErrors = saved.Errors };

        // The first guest is the booker's own registration (prefilled from the booker): its data stays on the booker's
        // record as before CO-12, so the guest views keep showing it.
        CopyToBooker(saved.Guests[0], guest);

        RecordPrivacyChoices(guest, session.Booking, request, marketingVersion, marketingEmail, now);
        guest.DataProcessingPurpose = "Alloggiati Web guest registration (TULPS Art. 109)";
        guest.UpdatedAt = now;

        session.Status = GuestCheckInSessionStatus.Completo;
        session.CompletedAt = now;
        session.UpdatedAt = now;

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Guest check-in submitted for session {SessionId}, booking {BookingId}: {GuestCount} guests",
            session.Id, session.BookingId, saved.Guests.Count);

        // NF-90: the consent the guest ticked becomes a contact of the CRM and its double opt-in e-mail goes out. After the check-in is saved
        // and never fatal for it: the record of the consent is already in the check-in.
        await RecordCrmConsentsAsync(session.Booking, guest, request, marketingVersion, marketingEmail, now);

        PushToHosts(session);

        return new GuestCheckInSubmitResult
        {
            Success = true,
            SessionId = session.Id,
            BookingId = session.BookingId,
            GuestId = guest.Id,
        };
    }

    /// <summary>
    /// Tells the hosts of the booking that the guest completed the online check-in: a push to their phones and, through the push
    /// decorator, the entry of the bell and the live message of the open web sessions (<c>docs/runbooks/in-app-notifications.md</c>).
    /// The text names the property only, never the guest or what was typed. Best effort after the check-in is saved: whatever goes
    /// wrong here is logged and never fails the guest's submit.
    /// </summary>
    private void PushToHosts(GuestCheckInSession session)
    {
        if (pushNotifications is null)
            return;

        try
        {
            var booking = session.Booking;
            var push = EmailTemplates.GuestCheckInCompletedPush(EmailTemplates.DefaultCulture, booking.Property.Name);
            pushNotifications.Enqueue(
                PushDeliveryKeys.GuestCheckInCompleted(session.Id),
                PushAudience.BookingHosts(booking.Id),
                new PushNotificationPayload(
                    push.Title, push.Body, PushTypes.GuestCheckInCompleted, booking.Id, PushRoutes.Booking(booking.Id)));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Push about the check-in of session {SessionId}, booking {BookingId} could not be prepared", session.Id, session.BookingId);
        }
    }

    public async Task<string> RegenerateTokenAsync(Guid bookingId, Guid orgId) =>
        (await IssueLinkAsync(bookingId, orgId)).Token;

    /// <summary>Expires every open link of the booking: a new link replaces them (the unique index allows one per status).</summary>
    private async Task ExpireOpenSessionsAsync(Guid bookingId)
    {
        var openSessions = await db.GuestCheckInSessions
            .Where(s => s.BookingId == bookingId && OpenLinkStatuses.Contains(s.Status))
            .ToListAsync();
        if (openSessions.Count == 0)
            return;

        var now = UtcNow();
        foreach (var s in openSessions)
        {
            s.Status = GuestCheckInSessionStatus.Scaduto;
            s.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
    }

    public async Task ExpireTokenAsync(string token)
    {
        var tokenHash = ComputeSha256Hex(token);
        var session = await db.GuestCheckInSessions
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash);

        if (session is null)
            return;

        session.Status = GuestCheckInSessionStatus.Scaduto;
        session.UpdatedAt = UtcNow();
        await db.SaveChangesAsync();
    }

    public async Task ExpireOtherActiveSessionsAsync(Guid bookingId, string tokenToKeep)
    {
        var tokenHashToKeep = ComputeSha256Hex(tokenToKeep);
        var activeSessions = await db.GuestCheckInSessions
            .Where(s =>
                s.BookingId == bookingId &&
                s.TokenHash != tokenHashToKeep &&
                OpenLinkStatuses.Contains(s.Status))
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            session.Status = GuestCheckInSessionStatus.Scaduto;
            session.UpdatedAt = UtcNow();
        }

        await db.SaveChangesAsync();
    }

    private async Task MakeRoomForNewSentSessionAsync(Guid bookingId)
    {
        var sentSessions = await db.GuestCheckInSessions
            .Where(s => s.BookingId == bookingId && s.Status == GuestCheckInSessionStatus.Inviato)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        if (sentSessions.Count == 0)
            return;

        var hasInProgressSession = await db.GuestCheckInSessions
            .AnyAsync(s => s.BookingId == bookingId && s.Status == GuestCheckInSessionStatus.InCompilazione);

        var preserveOneUsableLink = !hasInProgressSession;
        var now = UtcNow();
        foreach (var session in sentSessions)
        {
            session.Status = preserveOneUsableLink
                ? GuestCheckInSessionStatus.InCompilazione
                : GuestCheckInSessionStatus.Scaduto;
            session.UpdatedAt = now;
            preserveOneUsableLink = false;
        }

        await db.SaveChangesAsync();
    }

    private static bool IsBookingEligibleForPublicCheckIn(BookingStatus status) =>
        status is BookingStatus.Confirmed or BookingStatus.CheckedIn;

    /// <summary>
    /// The marketing consent choices of the submission (CO-15, NF-05, contract on <c>PublicCheckInSubmitRequest</c>). Nothing
    /// ticked: no errors and no e-mail. Ticked without a versioned text: the consent is not offered, one error per ticked
    /// field. A channel ticked (e-mail, WhatsApp): the booker (first guest) must be of age and the marketing e-mail must be
    /// a plain address, which is returned normalized; with no channel ticked the e-mail is ignored. The legacy box
    /// (<see cref="GuestCheckInSubmitRequest.MarketingConsent"/>) has no further rule.
    /// </summary>
    private (List<StayGuestFieldError> Errors, string? MarketingEmail) ValidateMarketingConsent(
        GuestCheckInSubmitRequest request,
        string? marketingVersion)
    {
        var errors = new List<StayGuestFieldError>();
        var channels = new List<string>(2);
        if (request.MarketingConsentEmail)
            channels.Add(nameof(request.MarketingConsentEmail));
        if (request.MarketingConsentWhatsApp)
            channels.Add(nameof(request.MarketingConsentWhatsApp));

        var ticked = request.MarketingConsent ? [nameof(request.MarketingConsent), .. channels] : channels;
        if (ticked.Count == 0)
            return (errors, null);

        if (marketingVersion is null)
        {
            errors.AddRange(ticked.Select(field => new StayGuestFieldError(null, field, CheckInValidationKeys.MarketingConsentUnavailable)));
            return (errors, null);
        }

        if (channels.Count == 0)
            return (errors, null);

        // Minors are excluded (Q1-05, AC-NF05-3): the consent is the booker's, the first guest of the list.
        if (request.Guests.FirstOrDefault()?.DateOfBirth is { } dateOfBirth
            && AlloggiatiRecordRules.IsMinor(dateOfBirth, _clock.TodayInRome()) == true)
        {
            errors.AddRange(channels.Select(field => new StayGuestFieldError(null, field, CheckInValidationKeys.MarketingConsentMinor)));
        }

        var email = NormalizeMarketingEmail(request.MarketingEmail);
        if (email is null)
        {
            var key = string.IsNullOrWhiteSpace(request.MarketingEmail)
                ? CheckInValidationKeys.MarketingEmailRequired
                : CheckInValidationKeys.MarketingEmailInvalid;
            errors.Add(new StayGuestFieldError(null, nameof(request.MarketingEmail), key));
        }

        return (errors, errors.Count == 0 ? email : null);
    }

    /// <summary>
    /// The marketing e-mail as it is stored: trimmed and lowercase, the form the suppression list hashes
    /// (<see cref="ShowcaseBookingRules.NormalizeEmail"/>). Null when it is missing or is not a plain address: no display
    /// name, no spaces, a host with a dot, at most <see cref="MarketingEmailMaxLength"/> characters.
    /// </summary>
    public static string? NormalizeMarketingEmail(string? value)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email)
            || email.Length > MarketingEmailMaxLength
            || email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.IsNullOrEmpty(parsed.DisplayName)
            || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        return ShowcaseBookingRules.NormalizeEmail(email);
    }

    /// <summary>
    /// CO-15 (A5-15): the Alloggiati registration is a legal obligation (art. 109 TULPS, art. 6.1.c GDPR), not a consent:
    /// only the version of the privacy notice presented is recorded. The marketing consent is optional and recorded only
    /// when the guest ticks it, with its version; an unticked box leaves an earlier consent as it is (it is not a
    /// withdrawal). Both rows carry the guest's IP and time as proof (art. 7.1 GDPR). The per-channel consent (NF-05) is one
    /// <see cref="MarketingConsentRecord"/> with the channels ticked and no other, the marketing e-mail (not confirmed: no
    /// send before the double opt-in), the booking, the time, the version of the text and the IP; it does not set
    /// <see cref="Guest.MarketingConsent"/>, which the campaigns read, so nothing is sent before the e-mail is confirmed.
    /// </summary>
    private void RecordPrivacyChoices(
        Guest guest,
        Booking booking,
        GuestCheckInSubmitRequest request,
        string? marketingVersion,
        string? marketingEmail,
        DateTime now)
    {
        var ip = request.ConsentIpAddress.Length > 50 ? request.ConsentIpAddress[..50] : request.ConsentIpAddress;
        var ipAddress = string.IsNullOrWhiteSpace(ip) ? null : ip;

        if (GdprOptions.Normalize(_gdpr.PrivacyNoticeVersion) is { } noticeVersion)
        {
            db.GuestConsentRecords.Add(new GuestConsentRecord
            {
                OrgId = guest.OrgId,
                GuestId = guest.Id,
                Purpose = GuestConsentPurpose.PrivacyNotice,
                Action = GuestConsentAction.NoticePresented,
                Version = noticeVersion,
                Source = GuestConsentSource.GuestPortal,
                IpAddress = ipAddress,
                RecordedAt = now,
            });
        }
        else
        {
            logger.LogWarning(
                "Gdpr:PrivacyNoticeVersion is not configured: the privacy notice shown for guest {GuestId} is not recorded",
                guest.Id);
        }

        if (request.MarketingConsent && marketingVersion is not null)
        {
            db.GuestConsentRecords.Add(new GuestConsentRecord
            {
                OrgId = guest.OrgId,
                GuestId = guest.Id,
                Purpose = GuestConsentPurpose.Marketing,
                Action = GuestConsentAction.Granted,
                Version = marketingVersion,
                Source = GuestConsentSource.GuestPortal,
                IpAddress = ipAddress,
                RecordedAt = now,
            });
            guest.MarketingConsent = true;
            guest.MarketingConsentDate = now;
        }

        // NF-05: validated before, so a marketing e-mail means at least one channel ticked and a versioned text.
        if (marketingEmail is not null && marketingVersion is not null)
        {
            db.MarketingConsentRecords.Add(new MarketingConsentRecord
            {
                OrgId = booking.OrgId,
                BookingId = booking.Id,
                GuestId = guest.Id,
                MarketingEmail = marketingEmail,
                ConsentEmail = request.MarketingConsentEmail,
                ConsentWhatsApp = request.MarketingConsentWhatsApp,
                EmailConfirmed = false,
                ConsentTimestamp = now,
                ConsentTextVersion = marketingVersion,
                IpAddress = ip,
            });
        }
    }

    /// <summary>
    /// Makes the contacts of the per-channel marketing consent of the check-in and sends the double opt-in e-mail (E07/F04/US01/T02, NF-90,
    /// CRM-03). A ticked e-mail channel makes the consent contact of the marketing address the guest typed (origin
    /// <see cref="GuestContactOrigin.CheckIn"/>, source <see cref="GuestConsentSource.GuestPortal"/>, the version, the IP, the time and the
    /// default language of the org) and queues its confirmation; a ticked WhatsApp channel makes a contact only when the phone number of the
    /// booker is known: otherwise the consent stays in the <see cref="MarketingConsentRecord"/> and the CRM sync picks it up when a phone
    /// exists. A box left unticked does nothing, not even to an earlier consent. Minors never get here: a submission whose booker is a minor
    /// is refused before. <b>Best effort and never fatal</b>: the check-in is saved, and without the key of the contact index (the CRM is closed in
    /// this environment) nothing is made; a failure is logged by id and error type, never by address, and the sync of the CRM makes the contact.
    /// </summary>
    private async Task RecordCrmConsentsAsync(
        Booking booking,
        Guest guest,
        GuestCheckInSubmitRequest request,
        string? marketingVersion,
        string? marketingEmail,
        DateTime now)
    {
        var email = request.MarketingConsentEmail && marketingEmail is not null;
        var whatsApp = request.MarketingConsentWhatsApp;
        if ((!email && !whatsApp) || marketingVersion is null || crmContacts is null || contactIndex is not { IsConfigured: true })
            return;

        try
        {
            var ip = request.ConsentIpAddress.Length > 50 ? request.ConsentIpAddress[..50] : request.ConsentIpAddress;
            var ipAddress = string.IsNullOrWhiteSpace(ip) ? null : ip;
            var language = await db.Orgs.AsNoTracking()
                .Where(o => o.Id == booking.OrgId)
                .Select(o => o.DefaultLanguage)
                .FirstOrDefaultAsync();
            var displayName = $"{guest.FirstName} {guest.LastName}".Trim();

            if (email)
            {
                var contact = await crmContacts.RecordConsentAsync(new CrmConsentInput(
                    booking.OrgId,
                    GuestContactChannel.Email,
                    marketingEmail,
                    GuestContactOrigin.CheckIn,
                    marketingVersion,
                    GuestConsentSource.GuestPortal,
                    ipAddress,
                    now,
                    language,
                    displayName,
                    guest.GuestCrmProfileId,
                    guest.Id));

                if (contact is not null && marketingConfirmation is not null)
                    await marketingConfirmation.SendConfirmationAsync(booking.OrgId, contact.Id);
            }

            if (whatsApp && CrmNormalizer.NormalizePhone(guest.PhoneNumber, crmOptions?.Value.DefaultCallingCode) is { } phone)
            {
                await crmContacts.RecordConsentAsync(new CrmConsentInput(
                    booking.OrgId,
                    GuestContactChannel.WhatsApp,
                    phone,
                    GuestContactOrigin.CheckIn,
                    marketingVersion,
                    GuestConsentSource.GuestPortal,
                    ipAddress,
                    now,
                    language,
                    displayName,
                    guest.GuestCrmProfileId,
                    guest.Id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "The CRM contact of the marketing consent of the check-in of booking {BookingId} could not be made ({Error}); the check-in holds the consent and the CRM sync makes the contact",
                booking.Id,
                CrmDatabaseErrors.Describe(ex));
        }
    }

    /// <summary>Copies the first guest's registration on the booker's record (identity and document, as before CO-12).</summary>
    private static void CopyToBooker(StayGuest first, Guest booker)
    {
        booker.FirstName = first.FirstName;
        booker.LastName = first.LastName;
        booker.Gender = first.Gender;
        booker.DateOfBirth = first.DateOfBirth;
        booker.Nationality = first.CitizenshipName;
        booker.PlaceOfBirth = first.BornInItaly == true
            ? $"{first.BirthComuneName} ({first.BirthProvince})"
            : first.BirthCountryName;
        if (AlloggiatiRecordRules.RequiresDocument(first.Type))
        {
            booker.DocumentType = first.DocumentType;
            booker.DocumentNumber = first.DocumentNumber;
            booker.DocumentIssuingCountry = first.DocumentIssuePlaceName;
        }
    }

    private async Task<Guest> EnsureBookingOwnsMutableGuestAsync(GuestCheckInSession session, DateTime now)
    {
        var booking = session.Booking;
        var guest = booking.Guest;
        var guestIsShared = await db.Bookings.AnyAsync(b => b.GuestId == guest.Id && b.Id != booking.Id);
        if (!guestIsShared)
            return guest;

        var snapshot = guest.CreateSnapshot(now);
        snapshot.OrgId = booking.OrgId;
        db.Guests.Add(snapshot);
        booking.GuestId = snapshot.Id;
        booking.Guest = snapshot;

        logger.LogInformation(
            "Created guest snapshot {SnapshotGuestId} for public check-in booking {BookingId} from shared guest {GuestId}",
            snapshot.Id, booking.Id, guest.Id);

        return snapshot;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

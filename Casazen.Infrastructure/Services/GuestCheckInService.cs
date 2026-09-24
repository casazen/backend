using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
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
    TimeProvider? timeProvider = null) : IGuestCheckInService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly GuestCheckInOptions _options = options?.Value ?? new GuestCheckInOptions();

    private readonly IAlloggiatiCodeTableService _codeTables =
        codeTableService ?? new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance);

    private readonly IStayGuestService _stayGuests = stayGuestService
        ?? new StayGuestService(db, codeTableService ?? new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance));

    /// <summary>Attempts of <see cref="IssueLinkAsync"/> when another link of the booking is issued at the same time.</summary>
    private const int MaxIssueAttempts = 3;

    private const string DocumentNumberMask = "*****";
    private const int DocumentNumberVisibleChars = 3;
    private const int DocumentNumberMinLengthForVisibleChars = 6;

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

        if (!request.GdprConsent)
        {
            return new GuestCheckInSubmitResult
            {
                Success = false,
                ValidationErrors = [new StayGuestFieldError(null, nameof(request.GdprConsent), CheckInValidationKeys.GdprConsentRequired)],
            };
        }

        // Every guest: kinds and order, document only for single guests and heads, shape of the codes.
        var errors = await _stayGuests.ValidateAsync(request.Guests);
        if (errors.Count > 0)
            return new GuestCheckInSubmitResult { Success = false, ValidationErrors = errors };

        var now = UtcNow();
        var guest = await EnsureBookingOwnsMutableGuestAsync(session, now);

        // Stages the rows: the session is completed in the same SaveChanges.
        var saved = await _stayGuests.ReplaceAsync(session.Booking, request.Guests, StayGuestAuthor.GuestPortal, save: false);
        if (!saved.Success)
            return new GuestCheckInSubmitResult { Success = false, ValidationErrors = saved.Errors };

        // The first guest is the booker's own registration (prefilled from the booker): its data stays on the booker's
        // record as before CO-12, so the guest views keep showing it.
        CopyToBooker(saved.Guests[0], guest);

        guest.ConsentDate = now;
        guest.DataProcessingConsentDate = now;
        guest.MarketingConsent = request.MarketingConsent;
        guest.MarketingConsentDate = request.MarketingConsent ? now : null;
        var ip = request.ConsentIpAddress;
        guest.ConsentIpAddress = ip.Length > 50 ? ip[..50] : ip;
        guest.DataRetentionUntil = now.AddYears(7);
        guest.DataProcessingPurpose = "Alloggiati Web guest registration (TULPS Art. 109)";
        guest.UpdatedAt = now;

        session.Status = GuestCheckInSessionStatus.Completo;
        session.CompletedAt = now;
        session.UpdatedAt = now;

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Guest check-in submitted for session {SessionId}, booking {BookingId}: {GuestCount} guests",
            session.Id, session.BookingId, saved.Guests.Count);

        return new GuestCheckInSubmitResult
        {
            Success = true,
            SessionId = session.Id,
            BookingId = session.BookingId,
            GuestId = guest.Id,
        };
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

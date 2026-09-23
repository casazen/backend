using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class GuestCheckInService(
    AppDbContext db,
    ILogger<GuestCheckInService> logger) : IGuestCheckInService
{
    private const string DocumentNumberMask = "*****";
    private const int DocumentNumberVisibleChars = 3;
    private const int DocumentNumberMinLengthForVisibleChars = 6;

    private static readonly GuestCheckInSessionStatus[] OpenLinkStatuses =
    [
        GuestCheckInSessionStatus.Inviato,
        GuestCheckInSessionStatus.InCompilazione,
    ];

    private static readonly GuestCheckInSessionStatus[] ActiveStatuses =
    [
        GuestCheckInSessionStatus.Inviato,
        GuestCheckInSessionStatus.InCompilazione,
        GuestCheckInSessionStatus.Completo,
        GuestCheckInSessionStatus.AlloggiatiInviato,
    ];

    public async Task<string> CreateSessionAsync(Guid bookingId, Guid orgId)
    {
        await MakeRoomForNewSentSessionAsync(bookingId);

        var rawToken = GenerateToken();
        var tokenHash = ComputeSha256Hex(rawToken);

        var session = new GuestCheckInSession
        {
            BookingId = bookingId,
            OrgId = orgId,
            TokenHash = tokenHash,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Status = GuestCheckInSessionStatus.Inviato,
            SentAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.GuestCheckInSessions.Add(session);
        await db.SaveChangesAsync();

        logger.LogInformation("Created guest check-in session {SessionId} for booking {BookingId}", session.Id, bookingId);
        return rawToken;
    }

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
        var guest = booking.Guest;
        return new GuestCheckInPublicView
        {
            Status = session.Status,
            SessionId = session.Id,
            PropertyName = booking.Property.Name,
            CheckInDate = booking.CheckInDate,
            CheckOutDate = booking.CheckOutDate,
            GuestPrefill = new GuestCheckInPrefill
            {
                FirstName = guest.FirstName,
                LastName = guest.LastName,
                Email = guest.Email,
                DateOfBirth = guest.DateOfBirth,
                Nationality = guest.Nationality,
                Gender = guest.Gender,
                DocumentNumberMasked = MaskDocumentNumber(guest.DocumentNumber),
                DocumentIssuingCountry = guest.DocumentIssuingCountry,
                PlaceOfBirth = guest.PlaceOfBirth,
            },
        };
    }

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

        if (session.ExpiresAt < DateTime.UtcNow || session.Status == GuestCheckInSessionStatus.Scaduto)
            return null;

        if (!IsBookingEligibleForPublicCheckIn(session.Booking.Status))
            return null;

        // Advance Inviato->InCompilazione on first open/submit.
        if (session.Status == GuestCheckInSessionStatus.Inviato)
        {
            session.Status = GuestCheckInSessionStatus.InCompilazione;
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return session;
    }

    public async Task<GuestCheckInSession?> GetSessionForBookingAsync(Guid bookingId)
    {
        return await db.GuestCheckInSessions
            .Where(s => s.BookingId == bookingId && ActiveStatuses.Contains(s.Status))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<GuestCheckInSubmitResult> SubmitAsync(string token, GuestCheckInSubmitRequest request)
    {
        var session = await GetUsableSessionByTokenAsync(token);
        if (session is null)
            return new GuestCheckInSubmitResult { Success = false };

        if (IsCompleted(session.Status))
            return new GuestCheckInSubmitResult { Success = false, Duplicate = true, SessionId = session.Id };

        if (!TryValidateRequest(request, out var documentType, out var invalidField, out var errorKey))
        {
            return new GuestCheckInSubmitResult
            {
                Success = false,
                ValidationField = invalidField,
                ValidationErrorKey = errorKey,
            };
        }

        var now = DateTime.UtcNow;
        var guest = await EnsureBookingOwnsMutableGuestAsync(session, now);

        if (!string.IsNullOrWhiteSpace(request.FirstName)) guest.FirstName = request.FirstName;
        if (!string.IsNullOrWhiteSpace(request.LastName)) guest.LastName = request.LastName;
        if (request.DateOfBirth.HasValue)
            guest.DateOfBirth = DateTime.SpecifyKind(request.DateOfBirth.Value.Date, DateTimeKind.Utc);
        if (!string.IsNullOrWhiteSpace(request.Nationality)) guest.Nationality = request.Nationality;
        if (request.Gender.HasValue) guest.Gender = request.Gender.Value;
        if (!string.IsNullOrWhiteSpace(request.DocumentNumber)) guest.DocumentNumber = request.DocumentNumber;
        if (!string.IsNullOrWhiteSpace(request.DocumentIssuingCountry)) guest.DocumentIssuingCountry = request.DocumentIssuingCountry;
        if (!string.IsNullOrWhiteSpace(request.PlaceOfBirth)) guest.PlaceOfBirth = request.PlaceOfBirth;

        guest.DocumentType = documentType;

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
            "Guest check-in submitted for session {SessionId}, booking {BookingId}",
            session.Id, session.BookingId);

        return new GuestCheckInSubmitResult
        {
            Success = true,
            SessionId = session.Id,
            BookingId = session.BookingId,
            GuestId = guest.Id,
        };
    }

    public async Task MarkAlloggiatiEnqueuedAsync(Guid sessionId)
    {
        var session = await db.GuestCheckInSessions.FindAsync(sessionId);
        if (session is null) return;

        session.Status = GuestCheckInSessionStatus.AlloggiatiInviato;
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<string> RegenerateTokenAsync(Guid bookingId, Guid orgId)
    {
        var activeSessions = await db.GuestCheckInSessions
            .Where(s => s.BookingId == bookingId && OpenLinkStatuses.Contains(s.Status))
            .ToListAsync();

        foreach (var s in activeSessions)
        {
            s.Status = GuestCheckInSessionStatus.Scaduto;
            s.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return await CreateSessionAsync(bookingId, orgId);
    }

    public async Task ExpireTokenAsync(string token)
    {
        var tokenHash = ComputeSha256Hex(token);
        var session = await db.GuestCheckInSessions
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash);

        if (session is null)
            return;

        session.Status = GuestCheckInSessionStatus.Scaduto;
        session.UpdatedAt = DateTime.UtcNow;
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
            session.UpdatedAt = DateTime.UtcNow;
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
        var now = DateTime.UtcNow;
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
    /// Service-level check of the data Alloggiati Web needs (the API validates the same fields first). On failure
    /// returns the <see cref="GuestCheckInSubmitRequest"/> property at fault and the SharedResources message key.
    /// </summary>
    private static bool TryValidateRequest(
        GuestCheckInSubmitRequest request,
        out GuestDocumentType documentType,
        out string? invalidField,
        out string? errorKey)
    {
        documentType = default;
        invalidField = null;
        errorKey = null;

        if (!request.GdprConsent)
            return Invalid(nameof(request.GdprConsent), CheckInValidationKeys.GdprConsentRequired, out invalidField, out errorKey);

        var missingField = FirstMissingRequiredField(request);
        if (missingField is not null)
            return Invalid(missingField, CheckInValidationKeys.FieldRequired, out invalidField, out errorKey);

        // Alloggiati Web accepts only 1 = male and 2 = female (tracciato record, field "Sesso").
        if (request.Gender is not (Gender.Male or Gender.Female))
            return Invalid(nameof(request.Gender), CheckInValidationKeys.GenderInvalid, out invalidField, out errorKey);

        if (!Enum.TryParse(request.DocumentType, ignoreCase: true, out documentType)
            || !Enum.IsDefined(typeof(GuestDocumentType), documentType))
        {
            return Invalid(nameof(request.DocumentType), CheckInValidationKeys.DocumentTypeInvalid, out invalidField, out errorKey);
        }

        return true;
    }

    private static string? FirstMissingRequiredField(GuestCheckInSubmitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FirstName)) return nameof(request.FirstName);
        if (string.IsNullOrWhiteSpace(request.LastName)) return nameof(request.LastName);
        if (!request.DateOfBirth.HasValue) return nameof(request.DateOfBirth);
        if (string.IsNullOrWhiteSpace(request.PlaceOfBirth)) return nameof(request.PlaceOfBirth);
        if (string.IsNullOrWhiteSpace(request.Nationality)) return nameof(request.Nationality);
        if (!request.Gender.HasValue) return nameof(request.Gender);
        if (string.IsNullOrWhiteSpace(request.DocumentType)) return nameof(request.DocumentType);
        if (string.IsNullOrWhiteSpace(request.DocumentNumber)) return nameof(request.DocumentNumber);
        if (string.IsNullOrWhiteSpace(request.DocumentIssuingCountry)) return nameof(request.DocumentIssuingCountry);
        return null;
    }

    private static bool Invalid(string field, string key, out string? invalidField, out string? errorKey)
    {
        invalidField = field;
        errorKey = key;
        return false;
    }

    private async Task<Guest> EnsureBookingOwnsMutableGuestAsync(GuestCheckInSession session, DateTime now)
    {
        var booking = session.Booking;
        var guest = booking.Guest;
        var guestIsShared = await db.Bookings.AnyAsync(b => b.GuestId == guest.Id && b.Id != booking.Id);
        if (!guestIsShared)
            return guest;

        var snapshot = guest.CreateSnapshot(now);
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

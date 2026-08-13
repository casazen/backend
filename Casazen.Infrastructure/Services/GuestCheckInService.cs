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
    private static readonly GuestCheckInSessionStatus[] ActiveStatuses =
    [
        GuestCheckInSessionStatus.Inviato,
        GuestCheckInSessionStatus.InCompilazione,
        GuestCheckInSessionStatus.Completo,
        GuestCheckInSessionStatus.AlloggiatiInviato,
    ];

    public async Task<string> CreateSessionAsync(Guid bookingId, Guid orgId)
    {
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

        // Advance Inviato→InCompilazione on first open
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
        var session = await GetSessionByTokenAsync(token);
        if (session is null)
            return new GuestCheckInSubmitResult { Success = false };

        if (session.Status == GuestCheckInSessionStatus.Completo ||
            session.Status == GuestCheckInSessionStatus.AlloggiatiInviato)
        {
            return new GuestCheckInSubmitResult { Success = false, Duplicate = true, SessionId = session.Id };
        }

        if (!request.GdprConsent)
            return new GuestCheckInSubmitResult { Success = false };

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

        if (Enum.TryParse<GuestDocumentType>(request.DocumentType, true, out var docType))
            guest.DocumentType = docType;

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
            .Where(s => s.BookingId == bookingId && ActiveStatuses.Contains(s.Status))
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
                ActiveStatuses.Contains(s.Status))
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            session.Status = GuestCheckInSessionStatus.Scaduto;
            session.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private static bool IsBookingEligibleForPublicCheckIn(BookingStatus status) =>
        status is BookingStatus.Confirmed or BookingStatus.CheckedIn;

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

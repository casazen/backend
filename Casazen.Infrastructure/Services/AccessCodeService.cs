using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Manages property access codes with encryption and verification gating (NF-13, E06/F03/US01/T01).
/// </summary>
public class AccessCodeService : IAccessCodeService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AccessCodeService> _logger;

    public AccessCodeService(AppDbContext db, ILogger<AccessCodeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SetAccessCodeAsync(Guid propertyId, string code, string userId, CancellationToken ct = default)
    {
        var property = await _db.Properties
            .Include(p => p.Org)
            .FirstOrDefaultAsync(p => p.Id == propertyId, ct);

        if (property == null)
            throw new InvalidOperationException($"Property {propertyId} not found");

        var existing = await _db.Set<PropertyAccessCode>()
            .FirstOrDefaultAsync(ac => ac.PropertyId == propertyId, ct);

        if (existing != null)
        {
            // Update existing code (EF converter handles encryption)
            existing.EncryptedCode = code;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Create new access code (EF converter handles encryption)
            var accessCode = new PropertyAccessCode
            {
                PropertyId = propertyId,
                OrgId = property.OrgId,
                EncryptedCode = code,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Set<PropertyAccessCode>().Add(accessCode);
        }

        await _db.SaveChangesAsync(ct);

        // Log the action without the code itself (AC-NF13-1, AC-NF13-5)
        _logger.LogInformation(
            "Access code set for property {PropertyId} by user {UserId}",
            propertyId, userId);
    }

    public async Task<string?> GetAccessCodeAsync(Guid bookingId, string userId, CancellationToken ct = default)
    {
        // Check verification passed before allowing access
        if (!await IsVerificationPassedAsync(bookingId, ct))
        {
            _logger.LogWarning(
                "Access code request denied for booking {BookingId}: verification not passed",
                bookingId);
            return null;
        }

        var booking = await _db.Bookings
            .Include(b => b.Property)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

        if (booking == null)
            return null;

        var accessCode = await _db.Set<PropertyAccessCode>()
            .FirstOrDefaultAsync(ac => ac.PropertyId == booking.PropertyId, ct);

        if (accessCode == null)
            return null;

        // Log the access (AC-NF13-2)
        _logger.LogInformation(
            "Access code accessed for booking {BookingId} by user {UserId}",
            bookingId, userId);

        // EF converter handles decryption
        return accessCode.EncryptedCode;
    }

    public async Task<string?> ReleaseToGuestAsync(Guid bookingId, string channel, CancellationToken ct = default)
    {
        // Check verification passed before releasing (AC-NF13-3, AC-NF13-6)
        if (!await IsVerificationPassedAsync(bookingId, ct))
        {
            _logger.LogWarning(
                "Access code release denied for booking {BookingId}: verification not passed",
                bookingId);
            return null;
        }

        var booking = await _db.Bookings
            .Include(b => b.Property)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

        if (booking == null)
            return null;

        var accessCode = await _db.Set<PropertyAccessCode>()
            .FirstOrDefaultAsync(ac => ac.PropertyId == booking.PropertyId, ct);

        if (accessCode == null)
            return null;

        // Log the release WITHOUT the code itself (AC-NF13-4, AC-NF13-9)
        var releaseLog = new AccessCodeReleaseLog
        {
            BookingId = bookingId,
            Channel = channel,
            ReleasedAt = DateTime.UtcNow
        };
        _db.Set<AccessCodeReleaseLog>().Add(releaseLog);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Access code released for booking {BookingId} via {Channel}",
            bookingId, channel);

        // EF converter handles decryption
        return accessCode.EncryptedCode;
    }

    /// <summary>
    /// Checks if the booking's guest has passed biometric verification or operator verification.
    /// </summary>
    private async Task<bool> IsVerificationPassedAsync(Guid bookingId, CancellationToken ct)
    {
        var verificationLog = await _db.BiometricVerificationLogs
            .Where(v => v.BookingId == bookingId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (verificationLog == null)
            return false;

        // Accept if outcome is Accept OR if operator manually verified (AC-NF13-6)
        return verificationLog.Outcome == BiometricOutcome.Accept
            || verificationLog.OperatorUserId != null;
    }
}

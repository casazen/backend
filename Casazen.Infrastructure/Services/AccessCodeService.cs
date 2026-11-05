using Casazen.Core.Authorization;
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
            OrgId = booking.Property.OrgId,
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

    public async Task<AccessCodeStatusPage> GetStatusAsync(
        HostScope scope,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        page = Math.Clamp(page, 1, AccessCodeStatusPage.MaxPage);
        pageSize = Math.Clamp(pageSize, 1, AccessCodeStatusPage.MaxPageSize);

        var skipped = (page - 1) * pageSize;
        var rows = await StatusRows(scope, skipped, pageSize).ToListAsync(ct);

        // A property with no code row has no date: the status is derived from the date, there is no second read of the code table.
        var items = rows
            .Select(r => new AccessCodeStatusItem(r.PropertyId, r.Name, r.City, r.CodeUpdatedAt is not null, r.CodeUpdatedAt))
            .ToList();

        // A page that is not full is the last one, and says how many there are: no second query. A full page, or a page past the end,
        // does not, and the count is asked.
        var isLastPage = items.Count < pageSize && (items.Count > 0 || skipped == 0);
        var total = isLastPage ? skipped + items.Count : await ReachableProperties(scope).CountAsync(ct);

        return new AccessCodeStatusPage(items, total, page, pageSize);
    }

    /// <summary>
    /// The properties of <paramref name="scope"/>, narrowed in SQL (AM-03): the org of the caller first, then what the caller reaches. A
    /// collaborator «Solo alcuni» gets the properties it was given, and a property of another org is never in the query.
    /// </summary>
    internal IQueryable<Property> ReachableProperties(HostScope scope) =>
        _db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope);

    /// <summary>
    /// The rows of one page of the status, by name (the id as the tie-break): exactly the statement <see cref="GetStatusAsync"/> runs, so
    /// a test can read its SQL. The projection names what it reads, the property and the date of its code; the encrypted column of the
    /// code is not selected, so the converter that decrypts it never runs (ADR-013) and neither the code nor its ciphertext can end up in
    /// the answer. The date is a single correlated subquery on the index of the property, whatever the rows a property has in the table.
    /// </summary>
    internal IQueryable<AccessCodeStatusRow> StatusRows(HostScope scope, int skip, int take) =>
        ReachableProperties(scope)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Skip(skip)
            .Take(take)
            .Select(p => new AccessCodeStatusRow(
                p.Id,
                p.Name,
                p.City,
                _db.PropertyAccessCodes
                    .Where(ac => ac.PropertyId == p.Id)
                    .Max(ac => (DateTime?)ac.UpdatedAt)));

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

/// <summary>
/// What the status query reads for a property: the property and the date its code was last set (<c>null</c> when it has none). There is
/// no member for the code on purpose, so the statement cannot select it.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Name">The name of the property.</param>
/// <param name="City">The city of the property.</param>
/// <param name="CodeUpdatedAt">The latest <see cref="PropertyAccessCode.UpdatedAt"/> of the property, <c>null</c> when it has no code row.</param>
internal sealed record AccessCodeStatusRow(Guid PropertyId, string Name, string City, DateTime? CodeUpdatedAt);

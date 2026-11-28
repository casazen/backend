using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class AccessCodeService : IAccessCodeService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AccessCodeService> _logger;
    private readonly ITouristTaxCollectionService? _touristTax;

    public AccessCodeService(AppDbContext db, ILogger<AccessCodeService> logger, ITouristTaxCollectionService? touristTax = null)
    {
        _db = db;
        _logger = logger;
        _touristTax = touristTax;
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
            existing.EncryptedCode = code;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
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

        _logger.LogInformation(
            "Access code set for property {PropertyId} by user {UserId}",
            propertyId, userId);
    }

    public async Task<string?> GetAccessCodeAsync(Guid bookingId, string userId, HostScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var reaches = await _db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId)
            .InScope(scope)
            .AnyAsync(ct);
        if (!reaches)
        {
            _logger.LogWarning(
                "Access code request denied for booking {BookingId} by user {UserId}: the booking is outside the caller scope",
                bookingId, userId);
            return null;
        }

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

        _logger.LogInformation(
            "Access code accessed for booking {BookingId} by user {UserId}",
            bookingId, userId);

        return accessCode.EncryptedCode;
    }

    public async Task<string?> ReleaseToGuestAsync(Guid bookingId, string channel, CancellationToken ct = default)
    {
        var code = await GetReleasableCodeAsync(bookingId, channel, ct);
        if (code is null)
            return null;

        await RecordReleaseAsync(bookingId, channel, ct);
        return code;
    }

    public async Task<string?> GetReleasableCodeAsync(Guid bookingId, string channel, CancellationToken ct = default)
    {
        if (!AccessCodeReleaseChannels.All.Contains(channel, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown release channel '{channel}'.", nameof(channel));

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

        if (!AlloggiatiStatusRules.IsDue(booking) || booking.Status == BookingStatus.CheckedOut)
        {
            _logger.LogWarning(
                "Access code release denied for booking {BookingId}: the stay is not active ({Status})",
                bookingId, booking.Status);
            return null;
        }

        if (await GetHoldReasonAsync(bookingId, ct) is { } hold)
        {
            _logger.LogInformation("Access code release held back for booking {BookingId} via {Channel}: {Reason}", bookingId, channel, hold);
            return null;
        }

        var accessCode = await _db.Set<PropertyAccessCode>()
            .FirstOrDefaultAsync(ac => ac.PropertyId == booking.PropertyId, ct);

        if (accessCode == null)
            return null;

        return accessCode.EncryptedCode;
    }

    public async Task RecordReleaseAsync(Guid bookingId, string channel, CancellationToken ct = default)
    {
        if (!AccessCodeReleaseChannels.All.Contains(channel, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown release channel '{channel}'.", nameof(channel));

        var orgId = await _db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => b.Property.OrgId)
            .SingleAsync(ct);

        var releaseLog = new AccessCodeReleaseLog
        {
            OrgId = orgId,
            BookingId = bookingId,
            Channel = channel,
            ReleasedAt = DateTime.UtcNow
        };
        _db.Set<AccessCodeReleaseLog>().Add(releaseLog);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Access code released for booking {BookingId} via {Channel}",
            bookingId, channel);
    }

    public async Task<string?> GetHoldReasonAsync(Guid bookingId, CancellationToken ct = default) =>
        _touristTax is not null && await _touristTax.IsAccessCodeHeldBackAsync(bookingId, ct) ? AccessCodeHoldReasons.TouristTaxDue : null;

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

        var items = rows
            .Select(r => new AccessCodeStatusItem(r.PropertyId, r.Name, r.City, r.CodeUpdatedAt is not null, r.CodeUpdatedAt))
            .ToList();

        var isLastPage = items.Count < pageSize && (items.Count > 0 || skipped == 0);
        var total = isLastPage ? skipped + items.Count : await ReachableProperties(scope).CountAsync(ct);

        return new AccessCodeStatusPage(items, total, page, pageSize);
    }

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
    /// The verification gate of both reads of the code (AC-NF13-6, AC-NF50-4, AC-NF24-4, E03/F04/US02): the latest
    /// <see cref="BiometricVerificationLog"/> of the booking is an <see cref="BiometricOutcome.Accept"/>, written by the agent or by an operator's
    /// approval. Since E03/F04/US02/T03 an operator's rejection writes a row too (<see cref="VerificationMethod.Operator"/>, <c>Reject</c>): who
    /// decided does not open the gate, only an accepted outcome does. Internal so a test proves the gate.
    /// </summary>
    internal async Task<bool> IsVerificationPassedAsync(Guid bookingId, CancellationToken ct)
    {
        var verificationLog = await _db.BiometricVerificationLogs
            .Where(v => v.BookingId == bookingId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return verificationLog is { Outcome: BiometricOutcome.Accept };
    }
}

internal sealed record AccessCodeStatusRow(Guid PropertyId, string Name, string City, DateTime? CodeUpdatedAt);

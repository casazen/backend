using System.Buffers.Text;
using System.Data;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.GuestPortal;

/// <inheritdoc cref="IGuestPortalLinkService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The token</b> is <see cref="GuestPortalLink.TokenBytes"/> random bytes (192 bits) in base64url: 32 characters, nothing derived from an id.
/// It is unique in the whole table; a collision (one in 2<sup>192</sup>) is handled like the race below. It is never written to a log or to the
/// message of an exception: the logs carry the id of the link.</item>
/// <item><b>One active link for each booking</b> is the partial unique index of the model
/// (<see cref="Data.Configurations.GuestPortalLinkConfiguration.ActiveBookingIndexName"/>), not a check of this class: two requests that both find no
/// link end with one, the other gets a unique violation and looks again.</item>
/// <item><b>Regeneration</b> revokes the active link, saves it, and makes the new one, in a transaction. The revoke is saved first because the order
/// EF would run an update and an insert of the same unique index in one batch is not the one the index needs (the model of
/// <see cref="Services.PropertyWelcomeLinkService"/>).</item>
/// <item><b>Tenant</b>: the host methods name the org on top of the tenant filter of the request. The resolution of a token ignores the tenant filter
/// of the request (only that one, the soft deletes stay) and names the link's own booking and org: the token is the tenant of a portal request,
/// whoever is signed in.</item>
/// </list>
/// </remarks>
public class GuestPortalLinkService(
    AppDbContext db,
    PublicSiteLinks siteLinks,
    TimeProvider timeProvider,
    ILogger<GuestPortalLinkService> logger) : IGuestPortalLinkService
{
    /// <summary>How many times a write is repeated when another writer won the unique index at the same moment.</summary>
    private const int MaxAttempts = 3;

    public async Task<GuestPortalLink?> GetActiveAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        return await db.GuestPortalLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OrgId == orgId && l.BookingId == bookingId && l.RevokedAt == null, cancellationToken);
    }

    public async Task<GuestPortalLinkResult?> GetOrCreateAsync(
        Guid orgId,
        Guid bookingId,
        string? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var booking = await FindBookingAsync(orgId, bookingId, cancellationToken);
        if (IneligibleReason(booking) is not null)
            return null;

        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.GuestPortalLinks
                .FirstOrDefaultAsync(l => l.OrgId == orgId && l.BookingId == bookingId && l.RevokedAt == null, cancellationToken);
            if (existing is not null)
            {
                await ExtendAsync(existing, booking.CheckOutDate, cancellationToken);
                return new GuestPortalLinkResult(existing, false);
            }

            var link = NewLink(orgId, bookingId, booking.CheckOutDate, createdByUserId);
            db.GuestPortalLinks.Add(link);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Stay link {LinkId} made for booking {BookingId} of org {OrgId}", link.Id, bookingId, orgId);
                return new GuestPortalLinkResult(link, true);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && CrmDatabaseErrors.IsUniqueViolation(ex))
            {
                // Another request made the active link of the booking at the same moment: look again, and take theirs.
                Forget();
                logger.LogInformation("The stay link of booking {BookingId} was made by another request at the same moment", bookingId);
            }
        }
    }

    public async Task<GuestPortalLink?> RegenerateAsync(
        Guid orgId,
        Guid bookingId,
        string? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var booking = await FindBookingAsync(orgId, bookingId, cancellationToken);
        if (IneligibleReason(booking) is not null)
            return null;

        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);

            // The old link is revoked, and saved, before the new one is added: the unique index lets one active link of the booking in.
            var active = await ActiveLinksAsync(orgId, bookingId, cancellationToken);
            var now = UtcNow();
            foreach (var old in active)
                old.RevokedAt = now;

            if (active.Count > 0)
                await db.SaveChangesAsync(cancellationToken);

            var link = NewLink(orgId, bookingId, booking.CheckOutDate, createdByUserId);
            db.GuestPortalLinks.Add(link);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Stay link of booking {BookingId} of org {OrgId} regenerated: {Revoked} revoked, {LinkId} is the active one",
                    bookingId,
                    orgId,
                    active.Count,
                    link.Id);
                return link;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && CrmDatabaseErrors.IsUniqueViolation(ex))
            {
                // Another regeneration won at the same moment: this attempt is undone (the transaction ends without a commit) and made again from what is
                // stored.
                Forget();
                logger.LogInformation("The stay link of booking {BookingId} was regenerated by another request at the same moment", bookingId);
            }
        }
    }

    public async Task<bool> RevokeAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        await FindBookingAsync(orgId, bookingId, cancellationToken);

        var active = await ActiveLinksAsync(orgId, bookingId, cancellationToken);
        if (active.Count == 0)
            return false;

        var now = UtcNow();
        foreach (var link in active)
            link.RevokedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Stay link {LinkId} of booking {BookingId} of org {OrgId} revoked",
            active[0].Id,
            bookingId,
            orgId);
        return true;
    }

    public async Task<GuestPortalLink?> ResolveAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!GuestPortalRules.IsWellFormedToken(token))
            return null;

        // One query for every well-formed token, whatever it opens: unknown, revoked, of a request, of an org that is not active, expired.
        var resolvable = GuestPortalRules.ResolvableStatuses;
        var row = await (
                from link in db.GuestPortalLinks.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                join booking in db.Bookings.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]) on link.BookingId equals booking.Id
                join org in db.Orgs on link.OrgId equals org.Id
                where link.Token == token
                      && link.RevokedAt == null
                      && booking.OrgId == link.OrgId
                      && org.IsActive
                      && resolvable.Contains(booking.Status)
                select new { Link = link, booking.CheckOutDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var expiry = GuestPortalRules.EffectiveExpiry(row.Link.ExpiresAt, row.CheckOutDate);
        if (expiry <= UtcNow())
            return null;

        await ExtendAsync(row.Link, row.CheckOutDate, cancellationToken);
        return row.Link;
    }

    public string BuildUrl(GuestPortalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return siteLinks.GuestPortal(link.Token);
    }

    /// <summary>The facts of the booking the rules read; a booking that is not in the org is a 404 (TN-2).</summary>
    private async Task<BookingFacts> FindBookingAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == orgId)
            .Select(b => new BookingFacts(b.Status, b.NoShowAt, b.CheckOutDate))
            .FirstOrDefaultAsync(cancellationToken);

        return booking
               ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
    }

    private string? IneligibleReason(BookingFacts booking) =>
        GuestPortalRules.IneligibleReason(booking.Status, booking.NoShowAt, booking.CheckOutDate, UtcNow());

    private Task<List<GuestPortalLink>> ActiveLinksAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken) =>
        db.GuestPortalLinks
            .Where(l => l.OrgId == orgId && l.BookingId == bookingId && l.RevokedAt == null)
            .ToListAsync(cancellationToken);

    /// <summary>Moves the expiry of a tracked link on when the departure moved later; never shortens it.</summary>
    private async Task ExtendAsync(GuestPortalLink link, DateTime checkOutDate, CancellationToken cancellationToken)
    {
        var expiry = GuestPortalRules.EffectiveExpiry(link.ExpiresAt, checkOutDate);
        if (expiry <= link.ExpiresAt)
            return;

        link.ExpiresAt = expiry;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Stay link {LinkId} extended to {ExpiresAt}: the departure moved later", link.Id, expiry);
    }

    private GuestPortalLink NewLink(Guid orgId, Guid bookingId, DateTime checkOutDate, string? createdByUserId)
    {
        var createdBy = createdByUserId?.Trim();
        return new GuestPortalLink
        {
            OrgId = orgId,
            BookingId = bookingId,
            Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(GuestPortalLink.TokenBytes)),
            CreatedAt = UtcNow(),
            ExpiresAt = GuestPortalRules.ExpiryFor(checkOutDate),
            CreatedByUserId = string.IsNullOrEmpty(createdBy)
                ? null
                : createdBy.Length <= GuestPortalLink.UserIdMaxLength
                    ? createdBy
                    : createdBy[..GuestPortalLink.UserIdMaxLength],
        };
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Lets go of the links of the unit of work after a refused save, so that the next attempt reads what is stored.</summary>
    private void Forget()
    {
        foreach (var entry in db.ChangeTracker.Entries<GuestPortalLink>().ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>A transaction on a relational database, unless the caller already opened one; none on the in-memory store of the tests.</summary>
    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    private sealed record BookingFacts(BookingStatus Status, DateTime? NoShowAt, DateTime CheckOutDate);
}

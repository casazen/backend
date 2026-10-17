using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class GuestRepository(AppDbContext context) : IGuestRepository
{
    private static readonly BookingStatus[] OpenBookingStatuses =
        [BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.CheckedIn];

    public async Task<Guest?> GetByIdAsync(Guid id)
    {
        return await context.Guests
            .Include(g => g.Bookings)
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public async Task<Guest?> GetByIdInScopeAsync(HostScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return await GuestsInScope(scope)
            .Include(g => g.Bookings)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
    }

    public async Task<Guest?> GetByEmailAsync(HostScope scope, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var normalized = email.Trim().ToLower();
        return await GuestsInScope(scope)
            .Where(g => g.Email.ToLower() == normalized)
            .OrderByDescending(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Guest> Items, int TotalCount)> GetPageAsync(
        HostScope scope,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // PO 2026-10-08: the host list is bookers only. Companions live on StayGuest rows and are not guest cards.
        var query = GuestsInScope(scope)
            .AsNoTracking()
            .Where(g => !g.IsDeleted && g.Bookings.Any());

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            var lowerTerm = term.ToLower();
            query = query.Where(g => g.FirstName.ToLower().Contains(lowerTerm) ||
                                     g.LastName.ToLower().Contains(lowerTerm) ||
                                     g.Email.ToLower().Contains(lowerTerm) ||
                                     g.PhoneNumber.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<Guest> AddAsync(Guest guest)
    {
        context.Guests.Add(guest);
        await context.SaveChangesAsync();
        return guest;
    }

    public async Task<Guest> UpdateAsync(Guest guest)
    {
        guest.UpdatedAt = DateTime.UtcNow;
        context.Guests.Update(guest);
        await context.SaveChangesAsync();
        return guest;
    }

    public async Task DeleteAsync(Guid id)
    {
        var guest = await context.Guests.FindAsync(id);
        if (guest != null)
        {
            context.Guests.Remove(guest);
            await context.SaveChangesAsync();
        }
    }

    public async Task<bool> ExistsAsync(Guid id)
    {
        return await context.Guests.AnyAsync(g => g.Id == id);
    }

    public async Task<bool> ExistsByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLower();
        return await context.Guests.AnyAsync(
            g => g.OrgId == orgId && g.Email.ToLower() == normalized,
            cancellationToken);
    }

    public async Task<GuestUsage> GetUsageAsync(Guid guestId, DateTime today, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: the Restrict FKs count every referencing row, whatever its org.
        var bookings = context.Bookings.IgnoreQueryFilters().AsNoTracking().Where(b => b.GuestId == guestId);

        var hasOpenBookings = await bookings.AnyAsync(
            b => OpenBookingStatuses.Contains(b.Status),
            cancellationToken);
        var hasReferences = hasOpenBookings
            || await bookings.AnyAsync(cancellationToken)
            || await context.AlloggiatiWebReports.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(r => r.GuestId == guestId, cancellationToken);

        return new GuestUsage(hasReferences, hasOpenBookings);
    }

    /// <summary>
    /// The guests of the org of <paramref name="scope"/> that the scope reaches (SR-04b): the whole org for an org-wide scope,
    /// otherwise the guests with a stay at one of the properties it reaches. Every read of a guest for the host starts here, so
    /// the document number and the scan of a guest follow the same rule as the guest.
    /// </summary>
    private IQueryable<Guest> GuestsInScope(HostScope scope) =>
        context.Guests
            .Where(g => g.OrgId == scope.OrgId)
            .InScope(scope, context.Bookings);
}

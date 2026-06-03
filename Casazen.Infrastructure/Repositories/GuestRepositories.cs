using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class GuestRepository(AppDbContext context) : IGuestRepository
{
    public async Task<Guest?> GetByIdAsync(Guid id)
    {
        return await context.Guests
            .Include(g => g.Bookings)
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public async Task<Guest?> GetByEmailAsync(string email)
    {
        return await context.Guests
            .Include(g => g.Bookings)
            .FirstOrDefaultAsync(g => g.Email.ToLower() == email.ToLower());
    }

    public async Task<IEnumerable<Guest>> GetAllAsync()
    {
        return await context.Guests
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Guest>> SearchAsync(string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return await GetAllAsync();

        var lowerSearchTerm = searchTerm.ToLower();
        return await context.Guests
            .Where(g => g.FirstName.ToLower().Contains(lowerSearchTerm) ||
                       g.LastName.ToLower().Contains(lowerSearchTerm) ||
                       g.Email.ToLower().Contains(lowerSearchTerm) ||
                       g.PhoneNumber.Contains(searchTerm))
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
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

    public async Task<bool> ExistsByEmailAsync(string email)
    {
        return await context.Guests.AnyAsync(g => g.Email.ToLower() == email.ToLower());
    }
}

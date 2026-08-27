using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IGuestService
{
    Task<Guest?> GetGuestAsync(Guid id);
    Task<Guest?> GetGuestByEmailAsync(string email);
    Task<IEnumerable<Guest>> GetAllGuestsAsync();
    Task<IEnumerable<Guest>> SearchGuestsAsync(string? searchTerm);
    Task<Guest> CreateGuestAsync(Guest guest);
    Task<Guest> CreateGuestSnapshotAsync(Guest guest);
    Task<Guest> UpdateGuestAsync(Guest guest);
    Task<bool> DeleteGuestAsync(Guid id);
}

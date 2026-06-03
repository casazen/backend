using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IGuestRepository
{
    Task<Guest?> GetByIdAsync(Guid id);
    Task<Guest?> GetByEmailAsync(string email);
    Task<IEnumerable<Guest>> GetAllAsync();
    Task<IEnumerable<Guest>> SearchAsync(string? searchTerm);
    Task<Guest> AddAsync(Guest guest);
    Task<Guest> UpdateAsync(Guest guest);
    Task DeleteAsync(Guid id);
    Task<bool> ExistsAsync(Guid id);
    Task<bool> ExistsByEmailAsync(string email);
}

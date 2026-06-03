using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(string id);
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetBySubAsync(string sub);
    Task<IEnumerable<User>> GetAllAsync();
    Task<(IEnumerable<User> Users, int TotalCount)> GetPagedAsync(string? search, string? role, bool? isActive, int page, int pageSize);
    Task<User> AddAsync(User user);
    Task UpdateAsync(User user);
    Task DeleteAsync(string id);
}

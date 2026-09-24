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

    /// <summary>
    /// Inserts <paramref name="user"/> unless a row with the same <c>Id</c> already exists, and returns the stored
    /// row. Two first-access requests of the same Auth0 user may both try the insert (A1-14): the one that loses
    /// gets the row of the winner instead of a primary-key violation.
    /// </summary>
    Task<(User User, bool Created)> AddIfAbsentAsync(User user);
    Task UpdateAsync(User user);
    Task DeleteAsync(string id);
}

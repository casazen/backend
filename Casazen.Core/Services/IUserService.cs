using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IUserService
{
    Task<User?> GetUserAsync(string id);
    Task<User?> GetUserByEmailAsync(string email);
    Task<User> RegisterUserAsync(string email, string firstName, string lastName, string password);
    Task<User> UpdateUserAsync(User user);
    Task<bool> DeleteUserAsync(string id);
    Task<bool> ValidateCredentialsAsync(string email, string password);
}

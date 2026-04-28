using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class UserService(
    IUserRepository repository,
    ILogger<UserService> logger) : IUserService
{
    public async Task<User?> GetUserAsync(string id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        return await repository.GetByEmailAsync(email);
    }

    public async Task<User> RegisterUserAsync(string email, string firstName, string lastName, string password)
    {
        // Check if user already exists
        var existingUser = await repository.GetByEmailAsync(email);
        if (existingUser != null)
        {
            logger.LogWarning("User registration failed: Email {Email} already exists", email);
            throw new InvalidOperationException($"User with email {email} already exists");
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new ArgumentException("Invalid email address", nameof(email));

        if (string.IsNullOrWhiteSpace(firstName))
            throw new ArgumentException("First name is required", nameof(firstName));

        if (string.IsNullOrWhiteSpace(lastName))
            throw new ArgumentException("Last name is required", nameof(lastName));

        // TODO: Create user in Auth0 via Management API
        // For now, create local user record with Auth0 sub as ID
        // In production, this would be called after Auth0 user creation succeeds

        var user = new User
        {
            Id = Guid.NewGuid().ToString(), // Will be replaced with Auth0 sub after creation
            Email = email.ToLowerInvariant(),
            FirstName = firstName,
            LastName = lastName,
            Role = UserRole.PropertyOwner,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await repository.AddAsync(user);

        logger.LogInformation("User registered: {UserId}, Email: {Email}", user.Id, user.Email);

        return user;
    }

    public async Task<User> UpdateUserAsync(User user)
    {
        var existing = await repository.GetByIdAsync(user.Id);
        if (existing == null)
            throw new KeyNotFoundException($"User {user.Id} not found");

        await repository.UpdateAsync(user);
        logger.LogInformation("User updated: {UserId}", user.Id);

        return user;
    }

    public async Task<bool> DeleteUserAsync(string id)
    {
        var user = await repository.GetByIdAsync(id);
        if (user == null)
            return false;

        // TODO: Delete user from Auth0
        await repository.DeleteAsync(id);

        logger.LogInformation("User deleted: {UserId}", id);
        return true;
    }

    public async Task<bool> ValidateCredentialsAsync(string email, string password)
    {
        // TODO: Validate credentials via Auth0
        // This should call Auth0 authentication endpoint
        // For now, return false as it's not implemented

        logger.LogWarning("ValidateCredentialsAsync called but not implemented - should use Auth0");
        return false;
    }
}

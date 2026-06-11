using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class UserService(
    IUserRepository repository,
    IAuth0ManagementService auth0Management,
    IOrgService orgService,
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
            logger.LogWarning("User registration failed: Email already exists for userId {UserId}", existingUser.Id);
            throw new InvalidOperationException($"User with email {email} already exists");
        }

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new ArgumentException("Invalid email address", nameof(email));

        if (string.IsNullOrWhiteSpace(firstName))
            throw new ArgumentException("First name is required", nameof(firstName));

        if (string.IsNullOrWhiteSpace(lastName))
            throw new ArgumentException("Last name is required", nameof(lastName));

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            Email = email.ToLowerInvariant(),
            FirstName = firstName,
            LastName = lastName,
            Role = UserRole.PropertyOwner,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await repository.AddAsync(user);
        logger.LogInformation("User registered: {UserId}", user.Id);
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

        await repository.DeleteAsync(id); // now soft-delete
        logger.LogInformation("User deactivated: {UserId}", id);
        return true;
    }

    public async Task<bool> ValidateCredentialsAsync(string email, string password)
    {
        logger.LogWarning("ValidateCredentialsAsync called but not implemented — should use Auth0");
        return false;
    }

    /// <inheritdoc />
    public async Task<User> GetCurrentUserAsync(string sub, string email, string firstName, string lastName)
    {
        // Upsert by sub (Auth0 sub == User.Id)
        var existing = await repository.GetBySubAsync(sub);
        if (existing != null)
        {
            var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : email.ToLowerInvariant();
            var changed = false;

            if (string.IsNullOrWhiteSpace(existing.Email) && normalizedEmail is not null)
            {
                existing.Email = normalizedEmail;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.FirstName) && !string.IsNullOrWhiteSpace(firstName))
            {
                existing.FirstName = firstName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.LastName) && !string.IsNullOrWhiteSpace(lastName))
            {
                existing.LastName = lastName;
                changed = true;
            }

            if (changed)
            {
                existing.UpdatedAt = DateTime.UtcNow;
                await repository.UpdateAsync(existing);
            }

            return existing;
        }

        var user = new User
        {
            Id = sub,
            Email = email.ToLowerInvariant(),
            FirstName = firstName,
            LastName = lastName,
            Role = UserRole.PropertyOwner,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await repository.AddAsync(user);
        logger.LogInformation("User auto-created on first login: {UserId}", user.Id);
        return user;
    }

    /// <inheritdoc />
    public async Task<(IEnumerable<User> Users, int TotalCount)> GetPagedAsync(
        string? search, string? role, bool? isActive, int page, int pageSize)
    {
        return await repository.GetPagedAsync(search, role, isActive, page, pageSize);
    }

    /// <inheritdoc />
    public async Task EnrichUsersFromAuth0Async(IList<User> users)
    {
        foreach (var user in users)
        {
            if (!string.IsNullOrWhiteSpace(user.Email) &&
                !string.IsNullOrWhiteSpace(user.FirstName) &&
                !string.IsNullOrWhiteSpace(user.LastName))
            {
                continue;
            }

            var profile = await auth0Management.GetUserProfileAsync(user.Id);
            if (profile is null)
                continue;

            var changed = false;

            if (string.IsNullOrWhiteSpace(user.Email) && !string.IsNullOrWhiteSpace(profile.Email))
            {
                user.Email = profile.Email.ToLowerInvariant();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(user.FirstName) && !string.IsNullOrWhiteSpace(profile.FirstName))
            {
                user.FirstName = profile.FirstName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(user.LastName) && !string.IsNullOrWhiteSpace(profile.LastName))
            {
                user.LastName = profile.LastName;
                changed = true;
            }

            if (changed)
            {
                user.UpdatedAt = DateTime.UtcNow;
                await repository.UpdateAsync(user);
            }
        }
    }

    /// <inheritdoc />
    public async Task ChangeRoleAsync(string id, UserRole newRole, string adminSub)
    {
        var user = await repository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"User {id} not found");

        user.Role = newRole;
        await repository.UpdateAsync(user);

        // Sync role on Auth0 (best-effort — service handles errors gracefully)
        await auth0Management.AssignRoleAsync(id, newRole);

        logger.LogInformation(
            "Role changed: userId={UserId} newRole={Role} changedBy={AdminId}",
            id, newRole, adminSub);
    }

    /// <inheritdoc />
    public async Task<(User User, IReadOnlyList<string> RolesAssigned)> CompleteOnboardingAsync(
        string sub,
        RentalType rentalType,
        PlanTier planTier,
        string email,
        string firstName,
        string lastName)
    {
        var roles = MapRentalTypeToRoles(rentalType);
        var user = await GetCurrentUserAsync(sub, email, firstName, lastName);

        user.RentalType = rentalType;
        user.Role = roles[0];
        user.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(user);

        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = email;

        await orgService.EnsureOrgForUserAsync(sub, email, displayName, planTier);

        user = await repository.GetByIdAsync(sub) ?? user;

        await auth0Management.AssignOnboardingRolesAsync(sub, roles);

        var assigned = roles.Select(r => r.ToString()).ToArray();
        logger.LogInformation(
            "Onboarding completed: userId={UserId} rentalType={RentalType} planTier={PlanTier} roles=[{Roles}]",
            sub, rentalType, planTier, string.Join(", ", assigned));

        return (user, assigned);
    }

    private static IReadOnlyList<UserRole> MapRentalTypeToRoles(RentalType rentalType) =>
        rentalType switch
        {
            RentalType.ShortTerm => [UserRole.PropertyOwner],
            RentalType.LongTerm => [UserRole.LongTermLandlord],
            RentalType.Both => [UserRole.PropertyOwner, UserRole.LongTermLandlord],
            _ => throw new ArgumentOutOfRangeException(nameof(rentalType), rentalType, "Unknown rental type")
        };
}

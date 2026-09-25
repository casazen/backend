using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class UserService(
    IUserRepository repository,
    IAuth0ManagementService auth0Management,
    IOrgService orgService,
    IUserContextMembershipService membershipService,
    IUserAuthorizationCache authorizationCache,
    ILogger<UserService> logger) : IUserService
{
    /// <summary>Roles driven by the onboarding rental-type choice. Other roles (Admin, Supplier…) are never touched.</summary>
    private static readonly UserRole[] OnboardingRoles =
    [
        UserRole.PropertyOwner,
        UserRole.LongTermLandlord,
    ];

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
            // PL-02: no host role before the onboarding and its consents.
            Role = UserRole.None,
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
        authorizationCache.Invalidate(user.Id);
        logger.LogInformation("User updated: {UserId}", user.Id);
        return user;
    }

    /// <inheritdoc />
    public async Task<UserActivationResult> DeactivateUserAsync(
        string id,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(id, actorId, StringComparison.Ordinal))
            throw new DomainRuleException(UserActivationErrors.CannotDeactivateSelf, "UserCannotDeactivateSelf");

        var (outcome, user) = await repository.SetActiveAsync(id, isActive: false, actorId, cancellationToken);
        if (outcome == UserActivationOutcome.NotFound || user is null)
            throw new NotFoundException($"User {id} not found");

        switch (outcome)
        {
            case UserActivationOutcome.ActorInactive:
                // The acting admin was deactivated by a parallel request: its next request gets 403 account_inactive.
                throw new UnauthorizedAccessException("The acting admin is no longer active.");
            case UserActivationOutcome.LastActiveAdmin:
                throw new DomainRuleException(UserActivationErrors.LastActiveAdmin, "UserLastActiveAdmin");
        }

        authorizationCache.Invalidate(id);

        // Block first: a blocked account gets no new token at all, refresh tokens included. Then remove the CasaZen roles,
        // remembering them before the removal so that the reactivation gives back exactly those.
        var sync = await auth0Management.SetBlockedAsync(id, blocked: true, cancellationToken);
        var current = await auth0Management.GetUserRolesAsync(id, cancellationToken);
        sync = sync.Combine(current.Sync);
        if (current.Sync.Succeeded && current.Roles.Count > 0)
        {
            user.SuspendedAuth0Roles = (user.SuspendedAuth0Roles ?? [])
                .Union(current.Roles.Select(r => r.ToString()), StringComparer.OrdinalIgnoreCase)
                .ToList();
            await repository.UpdateAsync(user);
            sync = sync.Combine(await auth0Management.RemoveRolesAsync(id, current.Roles, cancellationToken));
        }

        // Audit (no audit log table yet): who deactivated whom and when, ids only.
        logger.LogInformation(
            "User deactivated: userId={UserId} by={ActorId} at={AtUtc:o} changed={Changed} auth0Synced={Auth0Synced} auth0Error={Auth0Error} rolesSuspended=[{Roles}]",
            id, actorId, DateTime.UtcNow, outcome == UserActivationOutcome.Updated, sync.Succeeded, sync.ErrorCode,
            string.Join(", ", user.SuspendedAuth0Roles ?? []));

        return new UserActivationResult(user, outcome == UserActivationOutcome.Updated, sync, []);
    }

    /// <inheritdoc />
    public async Task<UserActivationResult> ReactivateUserAsync(
        string id,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        var (outcome, user) = await repository.SetActiveAsync(id, isActive: true, actorId, cancellationToken);
        if (outcome == UserActivationOutcome.NotFound || user is null)
            throw new NotFoundException($"User {id} not found");

        authorizationCache.Invalidate(id);

        // Roles back first, unblock only then: the first token after the reactivation already carries them. When the
        // roles cannot be given back the account stays blocked and the admin retries.
        var suspended = ParseRoles(user.SuspendedAuth0Roles);
        var sync = Auth0SyncResult.Synced;
        if (suspended.Count > 0)
        {
            sync = await auth0Management.AssignRolesAsync(id, suspended, cancellationToken);
            if (sync.Succeeded)
            {
                user.SuspendedAuth0Roles = null;
                await repository.UpdateAsync(user);
            }
        }

        if (sync.Succeeded)
            sync = await auth0Management.SetBlockedAsync(id, blocked: false, cancellationToken);

        logger.LogInformation(
            "User reactivated: userId={UserId} by={ActorId} at={AtUtc:o} changed={Changed} auth0Synced={Auth0Synced} auth0Error={Auth0Error} rolesRestored=[{Roles}]",
            id, actorId, DateTime.UtcNow, outcome == UserActivationOutcome.Updated, sync.Succeeded, sync.ErrorCode,
            sync.Succeeded ? string.Join(", ", suspended) : string.Empty);

        return new UserActivationResult(
            user,
            outcome == UserActivationOutcome.Updated,
            sync,
            sync.Succeeded ? suspended : []);
    }

    private static IReadOnlyList<UserRole> ParseRoles(IEnumerable<string>? names) =>
        (names ?? [])
            .Select(n => Enum.TryParse<UserRole>(n, ignoreCase: true, out var role) ? role : (UserRole?)null)
            .OfType<UserRole>()
            .Distinct()
            .ToList();

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
            // PL-02 (A1-05): a user registered on Auth0 has no host role, hence no host context, until the onboarding
            // sets it together with the legal consents (CompleteOnboardingAsync).
            Role = UserRole.None,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var (stored, created) = await repository.AddIfAbsentAsync(user);
        authorizationCache.Invalidate(sub);
        if (created)
            logger.LogInformation("User auto-created on first login: {UserId}", sub);
        else
            logger.LogInformation("User {UserId} was created by a parallel first request, reusing it", sub);
        return stored;
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
    public async Task<Auth0SyncResult> ChangeRoleAsync(string id, UserRole newRole, string adminSub)
    {
        var user = await repository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"User {id} not found");

        // The Auth0 roles of a deactivated user are suspended (PL-03): a change now would be undone, or doubled, by the
        // reactivation that gives them back.
        if (!user.IsActive)
            throw new DomainRuleException(UserActivationErrors.UserInactive, "UserInactiveRoleChange");

        var oldRole = user.Role;

        // Auth0 first: if it fails nothing changes in the DB and the admin can simply retry.
        // Only the previous primary role is removed; any other role (Supplier, LongTermLandlord…) is kept.
        var sync = await auth0Management.AssignRoleAsync(id, newRole);
        if (sync.Succeeded && oldRole != newRole)
            sync = await auth0Management.RemoveRoleAsync(id, oldRole);

        if (!sync.Succeeded)
        {
            logger.LogWarning(
                "Role change not applied, Auth0 sync failed ({ErrorCode}): userId={UserId} oldRole={OldRole} newRole={NewRole} changedBy={AdminId}",
                sync.ErrorCode, id, oldRole, newRole, adminSub);
            return sync;
        }

        user.Role = newRole;
        user.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(user);

        if (oldRole != newRole)
            await membershipService.RevokeAsync(id, [oldRole]);
        await membershipService.GrantAsync(id, [newRole]);
        authorizationCache.Invalidate(id);

        logger.LogInformation(
            "Role changed: userId={UserId} oldRole={OldRole} newRole={NewRole} changedBy={AdminId}",
            id, oldRole, newRole, adminSub);

        return sync;
    }

    /// <inheritdoc />
    public async Task<Auth0UserRolesResult> GetRolesAsync(string id, CancellationToken cancellationToken = default)
    {
        _ = await repository.GetByIdAsync(id) ?? throw new KeyNotFoundException($"User {id} not found");
        return await auth0Management.GetUserRolesAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RoleSetUpdateResult> UpdateRolesAsync(
        string id, IReadOnlyCollection<UserRole> roles, string adminSub, CancellationToken cancellationToken = default)
    {
        var user = await repository.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"User {id} not found");

        // Same guard as ChangeRoleAsync: the roles of a deactivated user are suspended (PL-03).
        if (!user.IsActive)
            throw new DomainRuleException(UserActivationErrors.UserInactive, "UserInactiveRoleChange");

        var target = roles.Where(AdminManageableRoles.All.Contains).Distinct().ToHashSet();

        // Auth0 is the source of truth for the roles actually held: comparing against it (not against the single
        // User.Role field) is what lets a dual-role user (host + supplier, PL-16 "Both") keep every role it holds
        // that the admin did not touch.
        var currentRoles = await auth0Management.GetUserRolesAsync(id, cancellationToken);
        if (!currentRoles.Sync.Succeeded)
        {
            logger.LogWarning(
                "Roles update not applied, could not read current Auth0 roles ({ErrorCode}): userId={UserId} changedBy={AdminId}",
                currentRoles.Sync.ErrorCode, id, adminSub);
            return new RoleSetUpdateResult(user, [], [], [], currentRoles.Sync);
        }

        var current = currentRoles.Roles.Where(AdminManageableRoles.All.Contains).ToHashSet();
        var toGrant = target.Except(current).ToArray();
        var toRevoke = current.Except(target).ToArray();

        if (toGrant.Length == 0 && toRevoke.Length == 0)
            return new RoleSetUpdateResult(user, target.ToArray(), [], [], Auth0SyncResult.Synced);

        // Would this leave the platform with zero active admins? Checked before touching Auth0, same spirit as the
        // deactivation guard (UserRepository.SetActiveAsync).
        if (toRevoke.Contains(UserRole.Admin) && !await repository.HasOtherActiveAdminAsync(id, cancellationToken))
            throw new DomainRuleException(UserActivationErrors.LastActiveAdmin, "UserLastActiveAdminRoleChange");

        // Auth0 first: if it fails nothing changes in the DB and the admin can simply retry.
        var sync = await auth0Management.AssignRolesAsync(id, toGrant, cancellationToken);
        if (sync.Succeeded && toRevoke.Length > 0)
            sync = await auth0Management.RemoveRolesAsync(id, toRevoke, cancellationToken);

        if (!sync.Succeeded)
        {
            logger.LogWarning(
                "Roles update not applied, Auth0 sync failed ({ErrorCode}): userId={UserId} target=[{Target}] changedBy={AdminId}",
                sync.ErrorCode, id, string.Join(", ", target), adminSub);
            return new RoleSetUpdateResult(user, current.ToArray(), [], [], sync);
        }

        var oldRole = user.Role;
        user.Role = AdminManageableRoles.All.FirstOrDefault(target.Contains, UserRole.None);
        user.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(user);

        if (toRevoke.Length > 0)
            await membershipService.RevokeAsync(id, toRevoke, cancellationToken);
        if (toGrant.Length > 0)
            await membershipService.GrantAsync(id, toGrant, cancellationToken);
        authorizationCache.Invalidate(id);

        logger.LogInformation(
            "Roles changed: userId={UserId} oldRole={OldRole} newRole={NewRole} granted=[{Granted}] revoked=[{Revoked}] changedBy={AdminId}",
            id, oldRole, user.Role, string.Join(", ", toGrant), string.Join(", ", toRevoke), adminSub);

        return new RoleSetUpdateResult(user, target.ToArray(), toGrant, toRevoke, sync);
    }

    /// <inheritdoc />
    public async Task<(User User, IReadOnlyList<string> RolesAssigned, Auth0SyncResult RoleSync)> CompleteOnboardingAsync(
        string sub,
        RentalType rentalType,
        string email,
        string firstName,
        string lastName)
    {
        var roles = MapRentalTypeToRoles(rentalType);
        var user = await GetCurrentUserAsync(sub, email, firstName, lastName);

        user.RentalType = rentalType;
        // A platform admin who also sets up a host org stays Admin: the admin role is changed only from the
        // admin console, never by the onboarding choice (A1-01).
        if (user.Role != UserRole.Admin)
            user.Role = roles[0];
        user.UpdatedAt = DateTime.UtcNow;

        // Set the onboarding completion timestamp (immutable once set)
        if (user.OnboardingCompletedAt == null)
        {
            user.OnboardingCompletedAt = DateTime.UtcNow;
        }

        await repository.UpdateAsync(user);

        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = email;

        await orgService.EnsureOrgForUserAsync(sub, email, displayName);

        user = await repository.GetByIdAsync(sub) ?? user;

        // The rental-type choice defines the onboarding roles exactly: unselected ones are revoked
        // (DB membership and Auth0 role), selected ones are granted. Non-onboarding roles are untouched.
        var unselected = OnboardingRoles.Except(roles).ToArray();

        // DB memberships for ALL selected roles, so backend authorization does not depend on the JWT.
        await membershipService.RevokeAsync(sub, unselected);
        await membershipService.GrantAsync(sub, roles);

        var roleSync = await auth0Management.AssignRolesAsync(sub, roles);
        roleSync = roleSync.Combine(await auth0Management.RemoveRolesAsync(sub, unselected));
        authorizationCache.Invalidate(sub);

        var assigned = roles.Select(r => r.ToString()).ToArray();
        if (roleSync.Succeeded)
        {
            logger.LogInformation(
                "Onboarding completed: userId={UserId} rentalType={RentalType} roles=[{Roles}]",
                sub, rentalType, string.Join(", ", assigned));
        }
        else
        {
            logger.LogWarning(
                "Onboarding completed but Auth0 roles not synced ({ErrorCode}): userId={UserId} rentalType={RentalType} roles=[{Roles}]",
                roleSync.ErrorCode, sub, rentalType, string.Join(", ", assigned));
        }

        return (user, assigned, roleSync);
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

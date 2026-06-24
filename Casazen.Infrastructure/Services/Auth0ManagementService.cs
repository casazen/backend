using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Wraps the Auth0 Management API for role synchronisation.
/// Reads <c>Auth0:ManagementApiToken</c> and <c>Auth0:ManagementApiDomain</c> from configuration.
/// If either value is absent the service logs a warning and returns gracefully — it must never throw
/// in environments (e.g., Railway test) where the M2M token is not configured.
/// </summary>
public class Auth0ManagementService(
    IConfiguration configuration,
    ILogger<Auth0ManagementService> logger) : IAuth0ManagementService
{
    // Map C# enum values to Auth0 role names that match the Auth0 Action output.
    private static readonly Dictionary<UserRole, string> RoleNames = new()
    {
        { UserRole.Admin,            "Admin" },
        { UserRole.PropertyOwner,    "PropertyOwner" },
        { UserRole.PropertyManager,  "PropertyManager" },
        { UserRole.Guest,            "Guest" },
        { UserRole.Staff,            "Staff" },
        { UserRole.LongTermLandlord, "LongTermLandlord" },
        { UserRole.Supplier,         "Supplier" },
    };

    private static readonly UserRole[] OnboardingRoles =
    [
        UserRole.PropertyOwner,
        UserRole.LongTermLandlord
    ];

    /// <summary>
    /// Assigns a role to an Auth0 user via the Management API.
    /// All existing roles are removed; the new one is assigned.
    /// Silently skips if the token or domain is not configured.
    /// </summary>
    public async Task AssignRoleAsync(string userId, UserRole role)
    {
        var token = configuration["Auth0:ManagementApiToken"];
        var domain = configuration["Auth0:ManagementApiDomain"]
                     ?? configuration["Auth0:Domain"];

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(domain))
        {
            logger.LogWarning(
                "Auth0ManagementService: ManagementApiToken or Domain not configured — " +
                "skipping role sync for user {UserId}", userId);
            return;
        }

        if (!RoleNames.TryGetValue(role, out var roleName))
        {
            logger.LogWarning("Auth0ManagementService: No Auth0 role mapping for {Role}", role);
            return;
        }

        try
        {
            var client = new ManagementApiClient(token, new Uri($"https://{domain}/api/v2"));

            var existingRoles = await client.Users.GetRolesAsync(userId);
            if (existingRoles != null && existingRoles.Count > 0)
            {
                await client.Users.RemoveRolesAsync(userId, new AssignRolesRequest
                {
                    Roles = existingRoles.Select(r => r.Id).ToArray()
                });
            }

            var allRoles = await client.Roles.GetAllAsync(new GetRolesRequest { NameFilter = roleName });
            var targetRole = allRoles?.FirstOrDefault(r =>
                string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));

            if (targetRole == null)
            {
                logger.LogWarning(
                    "Auth0ManagementService: Role '{RoleName}' not found in Auth0 — " +
                    "skipping assignment for {UserId}", roleName, userId);
                return;
            }

            await client.Users.AssignRolesAsync(userId, new AssignRolesRequest
            {
                Roles = new[] { targetRole.Id }
            });

            logger.LogInformation(
                "Auth0ManagementService: Assigned role {RoleName} to user {UserId}",
                roleName, userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Auth0ManagementService: Failed to sync role {Role} for user {UserId}", role, userId);
        }
    }

    /// <inheritdoc />
    public async Task AssignOnboardingRolesAsync(string userId, IReadOnlyList<UserRole> roles)
    {
        var token = configuration["Auth0:ManagementApiToken"];
        var domain = configuration["Auth0:ManagementApiDomain"]
                     ?? configuration["Auth0:Domain"];

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(domain))
        {
            logger.LogWarning(
                "Auth0ManagementService: ManagementApiToken or Domain not configured — " +
                "skipping role sync for user {UserId}", userId);
            return;
        }

        var targetRoleNames = roles
            .Where(r => RoleNames.ContainsKey(r))
            .Select(r => RoleNames[r])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetRoleNames.Count == 0)
        {
            logger.LogWarning("Auth0ManagementService: No valid Auth0 role mapping for onboarding user {UserId}", userId);
            return;
        }

        try
        {
            var client = new ManagementApiClient(token, new Uri($"https://{domain}/api/v2"));
            var existingRoles = await client.Users.GetRolesAsync(userId);

            var onboardingRoleNames = OnboardingRoles
                .Select(r => RoleNames[r])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rolesToRemove = existingRoles
                .Where(r => onboardingRoleNames.Contains(r.Name ?? string.Empty))
                .Select(r => r.Id)
                .ToArray();

            if (rolesToRemove.Length > 0)
            {
                await client.Users.RemoveRolesAsync(userId, new AssignRolesRequest { Roles = rolesToRemove });
            }

            var allRoles = await client.Roles.GetAllAsync(new GetRolesRequest());
            var roleIdsToAssign = allRoles?
                .Where(r => targetRoleNames.Contains(r.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .Select(r => r.Id)
                .Distinct()
                .ToArray() ?? [];

            if (roleIdsToAssign.Length == 0)
            {
                logger.LogWarning(
                    "Auth0ManagementService: Target onboarding roles not found in Auth0 for user {UserId}",
                    userId);
                return;
            }

            await client.Users.AssignRolesAsync(userId, new AssignRolesRequest { Roles = roleIdsToAssign });

            logger.LogInformation(
                "Auth0ManagementService: Assigned onboarding roles [{Roles}] to user {UserId}",
                string.Join(", ", targetRoleNames), userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Auth0ManagementService: Failed to sync onboarding roles for user {UserId}", userId);
        }
    }

    /// <inheritdoc />
    public async Task<Auth0UserProfile?> GetUserProfileAsync(string userId)
    {
        var token = configuration["Auth0:ManagementApiToken"];
        var domain = configuration["Auth0:ManagementApiDomain"]
                     ?? configuration["Auth0:Domain"];

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(domain))
            return null;

        try
        {
            var client = new ManagementApiClient(token, new Uri($"https://{domain}/api/v2"));
            var auth0User = await client.Users.GetAsync(userId);
            if (auth0User is null)
                return null;

            var email = auth0User.Email ?? string.Empty;
            var firstName = auth0User.FirstName ?? string.Empty;
            var lastName = auth0User.LastName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(auth0User.FullName))
            {
                var parts = auth0User.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                firstName = parts[0];
                if (parts.Length > 1)
                    lastName = parts[1];
            }

            if (string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(auth0User.NickName))
                firstName = auth0User.NickName;

            return new Auth0UserProfile(email, firstName, lastName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auth0ManagementService: Failed to fetch profile for user {UserId}", userId);
            return null;
        }
    }
}

using System.Security.Claims;
using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The email of the signed-in account and whether Auth0 verified it: what an invitation to an org is bound to (AM-02).
/// </summary>
public interface IAccountEmailResolver
{
    /// <summary>
    /// The email of the account and its <c>email_verified</c>. <c>Email</c> is null when neither the token nor Auth0 tell it;
    /// <c>EmailVerified</c> is true only when something authoritative says so.
    /// </summary>
    Task<(string? Email, bool EmailVerified)> ResolveAsync(ClaimsPrincipal user, string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Access token claims first (the Auth0 Action claims <c>https://casazen.app/email</c> and <c>…/email_verified</c>, runbook
/// <c>auth0.md</c> § 6, then the standard <c>email</c> and <c>email_verified</c>); what they lack (Action not deployed yet)
/// comes from Auth0 itself through the Management API, never from a stored copy, because these values bind invitations to the
/// account. A verified flag read from Auth0 counts only when Auth0 reports the same email as the token. The same rule as the
/// supplier invitations (<c>SuppliersController</c>).
/// </summary>
public sealed class AccountEmailResolver(IAuth0ManagementService auth0Management) : IAccountEmailResolver
{
    private const string AccountEmailClaim = "https://casazen.app/email";
    private const string AccountEmailVerifiedClaim = "https://casazen.app/email_verified";

    public async Task<(string? Email, bool EmailVerified)> ResolveAsync(
        ClaimsPrincipal user,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var email = GetEmail(user);
        var verified = GetEmailVerified(user);
        if (email is not null && verified is not null)
            return (email, verified == true);

        var profile = await auth0Management.GetUserProfileAsync(userId);
        var auth0Email = string.IsNullOrWhiteSpace(profile?.Email) ? null : profile.Email.Trim();
        email ??= auth0Email;
        if (verified is null && auth0Email is not null && string.Equals(auth0Email, email, StringComparison.OrdinalIgnoreCase))
            verified = profile!.EmailVerified;

        return (email, verified == true);
    }

    /// <summary>The email claim of the access token: the Auth0 Action claim, else the standard ones.</summary>
    public static string? GetEmail(ClaimsPrincipal user) =>
        new[] { AccountEmailClaim, "email", ClaimTypes.Email }
            .Select(user.FindFirstValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();

    /// <summary>The verified flag of the access token; null when the token carries none (or an unreadable value).</summary>
    public static bool? GetEmailVerified(ClaimsPrincipal user)
    {
        var value = new[] { AccountEmailVerifiedClaim, "email_verified" }
            .Select(user.FindFirstValue)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return bool.TryParse(value?.Trim(), out var verified) ? verified : null;
    }
}

using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Authorization;

/// <summary>Names of the authentication scheme and the claims of a guest session (DB-06a).</summary>
public static class GuestSessionDefaults
{
    /// <summary>The authentication scheme of the policy <see cref="CasazenPolicies.GuestSession"/>; the only one that policy uses.</summary>
    public const string AuthenticationScheme = "GuestSession";

    /// <summary>The org of the site the session was opened on.</summary>
    public const string OrgIdClaim = "cz_guest_org";

    /// <summary>The hash of the guest's email (never the address).</summary>
    public const string EmailHashClaim = "cz_guest_email";

    /// <summary>When the session ends, in Unix seconds (UTC).</summary>
    public const string ExpiresAtClaim = "cz_guest_exp";
}

/// <summary>
/// Reads the guest-session token of <c>Authorization: Bearer gs1.…</c> (DB-06a, <see cref="IGuestSessionService.Validate"/>) and, when it
/// is valid, makes a principal that carries the org, the email hash and the end of the session: no <c>sub</c>, no role, nothing that a
/// host endpoint reads as a person. Only the policy <see cref="CasazenPolicies.GuestSession"/> uses this scheme, so a guest token opens
/// nothing else and a host's token opens no guest action.
/// </summary>
/// <remarks>
/// <para>A session is bound to its org for the rest of the request: the tenant of the request is the org of the site, so the EF
/// tenant filter (on for any authenticated request) lets through only that org's rows and the queries of the guest area are
/// double-checked by the filter.</para>
/// <para>A missing header or another kind of token is "no result"; a token of the guest scheme that does not open (altered, expired)
/// fails, and the caller gets the usual 401.</para>
/// </remarks>
public sealed class GuestSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IGuestSessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = header[BearerPrefix.Length..].Trim();
        if (!GuestSessionTokens.IsGuestToken(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var ticket = sessions.Validate(token);
        if (ticket is null)
            return Task.FromResult(AuthenticateResult.Fail("The guest session is not valid or has expired."));

        try
        {
            Context.RequestServices.GetRequiredService<IRequestTenantContext>().SetOrgId(ticket.OrgId);
        }
        catch (InvalidOperationException)
        {
            // The request already belongs to another org: never mix the two.
            return Task.FromResult(AuthenticateResult.Fail("The request belongs to another org."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(GuestSessionDefaults.OrgIdClaim, ticket.OrgId.ToString("D")),
                new Claim(GuestSessionDefaults.EmailHashClaim, ticket.EmailHash),
                new Claim(
                    GuestSessionDefaults.ExpiresAtClaim,
                    new DateTimeOffset(ticket.ExpiresAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

/// <summary>What an action of the guest area reads from its caller.</summary>
public static class GuestSessionPrincipalExtensions
{
    /// <summary>
    /// The ticket of a guest session (org, email hash, end of the session), or <c>null</c> when the principal is not one. Behind the policy
    /// <see cref="CasazenPolicies.GuestSession"/> it is never <c>null</c>.
    /// </summary>
    public static GuestSessionTicket? GetGuestSessionTicket(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!Guid.TryParse(user.FindFirstValue(GuestSessionDefaults.OrgIdClaim), out var orgId)
            || user.FindFirstValue(GuestSessionDefaults.EmailHashClaim) is not { Length: > 0 } emailHash
            || !long.TryParse(user.FindFirstValue(GuestSessionDefaults.ExpiresAtClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt))
        {
            return null;
        }

        return new GuestSessionTicket(orgId, emailHash, DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime);
    }
}

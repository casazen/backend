using System.Security.Claims;
using System.Text.Encodings.Web;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Authorization;

/// <summary>Names of the authentication scheme and the claims of a connected site (S022, PLG-BE-CONNECT, site integration plan §10.3).</summary>
public static class SiteTokenDefaults
{
    /// <summary>The scheme of the credential of a connected site: only the policy <see cref="CasazenPolicies.SiteConnection"/> uses it.</summary>
    public const string AuthenticationScheme = "SiteToken";

    /// <summary>The connection the credential is.</summary>
    public const string ConnectionIdClaim = "site_connection_id";

    /// <summary>The org of the credential: the only org a call of the plugin acts on.</summary>
    public const string OrgIdClaim = "org_id";

    /// <summary>The claim of what the credential may do.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>The one scope of a credential: the operations of the domain of its org (<c>api/site/v1/*</c>).</summary>
    public const string DomainScope = "site:domain";
}

/// <summary>
/// Reads the credential of a connected site in <c>Authorization: Bearer czs_…</c> (S022, PLG-BE-CONNECT, NF-204), finds its active connection through
/// <see cref="ISiteConnectionService.AuthenticateAsync"/> (by the hash of the credential, the time of use written at most every five minutes) and makes a
/// principal with the connection, its org and the scope <see cref="SiteTokenDefaults.DomainScope"/>: no <c>sub</c>, no role, nothing a console endpoint
/// reads as a person.
/// </summary>
/// <remarks>
/// <para>Not the default scheme: only the policy <see cref="CasazenPolicies.SiteConnection"/> (the endpoints <c>api/site/v1/*</c>) authenticates with it, so a
/// credential opens no console endpoint (the JWT bearer handler leaves a <c>czs_</c> value alone, the request is anonymous there: 401) and a sign-in of a
/// person opens no endpoint of the plugin (this scheme ignores it: 401).</para>
/// <para>The request then belongs to the org of the credential: the tenant of the request is that org, so the EF tenant filter scopes every read of the
/// plugin's endpoints to it. The org is never read from the request.</para>
/// <para>A missing header or another kind of token is "no result"; a credential that does not open (unknown, revoked, of an org no longer active) fails, and
/// the caller gets 401 with no detail.</para>
/// </remarks>
public sealed class SiteTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISiteConnectionService connections) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header[BearerPrefix.Length..].Trim();
        if (!SiteConnectionTokens.HasCredentialPrefix(token))
            return AuthenticateResult.NoResult();

        var connection = await connections.AuthenticateAsync(token, Context.RequestAborted);
        if (connection is null)
            return AuthenticateResult.Fail("The site credential is not valid or was revoked.");

        try
        {
            Context.RequestServices.GetRequiredService<IRequestTenantContext>().SetOrgId(connection.OrgId);
        }
        catch (InvalidOperationException)
        {
            // The request already belongs to another org: never mix the two.
            return AuthenticateResult.Fail("The request belongs to another org.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(SiteTokenDefaults.ConnectionIdClaim, connection.ConnectionId.ToString("D")),
                new Claim(SiteTokenDefaults.OrgIdClaim, connection.OrgId.ToString("D")),
                new Claim(SiteTokenDefaults.ScopeClaim, SiteTokenDefaults.DomainScope),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>401 with <c>WWW-Authenticate: Bearer</c> and no detail; the body is the standard problem of an empty 401.</summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

/// <summary>What an endpoint of the plugin reads from its caller.</summary>
public static class SiteTokenPrincipalExtensions
{
    /// <summary>
    /// The connection and the org of the credential of the request, read from the identity of the scheme <see cref="SiteTokenDefaults.AuthenticationScheme"/>
    /// only; <c>null</c> when the caller is not a connected site. Behind the policy <see cref="CasazenPolicies.SiteConnection"/> it is never <c>null</c>.
    /// </summary>
    public static SiteConnectionPrincipal? GetSiteConnection(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = user.Identities.FirstOrDefault(i =>
            i.IsAuthenticated && string.Equals(i.AuthenticationType, SiteTokenDefaults.AuthenticationScheme, StringComparison.Ordinal));
        if (identity is null
            || !identity.HasClaim(SiteTokenDefaults.ScopeClaim, SiteTokenDefaults.DomainScope)
            || !Guid.TryParse(identity.FindFirst(SiteTokenDefaults.ConnectionIdClaim)?.Value, out var connectionId)
            || !Guid.TryParse(identity.FindFirst(SiteTokenDefaults.OrgIdClaim)?.Value, out var orgId))
        {
            return null;
        }

        return new SiteConnectionPrincipal(connectionId, orgId);
    }
}

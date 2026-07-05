using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Integration;

/// <summary>
/// Test authentication handler activated when the Authorization header is present.
/// User id and roles are supplied via X-Test-User and X-Test-Roles headers.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string DefaultUserId = "auth0|pricing-test-owner";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("Authorization"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var userId = Request.Headers["X-Test-User"].FirstOrDefault() ?? DefaultUserId;
        var rolesHeader = Request.Headers["X-Test-Roles"].FirstOrDefault();
        var email = Request.Headers["X-Test-Email"].FirstOrDefault();

        var claims = new List<Claim> { new("sub", userId) };
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (!string.IsNullOrWhiteSpace(rolesHeader))
        {
            foreach (var role in rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                // Mirror the Auth0 custom-roles claim so context-permission fallback resolves
                // (ContextAuthorizationService reads "https://casazen.app/roles"), matching prod JWTs.
                claims.Add(new Claim("https://casazen.app/roles", role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

using System.Security.Claims;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The JWT bearer sign-in of the hosts (Auth0), the scheme of the guest session and the scheme of the plugin of a connected site.
/// </summary>
public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenAuthentication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Domain and Audience validate every JWT: outside Development/Testing the app does not start without them (FD-12).
        var auth0Options = services.AddOptions<Auth0Options>()
            .Bind(configuration.GetSection(Auth0Options.SectionName));
        if (RequiredConfiguration.IsEnforced(environment))
        {
            services.AddSingleton<IValidateOptions<Auth0Options>, Auth0OptionsValidator>();
            auth0Options.ValidateOnStart();
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var domain = configuration["Auth0:Domain"];
                var audience = configuration["Auth0:Audience"];

                options.Authority = $"https://{domain}";
                options.Audience = audience;

                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://{domain}/",
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
                    RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // WebSockets and server-sent events carry the token of a SignalR connection in the query (hubs only).
                        if (context.Request.Headers.Authorization.Count == 0 && HubAccessToken.TryRead(context.Request, out var hubToken))
                        {
                            context.Token = hubToken;
                            return Task.CompletedTask;
                        }

                        // The token of a guest session (DB-06a) is not a JWT: only the GuestSession scheme reads it. The sign-in
                        // of the hosts leaves it alone, so it neither tries to validate it nor logs a failure at every call. The same for
                        // the credential of a connected site (czs_…, S022): only the SiteToken scheme reads it, so on a console endpoint
                        // the request is anonymous (401).
                        var authorization = context.Request.Headers.Authorization.ToString();
                        if (authorization.StartsWith("Bearer " + GuestSessionTokens.Prefix, StringComparison.OrdinalIgnoreCase)
                            || authorization.StartsWith("Bearer " + SiteConnectionTokens.CredentialPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            context.NoResult();
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerHandler>>();
                        logger.LogWarning("Authentication failed: {Error}", context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerHandler>>();
                        logger.LogWarning("Auth challenge — error: {Error}, description: {Description}",
                            context.Error, context.ErrorDescription);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        // Map Auth0 custom roles claim to standard .NET role claims
                        if (context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            var roles = Auth0RolesClaimParser.Parse(
                                context.Principal.FindAll("https://casazen.app/roles").Select(c => c.Value));

                            foreach (var role in roles)
                            {
                                identity.AddClaim(new Claim(
                                    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                                    role));
                            }

                            // Backfill Supplier role from DB. A user just linked to a supplier
                            // org (registration, invite or claim, SU-02) has SupplierOrgId set but
                            // no Supplier role in the Auth0 JWT until a new token (or at all when
                            // the role sync failed). Adding the claim here lets the
                            // [Authorize(Policy="RequireSupplier")] filter pass right away.
                            var sub = context.Principal.FindFirstValue("sub")
                                ?? context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

                            if (!string.IsNullOrWhiteSpace(sub))
                            {
                                var hasSupplierRole = identity.Claims.Any(c =>
                                    c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                                    && c.Value == "Supplier");

                                if (!hasSupplierRole)
                                {
                                    try
                                    {
                                        // Cached per request and for a short time per user (A4-30):
                                        // no DB round-trip on every authenticated call.
                                        var snapshots = context.HttpContext.RequestServices
                                            .GetRequiredService<IUserAuthorizationSnapshotStore>();
                                        var snapshot = await snapshots.GetAsync(
                                            sub, context.HttpContext.RequestAborted);

                                        if (snapshot.SupplierOrgId is not null)
                                        {
                                            identity.AddClaim(new Claim(
                                                "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                                                "Supplier"));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var logger = context.HttpContext.RequestServices
                                            .GetRequiredService<ILogger<JwtBearerHandler>>();
                                        logger.LogWarning(ex,
                                            "Failed to backfill Supplier role for user {Sub}", sub);
                                    }
                                }
                            }
                        }
                    }
                };

                // Disable HTTPS requirement in development only
                if (environment.IsDevelopment())
                {
                    options.RequireHttpsMetadata = false;
                }
            })
            // The guest of a booking site with no account (DB-06a): its own scheme, used only by the policy GuestSession.
            .AddScheme<AuthenticationSchemeOptions, GuestSessionAuthenticationHandler>(
                GuestSessionDefaults.AuthenticationScheme,
                _ => { })
            // The plugin of a site connected to the org (S022): its own scheme, never the default, used only by the policy SiteConnection.
            .AddScheme<AuthenticationSchemeOptions, SiteTokenAuthenticationHandler>(
                SiteTokenDefaults.AuthenticationScheme,
                _ => { });

        return services;
    }
}

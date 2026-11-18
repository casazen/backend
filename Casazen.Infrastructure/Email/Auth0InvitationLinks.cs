using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>Per deployment, a role-free Auth0 organization on the same database connection as the SPA.</summary>
public sealed class Auth0InvitationOptions
{
    public string? OrganizationId { get; init; }
    public string? ClientId { get; init; }
    public string? ConnectionId { get; init; }

    public bool Enabled => !string.IsNullOrWhiteSpace(OrganizationId);

    public bool IsValid() => !Enabled || (OrganizationId!.StartsWith("org_", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(ClientId)
        && ConnectionId?.StartsWith("con_", StringComparison.Ordinal) == true);
}

/// <summary>A CasaZen invitation to an account, with its original link and lifetime.</summary>
public sealed record AccountInvitationEmail(string Flow, string Url, DateTime ExpiresAtUtc);

/// <summary>Creates the Auth0 side of the invitation without requesting a second email or assigning an Auth0 role.</summary>
public sealed class Auth0InvitationLinks(
    IOptions<Auth0InvitationOptions> options,
    IAuth0ManagementTokenProvider tokens,
    IHttpClientFactory httpClients,
    PublicSiteLinks publicSite,
    TimeProvider clock)
{
    private const int MaxTtlSeconds = 30 * 24 * 60 * 60;

    public bool Enabled => options.Value.Enabled;

    public async Task<string?> CreateAsync(string email, AccountInvitationEmail invitation, CancellationToken cancellationToken)
    {
        var expiresIn = (invitation.ExpiresAtUtc - clock.GetUtcNow().UtcDateTime).TotalSeconds;
        if (expiresIn < 1)
            return null;

        var configuration = options.Value;
        if (!tokens.IsConfigured || string.IsNullOrWhiteSpace(tokens.Domain))
            throw new InvalidOperationException("Auth0 Management API is required for account invitations.");

        var legacy = new Uri(invitation.Url, UriKind.Absolute);
        var token = invitation.Flow switch
        {
            "host" => Uri.UnescapeDataString(legacy.Segments[^1]),
            "supplier" => QueryHelpers.ParseQuery(legacy.Query)["inviteToken"].ToString(),
            _ => QueryHelpers.ParseQuery(legacy.Query)["token"].ToString(),
        };
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The CasaZen invitation has no token.");

        var endpoint = $"https://{tokens.Domain}/api/v2/organizations/{Uri.EscapeDataString(configuration.OrganizationId!)}/invitations";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        request.Content = JsonContent.Create(new
        {
            inviter = new { name = "CasaZen" },
            invitee = new { email },
            client_id = configuration.ClientId,
            connection_id = configuration.ConnectionId,
            ttl_sec = (int)Math.Min(expiresIn, MaxTtlSeconds),
            send_invitation_email = false,
        });
        using var client = httpClients.CreateClient(Auth0ManagementTokenProvider.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            tokens.Invalidate();
        response.EnsureSuccessStatusCode();

        using var result = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
        if (result is null || !result.RootElement.TryGetProperty("invitation_url", out var url)
            || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var auth0))
            throw new InvalidOperationException("Auth0 returned no invitation link.");

        var parameters = QueryHelpers.ParseQuery(auth0.Query);
        var organization = parameters["organization"].ToString();
        var ticket = parameters["invitation"].ToString();
        if (!string.Equals(organization, configuration.OrganizationId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("Auth0 returned an invalid invitation link.");

        var fragment = QueryString.Create(new Dictionary<string, string?>
        {
            ["organization"] = organization,
            ["invitation"] = ticket,
            ["flow"] = invitation.Flow,
            ["token"] = token,
        }).ToString();
        return publicSite.PublicPage("/attiva-invito") + "#" + fragment[1..];
    }
}

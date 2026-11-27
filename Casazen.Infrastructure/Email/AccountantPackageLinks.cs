using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Email;

public sealed record AccountantPackageLink(string Url, DateTime ExpiresAt);

/// <summary>
/// The signed link of the package sent to the accountant (E15/F01/US02/T02, NF-101): HMAC-SHA256, with the secret shared by the signed links (<c>SIGNED_LINK_SECRET</c>, ADR-007-R20: no new variable), of
/// <c>accountant-package.{orgId}.{period}.{expiresAt}</c>, hex lowercase, valid <see cref="AccountantPackageLinkRules.Lifetime"/> (30 days), compared in constant time, under <c>App:ApiBaseUrl</c>:
/// <c>GET /api/public/accountant-package/{orgId}/{period}?expiresAt=&amp;signature=</c>. The accountant has no CasaZen account, so the signature is the whole proof, and it names the org, the month and the expiry:
/// a link made for another org or another month, a changed expiry and a signature of another kind of link (the purpose is the first word of what is signed) never open. Built here for the e-mail
/// (<see cref="Create"/>) and checked here by the endpoint that streams the package (<see cref="Verify"/>), so the two never drift. Neither the link nor its signature is ever logged.
/// </summary>
public sealed class AccountantPackageLinks(IConfiguration configuration, TimeProvider timeProvider)
{
    public const string SecretVariable = MarketingUnsubscribeLinks.SecretVariable;

    public const string ExpiresAtFormat = MarketingUnsubscribeLinks.ExpiresAtFormat;

    public const string Path = "/api/public/accountant-package";

    public bool IsConfigured => Secret is not null && ApiBaseUrl() is not null;

    private string? Secret => configuration[SecretVariable] is { } secret && !string.IsNullOrWhiteSpace(secret) ? secret : null;

    public AccountantPackageLink Create(Guid orgId, string period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(period);
        var secret = Secret ?? throw new EmailConfigurationException(
            $"{SecretVariable} is missing: the link of the package of the accountant cannot be signed.");
        var api = ApiBaseUrl() ?? throw new EmailConfigurationException(
            "App:ApiBaseUrl is missing or is not an https address: the link of the package of the accountant cannot be built.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).Add(AccountantPackageLinkRules.Lifetime);
        var expires = expiresAt.ToString(ExpiresAtFormat, CultureInfo.InvariantCulture);
        var url = $"{api}{Path}/{orgId:D}/{Uri.EscapeDataString(period)}?expiresAt={Uri.EscapeDataString(expires)}&signature={Sign(secret, orgId, period, expires)}";
        return new AccountantPackageLink(url, expiresAt);
    }

    public bool Verify(Guid orgId, string? period, string? expiresAt, string? signature)
    {
        if (orgId == Guid.Empty || string.IsNullOrWhiteSpace(period) || string.IsNullOrWhiteSpace(expiresAt) || string.IsNullOrWhiteSpace(signature))
            return false;

        if (!DateTime.TryParseExact(
                expiresAt,
                ExpiresAtFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiration)
            || timeProvider.GetUtcNow().UtcDateTime > expiration)
        {
            return false;
        }

        if (Secret is not { } secret)
            return false;

        var expected = Encoding.UTF8.GetBytes(Sign(secret, orgId, period, expiresAt));
        var given = Encoding.UTF8.GetBytes(signature.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static string Sign(string secret, Guid orgId, string period, string expiresAt)
    {
        var payload = $"{AccountantPackageLinkRules.SignaturePurpose}.{orgId:D}.{period}.{expiresAt}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private string? ApiBaseUrl()
    {
        var value = configuration["App:ApiBaseUrl"]?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? value!.TrimEnd('/')
            : null;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Email;

public sealed record MarketingUnsubscribeLink(string PageUrl, string? OneClickUrl);

public enum MarketingUnsubscribeCheck
{
    Valid,
    InvalidTimestamp,
    Expired,
    NotConfigured,
    InvalidSignature,
}

public sealed class MarketingUnsubscribeLinks(
    IConfiguration configuration,
    PublicSiteLinks publicSiteLinks,
    TimeProvider timeProvider,
    ICrmContactIndex contactIndex)
{
    public const string SecretVariable = "SIGNED_LINK_SECRET";

    public const string ExpiresAtFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public const string OneClickPath = "/api/public/marketing/unsubscribe";

    public bool IsConfigured => Secret is not null && publicSiteLinks.IsConfigured;

    private string? Secret => configuration[SecretVariable] is { } secret && !string.IsNullOrWhiteSpace(secret) ? secret : null;

    /// <summary>The link that stops the marketing of <paramref name="orgId"/> to <paramref name="email"/>.</summary>
    /// <exception cref="EmailConfigurationException">The secret or the public URL of the web app is missing.</exception>
    /// <exception cref="InvalidOperationException"><c>Crm:ContactIndexKey</c> is not configured outside Development and Testing.</exception>
    public MarketingUnsubscribeLink Create(Guid orgId, string email, Guid? deliveryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var secret = Secret ?? throw new EmailConfigurationException(
            $"{SecretVariable} is missing: the unsubscribe link of a marketing e-mail cannot be signed.");

        var emailHash = contactIndex.Hash(ShowcaseBookingRules.NormalizeEmail(email));
        var expiresAt = $"v2-{deliveryId.GetValueOrDefault():N}";
        var query = $"orgId={orgId:D}&emailHash={emailHash}&expiresAt={Uri.EscapeDataString(expiresAt)}" +
                    $"&signature={Sign(secret, $"{orgId:D}.{emailHash}", expiresAt)}";

        var oneClick = ApiBaseUrl() is { } api ? $"{api}{OneClickPath}?{query}" : null;
        return new MarketingUnsubscribeLink(publicSiteLinks.MarketingUnsubscribe(query), oneClick);
    }

    public MarketingUnsubscribeCheck Verify(Guid orgId, string emailHash, string expiresAt, string signature)
    {
        ArgumentNullException.ThrowIfNull(emailHash);
        ArgumentNullException.ThrowIfNull(expiresAt);
        ArgumentNullException.ThrowIfNull(signature);

        bool permanent = expiresAt.StartsWith("v2-", StringComparison.Ordinal) && Guid.TryParseExact(expiresAt[3..], "N", out _);
        DateTime expiration = default;
        if (!permanent && !DateTime.TryParseExact(
                expiresAt,
                ExpiresAtFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out expiration))
        {
            return MarketingUnsubscribeCheck.InvalidTimestamp;
        }

        if (!permanent && timeProvider.GetUtcNow().UtcDateTime > expiration)
            return MarketingUnsubscribeCheck.Expired;

        if (Secret is not { } secret)
            return MarketingUnsubscribeCheck.NotConfigured;

        var expected = Encoding.UTF8.GetBytes(Sign(secret, permanent ? $"{orgId:D}.{emailHash}" : emailHash, expiresAt));
        var given = Encoding.UTF8.GetBytes(signature.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given)
            ? MarketingUnsubscribeCheck.Valid
            : MarketingUnsubscribeCheck.InvalidSignature;
    }

    private static string Sign(string secret, string emailHash, string expiresAt)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{emailHash}.{expiresAt}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string? ApiBaseUrl()
    {
        var value = configuration["App:ApiBaseUrl"]?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? value!.TrimEnd('/')
            : null;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Email;

/// <summary>The two addresses of a signed unsubscribe link (<see cref="MarketingUnsubscribeLinks.Create"/>).</summary>
/// <param name="PageUrl">The page of the web app (<see cref="PublicSiteLinks.MarketingUnsubscribe"/>): the link in the body of the e-mail.</param>
/// <param name="OneClickUrl">
/// <c>POST api/public/marketing/unsubscribe</c> with the signed values in the query, for the <c>List-Unsubscribe</c> header (RFC 8058: the
/// mail client posts to it itself); <c>null</c> when <c>App:ApiBaseUrl</c> is not an https URL.
/// </param>
public sealed record MarketingUnsubscribeLink(string PageUrl, string? OneClickUrl);

/// <summary>Outcome of the check of an unsubscribe link (<see cref="MarketingUnsubscribeLinks.Verify"/>), in the order it is checked.</summary>
public enum MarketingUnsubscribeCheck
{
    Valid,
    InvalidTimestamp,
    Expired,
    NotConfigured,
    InvalidSignature,
}

/// <summary>
/// New links sign the organization, opaque address hash and delivery token without an expiry.
/// The old timestamp format still verifies against its original signature and expiry.
/// </summary>
public sealed class MarketingUnsubscribeLinks(
    IConfiguration configuration,
    PublicSiteLinks publicSiteLinks,
    TimeProvider timeProvider,
    ICrmContactIndex contactIndex)
{
    /// <summary>The variable of the secret of the signed links (ADR-007-R20), read through the configuration.</summary>
    public const string SecretVariable = "SIGNED_LINK_SECRET";

    /// <summary>Format of <c>expiresAt</c>, in UTC.</summary>
    public const string ExpiresAtFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>The one-click endpoint, under <c>App:ApiBaseUrl</c>.</summary>
    public const string OneClickPath = "/api/public/marketing/unsubscribe";

    /// <summary>True when a link can be built: the secret and the public URL of the web app are configured.</summary>
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

    /// <summary>
    /// Checks the values of a link, in the order the endpoint answered them before (DEB-01 moved the check here): the timestamp, the expiry,
    /// the secret, then the signature, compared in constant time.
    /// </summary>
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

    /// <summary>ADR-007-R18: HMAC-SHA256 of <c>emailHash + "." + expiresAt</c> (UTF-8), hex lowercase.</summary>
    private static string Sign(string secret, string emailHash, string expiresAt)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{emailHash}.{expiresAt}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The public https URL of this API (<c>App:ApiBaseUrl</c>, as for the iCal export links), without the final slash.</summary>
    private string? ApiBaseUrl()
    {
        var value = configuration["App:ApiBaseUrl"]?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? value!.TrimEnd('/')
            : null;
    }
}

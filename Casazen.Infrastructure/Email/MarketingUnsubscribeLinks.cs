using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Infrastructure.Services;
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
/// The signed one-click unsubscribe link of the marketing e-mails (NF-05, ADR-007-R18 and R20; DEB-01): HMAC-SHA256, with the secret shared
/// by the signed links (<c>SIGNED_LINK_SECRET</c>), of <c>emailHash + "." + expiresAt</c>, hex lowercase, valid 30 days. <c>emailHash</c> is
/// the SHA-256 blind index of the address, the one <see cref="SuppressionService"/> stores and checks: the link never carries the address.
/// The org names the suppression list and, as ADR-007-R18 says, is not part of the signature. Built here for the campaigns
/// (<see cref="Create"/>) and checked here by <c>POST api/public/marketing/unsubscribe</c> (<see cref="Verify"/>), so the two never drift.
/// </summary>
public sealed class MarketingUnsubscribeLinks(IConfiguration configuration, PublicSiteLinks publicSiteLinks, TimeProvider timeProvider)
{
    /// <summary>The variable of the secret of the signed links (ADR-007-R20), read through the configuration.</summary>
    public const string SecretVariable = "SIGNED_LINK_SECRET";

    /// <summary>Format of <c>expiresAt</c>, in UTC.</summary>
    public const string ExpiresAtFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>The one-click endpoint, under <c>App:ApiBaseUrl</c>.</summary>
    public const string OneClickPath = "/api/public/marketing/unsubscribe";

    /// <summary>How long a link works (ADR-007-R18).</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    /// <summary>True when a link can be built: the secret and the public URL of the web app are configured.</summary>
    public bool IsConfigured => Secret is not null && publicSiteLinks.IsConfigured;

    private string? Secret => configuration[SecretVariable] is { } secret && !string.IsNullOrWhiteSpace(secret) ? secret : null;

    /// <summary>The link that stops the marketing of <paramref name="orgId"/> to <paramref name="email"/>, valid for <see cref="Validity"/>.</summary>
    /// <exception cref="EmailConfigurationException">The secret or the public URL of the web app is missing.</exception>
    public MarketingUnsubscribeLink Create(Guid orgId, string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var secret = Secret ?? throw new EmailConfigurationException(
            $"{SecretVariable} is missing: the unsubscribe link of a marketing e-mail cannot be signed.");

        var emailHash = SuppressionService.ComputeEmailHash(email);
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.Add(Validity).ToString(ExpiresAtFormat, CultureInfo.InvariantCulture);
        var query = $"orgId={orgId:D}&emailHash={emailHash}&expiresAt={Uri.EscapeDataString(expiresAt)}" +
                    $"&signature={Sign(secret, emailHash, expiresAt)}";

        var oneClick = ApiBaseUrl() is { } api ? $"{api}{OneClickPath}?{query}" : null;
        return new MarketingUnsubscribeLink(publicSiteLinks.MarketingUnsubscribe(query), oneClick);
    }

    /// <summary>
    /// Checks the values of a link, in the order the endpoint answered them before (DEB-01 moved the check here): the timestamp, the expiry,
    /// the secret, then the signature, compared in constant time.
    /// </summary>
    public MarketingUnsubscribeCheck Verify(string emailHash, string expiresAt, string signature)
    {
        ArgumentNullException.ThrowIfNull(emailHash);
        ArgumentNullException.ThrowIfNull(expiresAt);
        ArgumentNullException.ThrowIfNull(signature);

        if (!DateTime.TryParseExact(
                expiresAt,
                ExpiresAtFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiration))
        {
            return MarketingUnsubscribeCheck.InvalidTimestamp;
        }

        if (timeProvider.GetUtcNow().UtcDateTime > expiration)
            return MarketingUnsubscribeCheck.Expired;

        if (Secret is not { } secret)
            return MarketingUnsubscribeCheck.NotConfigured;

        var expected = Encoding.UTF8.GetBytes(Sign(secret, emailHash, expiresAt));
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

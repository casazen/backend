using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

public sealed record MarketingConfirmationLink(string SignedQuery, DateTime ExpiresAtUtc);

public enum MarketingConfirmationCheck
{
    Valid,
    InvalidTimestamp,
    Expired,
    NotConfigured,
    InvalidSignature,
}

/// <summary>
/// The signed link of the double opt-in e-mail (E07/F04/US01/T01, NF-90, ADR-007 amendment): HMAC-SHA256, with the secret shared by the signed
/// links (<c>SIGNED_LINK_SECRET</c>, ADR-007-R20), of <c>contactId + "." + expiresAt</c> (the id as 36 lowercase characters, the expiry as
/// <c>yyyy-MM-ddTHH:mm:ssZ</c> in UTC), hex lowercase, valid <c>Crm:ConfirmationLinkValidityHours</c> hours (72). The pattern of
/// <see cref="MarketingUnsubscribeLinks"/>, with one difference that matters: the link names <b>the contact</b>, not the address, so it holds no
/// personal data at all and no org id (the contact knows its org). Built here for the confirmation e-mail (<see cref="Create"/>) and checked
/// here by <c>POST api/public/marketing/confirm</c> (<see cref="Verify"/>), so the two never drift. The old link of the check-in
/// (<c>confirm-email</c>, which carries the address) was never sent by any code and keeps its endpoint untouched.
/// </summary>
public sealed class MarketingConfirmationLinks(
    IConfiguration configuration,
    IOptions<CrmOptions> options,
    TimeProvider timeProvider)
{
    public const string SecretVariable = MarketingUnsubscribeLinks.SecretVariable;

    public const string ExpiresAtFormat = MarketingUnsubscribeLinks.ExpiresAtFormat;

    public bool IsConfigured => Secret is not null;

    private string? Secret => configuration[SecretVariable] is { } secret && !string.IsNullOrWhiteSpace(secret) ? secret : null;

    public MarketingConfirmationLink Create(Guid contactId)
    {
        if (contactId == Guid.Empty)
            throw new ArgumentException("A confirmation link names a contact.", nameof(contactId));

        var secret = Secret ?? throw new EmailConfigurationException(
            $"{SecretVariable} is missing: the confirmation link of a marketing consent cannot be signed.");

        var hours = Math.Clamp(
            options.Value.ConfirmationLinkValidityHours,
            CrmOptions.MinConfirmationLinkValidityHours,
            CrmOptions.MaxConfirmationLinkValidityHours);
        var expiry = timeProvider.GetUtcNow().UtcDateTime.AddHours(hours);

        var expiresAtUtc = new DateTime(expiry.Ticks - (expiry.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        var expiresAt = expiresAtUtc.ToString(ExpiresAtFormat, CultureInfo.InvariantCulture);
        var query = $"contactId={contactId:D}&expiresAt={Uri.EscapeDataString(expiresAt)}&signature={Sign(secret, contactId, expiresAt)}";
        return new MarketingConfirmationLink(query, expiresAtUtc);
    }

    public MarketingConfirmationCheck Verify(Guid contactId, string expiresAt, string signature)
    {
        ArgumentNullException.ThrowIfNull(expiresAt);
        ArgumentNullException.ThrowIfNull(signature);

        if (!DateTime.TryParseExact(
                expiresAt,
                ExpiresAtFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiration))
        {
            return MarketingConfirmationCheck.InvalidTimestamp;
        }

        if (timeProvider.GetUtcNow().UtcDateTime > expiration)
            return MarketingConfirmationCheck.Expired;

        if (Secret is not { } secret)
            return MarketingConfirmationCheck.NotConfigured;

        var expected = Encoding.UTF8.GetBytes(Sign(secret, contactId, expiresAt));
        var given = Encoding.UTF8.GetBytes(signature.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given)
            ? MarketingConfirmationCheck.Valid
            : MarketingConfirmationCheck.InvalidSignature;
    }

    private static string Sign(string secret, Guid contactId, string expiresAt)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{contactId:D}.{expiresAt}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

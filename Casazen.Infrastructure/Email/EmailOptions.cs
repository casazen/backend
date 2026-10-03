using System.Net.Mail;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Configuration of the transactional email provider (section <c>Email</c>; on Railway <c>Email__Provider</c>,
/// <c>Email__ApiKey</c>, <c>Email__FromAddress</c>, <c>Email__FromName</c>). There is no default sender in code: the
/// sender must be an address of a domain verified on the provider (runbook <c>docs/runbooks/email.md</c>).
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>The only provider implemented (<see cref="External.ResendEmailService"/>).</summary>
    public const string ResendProvider = "Resend";

    /// <summary>Key accepted until Railway is migrated to <c>Email__ApiKey</c>.</summary>
    public const string LegacyResendApiKeyKey = "Email:ResendApiKey";

    public string Provider { get; set; } = ResendProvider;

    public string? ApiKey { get; set; }

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "CasaZen";

    /// <summary>
    /// Test-only: base URL of a Resend-compatible mail catcher (<c>Email__ApiUrl</c>), used by the ephemeral stack of the
    /// Golden Journey L3 (FN-03) so no email leaves the runner. Empty = the real Resend API. Refused outside Development
    /// and Testing by <see cref="EmailOptionsValidator"/>, so it can never redirect production email.
    /// </summary>
    public string? ApiUrl { get; set; }

    /// <summary>True when an email can be handed to the provider: known provider, API key and a valid sender.</summary>
    public bool IsConfigured =>
        string.Equals(Provider, ResendProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && IsValidAddress(FromAddress);

    internal static bool IsValidAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && MailAddress.TryCreate(address.Trim(), out var parsed)
        && string.IsNullOrEmpty(parsed.DisplayName)
        && string.Equals(parsed.Address, address.Trim(), StringComparison.OrdinalIgnoreCase);
}

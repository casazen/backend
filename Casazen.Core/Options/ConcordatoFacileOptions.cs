using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>ConcordatoFacile</c> (ADR-005): Stripe Price ID for one-time payment and link secret for practice access.
/// The StripePriceId is required in Production and must be configured in the Stripe dashboard before deployment.
/// </summary>
public class ConcordatoFacileOptions
{
    public const string SectionName = "ConcordatoFacile";

    /// <summary>
    /// Stripe Price ID for the EUR 119,00 one-time payment (ADR-005-R10).
    /// Must be configured in Production, otherwise the app fails to start.
    /// Example: price_1234567890abcdef
    /// </summary>
    public string? StripePriceId { get; set; }

    /// <summary>
    /// Secret key for generating signed links to the ConcordatoFacile portal (NF-05).
    /// Used to create time-limited, tamper-proof URLs.
    /// </summary>
    public string? LinkSecret { get; set; }

    /// <summary>
    /// Base URL of the web application for constructing Stripe Checkout return URLs.
    /// Example: https://app.casazen.it or https://localhost:5173 in development
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Stripe API key (optional, can also come from environment variable STRIPE_SECRET_KEY).
    /// </summary>
    public string? StripeApiKey { get; set; }

    /// <summary>
    /// Stripe Price IDs for biennial subscriptions that grant a ConcordatoFacile gift (ADR-006-R1, ADR-006-R2).
    /// Required in Production (ADR-006-R8). Example: ["price_123abc", "price_456def"]
    /// </summary>
    public string[]? BiennialPriceIds { get; set; }
}

/// <summary>
/// Validates ConcordatoFacileOptions at startup (ADR-005-R10).
/// In Production, StripePriceId must be configured or the app refuses to start.
/// </summary>
public sealed class ConcordatoFacileOptionsValidator(IHostEnvironment environment) : IValidateOptions<ConcordatoFacileOptions>
{
    public ValidateOptionsResult Validate(string? name, ConcordatoFacileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // StripePriceId is required in Production (ADR-005-R10)
        if (IsProductionEnvironment() && string.IsNullOrWhiteSpace(options.StripePriceId))
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:StripePriceId is required in Production. " +
                "Configure the Stripe Price ID for the ConcordatoFacile one-time payment.");
        }

        // BiennialPriceIds is required in Production (ADR-006-R8)
        if (IsProductionEnvironment() && (options.BiennialPriceIds is null || options.BiennialPriceIds.Length == 0))
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:BiennialPriceIds is required in Production. " +
                "Configure the Stripe Price IDs for biennial subscriptions that grant a ConcordatoFacile gift.");
        }

        // ADR-007-R3: LinkSecret is required in Production (can come from env var or config)
        var linkSecret = Environment.GetEnvironmentVariable("CONCORDATOFACILE_LINK_SECRET")
            ?? options.LinkSecret;

        if (IsProductionEnvironment() && string.IsNullOrWhiteSpace(linkSecret))
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:LinkSecret is required in Production. " +
                "Set CONCORDATOFACILE_LINK_SECRET environment variable or configure ConcordatoFacile:LinkSecret.");
        }

        // ADR-007-R4: LinkSecret must be >= 32 characters
        if (!string.IsNullOrWhiteSpace(linkSecret) && linkSecret.Length < 32)
        {
            failures.Add(
                "ConcordatoFacile link secret must be at least 32 characters.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private bool IsProductionEnvironment() =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}

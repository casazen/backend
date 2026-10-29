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

    /// <summary>
    /// Where the signed link of the module leads (CF-SIM). <see cref="ConcordatoFacileFormMode.Real"/> (the default, and the only mode
    /// of production): the real module of ConcordatoFacile, <c>https://app.concordatofacile.it/</c>.
    /// <see cref="ConcordatoFacileFormMode.Simulated"/>: for the test environment only, while ConcordatoFacile has no environment of its
    /// own to try the whole round; the link opens the simulated module <c>{BaseUrl}/guided-form</c> of the web app, the status of the
    /// practices is read from the table <c>cf_simulated_practices</c> and the endpoints <c>api/cf-simulator/*</c> answer. It is refused
    /// at startup in Production (<see cref="ConcordatoFacileOptionsValidator"/>).
    /// </summary>
    public ConcordatoFacileFormMode FormMode { get; set; } = ConcordatoFacileFormMode.Real;

    /// <summary>
    /// The address of the real module of ConcordatoFacile (CF-URL). Unset (the default, and the only value allowed in Production):
    /// <c>https://app.concordatofacile.it/</c>, as the specification v0.2 §2 says. ConcordatoFacile prepares a module of its own to try
    /// the whole round from the test environment, which accepts the links signed with the test secret of the specification, at an address
    /// it gives to CasaZen: that address is set here on the test environment only, an absolute <c>https</c> address without query or
    /// fragment (the signed parameters are appended to it). Ignored with <see cref="ConcordatoFacileFormMode.Simulated"/>. Refused at
    /// startup in Production (<see cref="ConcordatoFacileOptionsValidator"/>).
    /// </summary>
    public string? FormUrl { get; set; }

    /// <summary>
    /// The access secret of the protected test module (CF-BYPASS). The test module of ConcordatoFacile (<see cref="FormUrl"/>) runs on a
    /// Vercel deployment with its protection on: an address without the secret of the deployment shows "You Need Access" and asks for an
    /// account of the team. ConcordatoFacile hands the secret to CasaZen out of band (a one-time Bitwarden Send, like the keys); with it
    /// set, the signed link carries <c>x-vercel-protection-bypass=&lt;secret&gt;</c> and <c>x-vercel-set-bypass-cookie=true</c> next to
    /// the signed parameters (the signature does not cover them), so whoever opens the link enters, the test users too, with no Vercel
    /// account. Only with <see cref="FormUrl"/>; ignored with <see cref="ConcordatoFacileFormMode.Simulated"/>. Refused at startup in
    /// Production and without <see cref="FormUrl"/> (<see cref="ConcordatoFacileOptionsValidator"/>). Never logged, never stored:
    /// neither is the link.
    /// </summary>
    public string? FormBypassSecret { get; set; }

    /// <summary>Settings of the simulated module and status API (CF-SIM); read only with <see cref="ConcordatoFacileFormMode.Simulated"/>.</summary>
    public ConcordatoFacileSimulatorOptions Simulator { get; set; } = new();
}

/// <summary>How the signed link of ConcordatoFacile is served (<see cref="ConcordatoFacileOptions.FormMode"/>).</summary>
public enum ConcordatoFacileFormMode
{
    /// <summary>The real module and status API of ConcordatoFacile. The only mode allowed in Production.</summary>
    Real = 0,

    /// <summary>A simulation of both, for the test environment only (CF-SIM). Never in Production.</summary>
    Simulated = 1,
}

/// <summary>Settings of the simulator of ConcordatoFacile (CF-SIM): nothing here has an effect with <see cref="ConcordatoFacileFormMode.Real"/>.</summary>
public class ConcordatoFacileSimulatorOptions
{
    /// <summary>How long (seconds) the cache of the status keeps what it read from the simulator, so that a person who changes a state sees it at once.</summary>
    public int StatusTtlSeconds { get; set; } = 5;
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

        if (options.FormMode == ConcordatoFacileFormMode.Simulated)
            AddSimulatorFailures(options, failures);

        if (!string.IsNullOrWhiteSpace(options.FormUrl))
            AddFormUrlFailures(options.FormUrl, failures);

        if (!string.IsNullOrWhiteSpace(options.FormBypassSecret))
            AddFormBypassSecretFailures(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// CF-SIM: the simulator of the module and of the status API stands in for ConcordatoFacile, so it must never run where the real one
    /// has to be reached. The test environment of Railway runs as <c>Staging</c> and production as <c>Production</c> (docs/INFRA.md,
    /// docs/runbooks/deploy-checklist.md), and the startup refuses a live Stripe key outside Production and a test one inside it
    /// (<c>BillingConfiguration</c>): <c>Simulated</c> under <c>Production</c> stops the startup here, and an environment renamed to get
    /// around this one could not run with the live keys of production.
    /// </summary>
    private void AddSimulatorFailures(ConcordatoFacileOptions options, List<string> failures)
    {
        if (environment.IsProduction())
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:FormMode=Simulated is allowed only in test environments, never when " +
                "ASPNETCORE_ENVIRONMENT is Production: the simulator stands in for ConcordatoFacile, which production must always reach. " +
                $"Remove {ConcordatoFacileOptions.SectionName}__FormMode or set it to Real.");
        }

        // The signed link of the simulated module is an address of the CasaZen web app: without it there is nowhere to send the person.
        if (!Uri.TryCreate(options.BaseUrl?.Trim(), UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:BaseUrl (the address of the CasaZen web app, https://<host>) is required with " +
                "FormMode=Simulated: the simulated module is its page /guided-form.");
        }

        if (options.Simulator is null || options.Simulator.StatusTtlSeconds is < 1 or > 3600)
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:Simulator:StatusTtlSeconds must be between 1 and 3600 seconds.");
        }
    }

    /// <summary>
    /// CF-URL: production always opens the real module of ConcordatoFacile, so another address is refused there; elsewhere it is the test
    /// module that ConcordatoFacile gives to CasaZen, and it must be an absolute https address without query or fragment, because the
    /// signed parameters are appended to it.
    /// </summary>
    private void AddFormUrlFailures(string formUrl, List<string> failures)
    {
        if (environment.IsProduction())
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:FormUrl is allowed only in test environments, never when ASPNETCORE_ENVIRONMENT " +
                $"is Production: production always opens the real module of ConcordatoFacile. Remove {ConcordatoFacileOptions.SectionName}__FormUrl.");
        }

        if (!Uri.TryCreate(formUrl.Trim(), UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(address.Query)
            || !string.IsNullOrEmpty(address.Fragment))
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:FormUrl must be an absolute https address without query or fragment " +
                "(for example https://test.example/).");
        }
    }

    /// <summary>
    /// CF-BYPASS: the secret of the protected test module goes on the link, so it can only exist where the link opens that module: never
    /// in Production (the link would carry a secret to the real module, which has no such protection) and only with a <c>FormUrl</c>. It
    /// is one token: a blank inside means it was pasted wrong. No message repeats the value.
    /// </summary>
    private void AddFormBypassSecretFailures(ConcordatoFacileOptions options, List<string> failures)
    {
        if (environment.IsProduction())
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:FormBypassSecret is allowed only in test environments, never when " +
                "ASPNETCORE_ENVIRONMENT is Production: production opens the real module of ConcordatoFacile, which has no such " +
                $"protection. Remove {ConcordatoFacileOptions.SectionName}__FormBypassSecret.");
        }

        if (string.IsNullOrWhiteSpace(options.FormUrl))
        {
            failures.Add(
                $"{ConcordatoFacileOptions.SectionName}:FormBypassSecret needs {ConcordatoFacileOptions.SectionName}:FormUrl: it is the " +
                "secret of the test module that FormUrl opens.");
        }

        if (options.FormBypassSecret!.Trim().Any(char.IsWhiteSpace))
        {
            failures.Add($"{ConcordatoFacileOptions.SectionName}:FormBypassSecret must be a single token, without blanks inside.");
        }
    }

    private bool IsProductionEnvironment() =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Where the email configuration must be complete. Outside Development and Testing (Production, Staging, …) a missing
/// or invalid value stops the application at startup (<c>ValidateOnStart</c>) with the list of problems, instead of
/// silently losing every email. Development and Testing (CI and the Railway test service) may run without a provider:
/// each skipped email is logged as a warning.
/// </summary>
public static class EmailConfigurationPolicy
{
    public static bool RequiresCompleteConfiguration(IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");
}

/// <summary>Validates <see cref="EmailOptions"/> where the configuration must be complete.</summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    /// <summary>Resend's shared test domain: it delivers only to the owner of the Resend account.</summary>
    public const string ResendTestDomain = "resend.dev";

    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var errors = GetErrors(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public static IReadOnlyList<string> GetErrors(EmailOptions options)
    {
        var errors = new List<string>();

        if (!string.Equals(options.Provider, EmailOptions.ResendProvider, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Email__Provider '{options.Provider}' is not supported: use '{EmailOptions.ResendProvider}'.");

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            errors.Add("Email__ApiKey is missing: set the Resend API key (re_…).");
        else if (!options.ApiKey.Trim().StartsWith("re_", StringComparison.Ordinal))
            errors.Add("Email__ApiKey is not a Resend API key (it must start with 're_').");

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            errors.Add("Email__FromAddress is missing: set a sender of the domain verified on Resend.");
        }
        else if (!EmailOptions.IsValidAddress(options.FromAddress))
        {
            errors.Add("Email__FromAddress is not a valid email address (only the address: the name goes in Email__FromName).");
        }
        else if (options.FromAddress.Trim().EndsWith("@" + ResendTestDomain, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Email__FromAddress uses the Resend test domain '{ResendTestDomain}', which delivers only to the Resend account owner: use the verified CasaZen domain.");
        }

        return errors;
    }
}

/// <summary>Validates <see cref="PublicSiteOptions"/> where the configuration must be complete.</summary>
public sealed class PublicSiteOptionsValidator : IValidateOptions<PublicSiteOptions>
{
    public ValidateOptionsResult Validate(string? name, PublicSiteOptions options)
    {
        if (!PublicSiteOptions.TryGetBaseUri(options.PublicSiteBaseUrl, out var baseUri))
        {
            return ValidateOptionsResult.Fail(
                "App__PublicSiteBaseUrl is missing or invalid: set the absolute URL of the web app (e.g. https://<domain>), " +
                "used for every link in emails, the SEO canonical URLs and the sitemap.");
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps)
            return ValidateOptionsResult.Fail("App__PublicSiteBaseUrl must use https.");

        // SE-02 (A8-02): one public domain. The Seo__PublicBaseUrl alias may repeat it, never point elsewhere.
        if (!string.IsNullOrWhiteSpace(options.SeoPublicBaseUrl)
            && PublicSiteOptions.Normalize(options.SeoPublicBaseUrl) != PublicSiteOptions.Normalize(options.PublicSiteBaseUrl))
        {
            return ValidateOptionsResult.Fail(
                "Seo__PublicBaseUrl differs from App__PublicSiteBaseUrl: the SEO pages are served by the same web app, " +
                "so set only App__PublicSiteBaseUrl (or give both the same value).");
        }

        return ValidateOptionsResult.Success;
    }
}

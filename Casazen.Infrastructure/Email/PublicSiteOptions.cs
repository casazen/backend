using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Public URL of the web app (section <c>App</c>, Railway variable <c>App__PublicSiteBaseUrl</c>): the single source of
/// the public domain (decision D3: no domain written in code). Every link in an email, the SEO canonical URLs, the
/// sitemap and the CORS origin of the web app are built from it (runbook <c>docs/runbooks/seo-domain.md</c>).
/// </summary>
/// <remarks>
/// <c>Seo:PublicBaseUrl</c> (<c>Seo__PublicBaseUrl</c>) is accepted as an alias, used only when
/// <c>App:PublicSiteBaseUrl</c> is empty: the SEO pages live in the same web app, so there is one public domain. Both
/// set with different values is a configuration error (<see cref="PublicSiteOptionsValidator"/>).
/// </remarks>
public sealed class PublicSiteOptions
{
    public const string SectionName = "App";

    /// <summary>Configuration key of the alias accepted by decision D3.</summary>
    public const string SeoAliasKey = "Seo:PublicBaseUrl";

    public string? PublicSiteBaseUrl { get; set; }

    /// <summary>
    /// Raw value of <see cref="SeoAliasKey"/>, kept to detect a conflict with <see cref="PublicSiteBaseUrl"/>. Set by
    /// <see cref="ApplySeoAlias"/>, never bound from the <c>App</c> section.
    /// </summary>
    public string? SeoPublicBaseUrl { get; internal set; }

    /// <summary>
    /// The effective public URL of a configuration: <c>App:PublicSiteBaseUrl</c>, or the <c>Seo:PublicBaseUrl</c> alias
    /// when the former is empty. The same rule as <see cref="ApplySeoAlias"/>, for code that reads the configuration
    /// directly (CORS).
    /// </summary>
    public static string? ResolveBaseUrl(IConfiguration configuration)
    {
        var app = configuration[$"{SectionName}:{nameof(PublicSiteBaseUrl)}"];
        return string.IsNullOrWhiteSpace(app) ? configuration[SeoAliasKey] : app;
    }

    /// <summary>Applies the <c>Seo:PublicBaseUrl</c> alias (post-configuration of the options).</summary>
    public void ApplySeoAlias(IConfiguration configuration)
    {
        SeoPublicBaseUrl = configuration[SeoAliasKey];
        if (string.IsNullOrWhiteSpace(PublicSiteBaseUrl))
            PublicSiteBaseUrl = SeoPublicBaseUrl;
    }

    /// <summary>Origin (<c>scheme://host[:port]</c>, lower case) of a valid public URL, for the CORS allow-list.</summary>
    public static bool TryGetOrigin(string? value, out string origin)
    {
        origin = string.Empty;
        if (!TryGetBaseUri(value, out var baseUri))
            return false;

        origin = $"{baseUri.Scheme}://{baseUri.Authority}".ToLowerInvariant();
        return true;
    }

    internal static bool TryGetBaseUri(string? value, out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        baseUri = parsed;
        return true;
    }

    /// <summary>Normalized form used to compare two public URLs (no trailing slash, host in lower case).</summary>
    internal static string? Normalize(string? value) =>
        TryGetBaseUri(value, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : null;
}

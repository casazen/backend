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

    public const string SeoAliasKey = "Seo:PublicBaseUrl";

    public string? PublicSiteBaseUrl { get; set; }

    public string? SeoPublicBaseUrl { get; internal set; }

    public static string? ResolveBaseUrl(IConfiguration configuration)
    {
        var app = configuration[$"{SectionName}:{nameof(PublicSiteBaseUrl)}"];
        return string.IsNullOrWhiteSpace(app) ? configuration[SeoAliasKey] : app;
    }

    public void ApplySeoAlias(IConfiguration configuration)
    {
        SeoPublicBaseUrl = configuration[SeoAliasKey];
        if (string.IsNullOrWhiteSpace(PublicSiteBaseUrl))
            PublicSiteBaseUrl = SeoPublicBaseUrl;
    }

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

    internal static string? Normalize(string? value) =>
        TryGetBaseUri(value, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : null;
}

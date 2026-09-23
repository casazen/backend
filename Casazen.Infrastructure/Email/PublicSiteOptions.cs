namespace Casazen.Infrastructure.Email;

/// <summary>
/// Public URL of the web app (section <c>App</c>, Railway variable <c>App__PublicSiteBaseUrl</c>). Every link in an
/// email is built from it (decision D3: no domain written in code).
/// </summary>
public sealed class PublicSiteOptions
{
    public const string SectionName = "App";

    public string? PublicSiteBaseUrl { get; set; }

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
}

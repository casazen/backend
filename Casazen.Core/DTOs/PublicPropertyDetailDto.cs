namespace Casazen.Core.DTOs;

public class PublicPropertyDetailDto : PublicPropertyDto
{
    public string HouseRules { get; set; } = string.Empty;
    public string CancellationPolicySummary { get; set; } = string.Empty;
    public int? MinNights { get; set; }
    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// Absolute URL of the property page on the public domain (<c>App:PublicSiteBaseUrl</c>), without query string, for
    /// the <c>canonical</c> and <c>og:url</c> of the page (BK-15); <c>null</c> only when that is not configured. Set by
    /// the endpoint, never built in the browser.
    /// </summary>
    public string? CanonicalUrl { get; set; }
}

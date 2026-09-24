namespace Casazen.Core.Options;

public class PublicHostOptions
{
    public const string SectionName = "PublicHost";

    /// <summary>
    /// Base domain of the org subdomains (<c>{label}.{BaseDomain}</c>), Railway variable <c>PublicHost__BaseDomain</c>.
    /// No default (decision D3, SE-03): it is not the public domain of the web app (<c>App__PublicSiteBaseUrl</c>) but
    /// the domain whose wildcard DNS record points to the web app. When empty, the subdomain publication mode is
    /// unavailable (runbook <c>docs/runbooks/seo-domain.md</c>).
    /// </summary>
    public string? BaseDomain { get; set; }

    /// <summary><see cref="BaseDomain"/> in lower case without surrounding dots; <c>null</c> when not configured.</summary>
    public string? NormalizedBaseDomain =>
        string.IsNullOrWhiteSpace(BaseDomain) ? null : BaseDomain.Trim().Trim('.').ToLowerInvariant();

    public string[] ReservedSubdomains { get; set; } =
    [
        "www",
        "api",
        "app",
        "admin",
        "staging",
        "test",
        "mail",
    ];

    /// <summary>DNS CNAME target for Pro custom domains (Vercel Custom Domains).</summary>
    public string VercelCnameTarget { get; set; } = "cname.vercel-dns.com";

    /// <summary>Fixed-window rate limit for <c>PublicResolveHost</c> — requests per IP per minute.</summary>
    public int RateLimitPermitLimit { get; set; } = 60;

    /// <summary>Timeout for the DNS TXT ownership lookup during domain verification.</summary>
    public int DnsLookupTimeoutSeconds { get; set; } = 5;

    /// <summary>In-process resolve-host cache TTL, keyed by normalized host.</summary>
    public int ResolveCacheSeconds { get; set; } = 60;

    /// <summary>TXT record host label prefix used for the domain ownership challenge.</summary>
    public string TxtRecordPrefix { get; set; } = "_casazen-challenge";
}

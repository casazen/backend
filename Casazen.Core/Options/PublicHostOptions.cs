namespace Casazen.Core.Options;

public class PublicHostOptions
{
    public const string SectionName = "PublicHost";

    public string BaseDomain { get; set; } = "casazen.it";

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

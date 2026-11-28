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

    public string VercelCnameTarget { get; set; } = "cname.vercel-dns.com";

    /// <summary>
    /// Domains a CNAME may point to and still count as "pointing at Vercel" (BK-17, <c>PublicHost__AcceptedCnameSuffixes__0</c>):
    /// the target itself or a name under one of these suffixes. Vercel also issues project-specific targets under its own
    /// domains; add the suffix the dashboard shows when the project's recommended record is not under the default one.
    /// </summary>
    public string[] AcceptedCnameSuffixes { get; set; } = [".vercel-dns.com"];

    /// <summary>
    /// IPv4 addresses of Vercel that an A record (or a flattened CNAME at the root of a domain) may resolve to
    /// (<c>PublicHost__VercelAddresses__0</c>), shown to the host as the alternative to the CNAME. Confirm the value in the
    /// Vercel dashboard of the project (Domains), where Vercel recommends it.
    /// </summary>
    public string[] VercelAddresses { get; set; } = ["76.76.21.21"];

    public int RateLimitPermitLimit { get; set; } = 60;

    public int DnsLookupTimeoutSeconds { get; set; } = 5;

    public int ResolveCacheSeconds { get; set; } = 60;

    public string TxtRecordPrefix { get; set; } = "_casazen-challenge";

    public int RecheckPendingMinutes { get; set; } = 30;

    public int RecheckVerifiedHours { get; set; } = 24;

    public int MaxPendingDays { get; set; } = 14;

    public int FailuresBeforeDemotion { get; set; } = 2;

    public int RecheckBatchSize { get; set; } = 50;
}

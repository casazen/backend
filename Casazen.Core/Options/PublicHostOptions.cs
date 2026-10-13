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

    /// <summary>
    /// DNS CNAME target shown to the host for a Pro custom domain (Vercel Custom Domains,
    /// <c>PublicHost__VercelCnameTarget</c>): the value the Vercel dashboard recommends for the project.
    /// </summary>
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

    /// <summary>Fixed-window rate limit for <c>PublicResolveHost</c> — requests per IP per minute.</summary>
    public int RateLimitPermitLimit { get; set; } = 60;

    /// <summary>Timeout for the DNS TXT ownership lookup during domain verification.</summary>
    public int DnsLookupTimeoutSeconds { get; set; } = 5;

    /// <summary>In-process resolve-host cache TTL, keyed by normalized host.</summary>
    public int ResolveCacheSeconds { get; set; } = 60;

    /// <summary>TXT record host label prefix used for the domain ownership challenge.</summary>
    public string TxtRecordPrefix { get; set; } = "_casazen-challenge";

    /// <summary>
    /// Minutes between the periodic checks of a domain that is not verified yet (BK-17); it activates by itself once the DNS
    /// records are in place. The host can check at any time with "Verifica ora".
    /// </summary>
    public int RecheckPendingMinutes { get; set; } = 30;

    /// <summary>Hours between the periodic checks of a verified domain: a removed DNS record is noticed within this time.</summary>
    public int RecheckVerifiedHours { get; set; } = 24;

    /// <summary>Days after which the periodic job stops checking a domain that never became verified (the host can still check by hand).</summary>
    public int MaxPendingDays { get; set; } = 14;

    /// <summary>Consecutive failed checks after which a verified domain is no longer reported as verified.</summary>
    public int FailuresBeforeDemotion { get; set; } = 2;

    /// <summary>Most domains one run of the periodic job checks.</summary>
    public int RecheckBatchSize { get; set; } = 50;
}

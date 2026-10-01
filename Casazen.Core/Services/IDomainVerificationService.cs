using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Stable codes of why a custom domain is not (or no longer) <see cref="DomainVerificationStatus.Verified"/> (BK-17,
/// <see cref="Org.DomainStatusDetail"/>). The settings page explains each in the user's language; never rename one.
/// </summary>
public static class DomainIssues
{
    /// <summary>The TXT ownership record <c>_casazen-challenge.{domain}</c> is missing or has another value. Pending.</summary>
    public const string OwnershipTxtMissing = "ownership_txt_missing";

    /// <summary>The domain does not point at Vercel (no CNAME to the target, no A record of Vercel). Pending.</summary>
    public const string DnsNotPointing = "dns_not_pointing";

    /// <summary>
    /// The domain is on the Vercel project but Vercel asks for its own TXT record first (the domain was used on another
    /// Vercel account). Pending, with the record to add.
    /// </summary>
    public const string VercelVerificationPending = "vercel_verification_pending";

    /// <summary>The platform cannot activate domains yet: <c>Vercel__ApiToken</c> or <c>Vercel__ProjectId</c> is not set. Pending, until the product owner configures it.</summary>
    public const string VercelNotConfigured = "vercel_not_configured";

    /// <summary>Vercel refused the platform's token or the project is not found: a platform setting is wrong. Pending, until it is fixed.</summary>
    public const string VercelUnauthorized = "vercel_unauthorized";

    /// <summary>Vercel did not answer or is rate limiting: retried by itself. Pending (a verified domain keeps its status).</summary>
    public const string VercelUnavailable = "vercel_unavailable";

    /// <summary>Another CasaZen org already has this domain verified (one org per domain). Failed.</summary>
    public const string DomainTaken = "domain_taken";

    /// <summary>The domain is assigned to another Vercel project or account that the platform cannot take it from. Failed.</summary>
    public const string VercelDomainInUse = "vercel_domain_in_use";

    /// <summary>Vercel does not accept the domain (invalid or not allowed). Failed.</summary>
    public const string VercelRejected = "vercel_rejected";
}

/// <summary>
/// Outcome of one check of a custom domain (BK-17): the status it now has and why. The Vercel TXT record is set only with
/// <see cref="DomainIssues.VercelVerificationPending"/>.
/// </summary>
public sealed record DomainVerificationResult(
    DomainVerificationStatus Status,
    string CustomDomain,
    DateTime CheckedAt,
    string? Detail,
    string? VercelTxtHost = null,
    string? VercelTxtValue = null);

/// <summary>
/// Checks a custom domain end to end and persists the resulting <see cref="Org.DomainVerificationStatus"/> (#298, BK-17,
/// A3-25): (1) the TXT ownership challenge, (2) the CNAME (or A record) towards Vercel, (3) the domain on the Vercel
/// project, added and verified through the Domains API. Only then is it <c>Verified</c>; a verified domain whose records
/// disappear is demoted after a few failed checks (it no longer stays verified forever). The caller
/// (<c>IOrgDomainService</c>, the periodic job) validates that <paramref name="org"/> is in <c>CustomDomain</c> mode with a
/// <c>CustomDomain</c> and a <c>DomainVerificationToken</c>, and that its plan allows the feature.
/// </summary>
public interface IDomainVerificationService
{
    Task<DomainVerificationResult> VerifyAsync(Org org, CancellationToken cancellationToken = default);
}

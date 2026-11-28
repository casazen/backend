using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class DomainIssues
{
    public const string OwnershipTxtMissing = "ownership_txt_missing";

    public const string DnsNotPointing = "dns_not_pointing";

    public const string VercelVerificationPending = "vercel_verification_pending";

    public const string VercelNotConfigured = "vercel_not_configured";

    public const string VercelUnauthorized = "vercel_unauthorized";

    public const string VercelUnavailable = "vercel_unavailable";

    public const string DomainTaken = "domain_taken";

    public const string VercelDomainInUse = "vercel_domain_in_use";

    public const string VercelRejected = "vercel_rejected";
}

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

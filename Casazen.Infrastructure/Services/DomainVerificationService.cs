using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDomainVerificationService"/>
/// <remarks>
/// <para>The order of the checks is the order of what the host has to do, so the first thing that is missing is what the page
/// tells: the ownership TXT (<c>{TxtRecordPrefix}.{domain}</c>, proves the host controls the DNS), the CNAME (or A record)
/// towards Vercel, then the domain on the Vercel project. The platform adds the domain to the project only once the ownership
/// TXT is there, so nobody can make it claim a domain they do not control.</para>
/// <para>Who is at fault decides the state. What the host can fix (a record missing) is <c>Pending</c> with its code; what only
/// the platform can fix (<c>Vercel__ApiToken</c> missing, Vercel down) is <c>Pending</c> too and is retried by itself, and
/// never takes a verified site down; a domain Vercel refuses or that belongs to another project is <c>Failed</c>. A verified
/// domain whose host-side records go missing is demoted after <see cref="PublicHostOptions.FailuresBeforeDemotion"/>
/// consecutive checks (A3-25: it used to stay verified forever).</para>
/// </remarks>
public sealed class DomainVerificationService(
    AppDbContext dbContext,
    IDnsTxtLookup dnsTxtLookup,
    IDnsRecordLookup dnsRecordLookup,
    IVercelDomainsClient vercel,
    IPublicHostResolver hostResolver,
    IOptions<PublicHostOptions> options,
    TimeProvider timeProvider,
    ILogger<DomainVerificationService> logger) : IDomainVerificationService
{
    private enum Outcome
    {
        Verified,

        /// <summary>The host has something to do (a record is missing, Vercel's own TXT).</summary>
        HostPending,

        /// <summary>Only the platform can fix it (not configured, token refused, Vercel unavailable).</summary>
        PlatformPending,

        /// <summary>A hard no: Vercel refuses the domain or it belongs to another project.</summary>
        Failed,
    }

    private sealed record Check(
        Outcome Outcome,
        string? Detail = null,
        bool OnVercelProject = false,
        VercelVerificationChallenge? Challenge = null);

    public async Task<DomainVerificationResult> VerifyAsync(Org org, CancellationToken cancellationToken = default)
    {
        var domain = org.CustomDomain!;
        var check = await EvaluateAsync(org, domain, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var statusBefore = org.DomainVerificationStatus;
        Apply(org, check, now);
        org.UpdatedAt = now;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (org.DomainVerificationStatus == DomainVerificationStatus.Verified)
        {
            // Lost the race for the unique verified domain to another org between the check and the save.
            org.DomainVerificationStatus = DomainVerificationStatus.Failed;
            org.DomainStatusDetail = DomainIssues.DomainTaken;
            org.DomainVerifiedAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Every check is a chance that what the host serves changed (verified, demoted): drop the cached answers of the host.
        if (statusBefore != org.DomainVerificationStatus)
            hostResolver.InvalidateCacheForHost(domain);

        logger.LogInformation(
            "Domain check of org {OrgId}: {Status} ({Detail})", org.Id, org.DomainVerificationStatus, org.DomainStatusDetail ?? "ok");

        return new DomainVerificationResult(
            org.DomainVerificationStatus,
            domain,
            now,
            org.DomainStatusDetail,
            org.DomainVercelTxtHost,
            org.DomainVercelTxtValue);
    }

    private async Task<Check> EvaluateAsync(Org org, string domain, CancellationToken cancellationToken)
    {
        var hostOptions = options.Value;

        // 0. One org per domain: another org that already has it verified keeps it (the unique index says the same).
        var takenByAnotherOrg = await dbContext.Orgs.AsNoTracking().AnyAsync(
            o => o.Id != org.Id
                 && o.CustomDomain == domain
                 && o.DomainVerificationStatus == DomainVerificationStatus.Verified,
            cancellationToken);
        if (takenByAnotherOrg)
            return new Check(Outcome.Failed, DomainIssues.DomainTaken);

        // 1. Ownership: the TXT record with the org's random token.
        var txtHost = $"{hostOptions.TxtRecordPrefix}.{domain}";
        var records = await LookupAsync(ct => dnsTxtLookup.LookupTxtAsync(txtHost, ct), cancellationToken);
        if (!records.Any(r => string.Equals(r, org.DomainVerificationToken, StringComparison.Ordinal)))
            return new Check(Outcome.HostPending, DomainIssues.OwnershipTxtMissing);

        // 2. The domain points at Vercel (CNAME to its target, or an A record of Vercel).
        if (!await PointsAtVercelAsync(domain, hostOptions, cancellationToken))
            return new Check(Outcome.HostPending, DomainIssues.DnsNotPointing);

        // 3. The domain is on the Vercel project and Vercel accepts it.
        if (!vercel.IsConfigured)
            return new Check(Outcome.PlatformPending, DomainIssues.VercelNotConfigured);

        return await CheckVercelAsync(domain, cancellationToken);
    }

    private async Task<Check> CheckVercelAsync(string domain, CancellationToken cancellationToken)
    {
        var current = await vercel.GetDomainAsync(domain, cancellationToken);
        if (current.Status == VercelCallStatus.NotFound)
        {
            var added = await vercel.AddDomainAsync(domain, cancellationToken);
            if (added.Status == VercelCallStatus.Rejected)
            {
                // A 400 also means "already on the project": read it again before calling it a refusal.
                var reread = await vercel.GetDomainAsync(domain, cancellationToken);
                if (reread.IsOk)
                    added = reread;
            }

            current = added;
        }

        if (!current.IsOk)
            return FromFailedCall(current.Status);

        var onProject = current.Value!;
        if (onProject.Verified)
            return new Check(Outcome.Verified, OnVercelProject: true);

        // On the project but not accepted yet: ask Vercel to check its challenge again, now that the DNS is right.
        var verified = await vercel.VerifyDomainAsync(domain, cancellationToken);
        if (verified.IsOk && verified.Value!.Verified)
            return new Check(Outcome.Verified, OnVercelProject: true);

        // A 400 on this call is "the challenge is not satisfied yet" (the host still has something to do), not a refusal.
        if (!verified.IsOk && verified.Status != VercelCallStatus.Rejected)
            return FromFailedCall(verified.Status) with { OnVercelProject = true };

        var challenge = (verified.Value ?? onProject).Verification
            .FirstOrDefault(c => string.Equals(c.Type, "TXT", StringComparison.OrdinalIgnoreCase));
        return new Check(Outcome.HostPending, DomainIssues.VercelVerificationPending, OnVercelProject: true, challenge);
    }

    private static Check FromFailedCall(VercelCallStatus status) => status switch
    {
        VercelCallStatus.Conflict => new Check(Outcome.Failed, DomainIssues.VercelDomainInUse),
        VercelCallStatus.Rejected => new Check(Outcome.Failed, DomainIssues.VercelRejected),
        // A refused token, or a project that is not found: a platform setting is wrong, the host did nothing wrong.
        VercelCallStatus.Unauthorized or VercelCallStatus.NotFound => new Check(Outcome.PlatformPending, DomainIssues.VercelUnauthorized),
        VercelCallStatus.NotConfigured => new Check(Outcome.PlatformPending, DomainIssues.VercelNotConfigured),
        _ => new Check(Outcome.PlatformPending, DomainIssues.VercelUnavailable),
    };

    private async Task<bool> PointsAtVercelAsync(string domain, PublicHostOptions hostOptions, CancellationToken cancellationToken)
    {
        var targets = await LookupAsync(ct => dnsRecordLookup.LookupCnameAsync(domain, ct), cancellationToken);
        if (targets.Any(target => IsVercelTarget(target, hostOptions)))
            return true;

        // The root of a domain cannot have a CNAME: an A record (or a flattened CNAME) with Vercel's address is as good.
        var addresses = await LookupAsync(ct => dnsRecordLookup.LookupAddressesAsync(domain, ct), cancellationToken);
        return addresses.Any(address => hostOptions.VercelAddresses.Contains(address, StringComparer.Ordinal));
    }

    /// <summary>True for the configured CNAME target and for a name under one of the accepted suffixes (never a look-alike).</summary>
    internal static bool IsVercelTarget(string target, PublicHostOptions hostOptions)
    {
        var name = target.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0)
            return false;

        if (string.Equals(name, hostOptions.VercelCnameTarget.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            return true;

        return hostOptions.AcceptedCnameSuffixes
            .Select(suffix => suffix.Trim().ToLowerInvariant())
            .Where(suffix => suffix.Length > 1)
            .Select(suffix => suffix.StartsWith('.') ? suffix : "." + suffix)
            .Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    private void Apply(Org org, Check check, DateTime now)
    {
        var wasVerified = org.DomainVerificationStatus == DomainVerificationStatus.Verified;
        var failuresBeforeDemotion = Math.Max(1, options.Value.FailuresBeforeDemotion);
        org.DomainCheckedAt = now;
        if (check.OnVercelProject)
            org.DomainVercelAddedAt ??= now;

        switch (check.Outcome)
        {
            case Outcome.Verified:
                org.DomainVerificationStatus = DomainVerificationStatus.Verified;
                org.DomainStatusDetail = null;
                org.DomainVerifiedAt = wasVerified ? org.DomainVerifiedAt ?? now : now;
                org.DomainCheckFailures = 0;
                SetVercelChallenge(org, null);
                break;

            case Outcome.PlatformPending:
                // Not the host's doing: a verified site stays up and is checked again soon; a pending one says why it waits.
                if (!wasVerified)
                {
                    org.DomainVerificationStatus = DomainVerificationStatus.Pending;
                    org.DomainStatusDetail = check.Detail;
                }
                break;

            case Outcome.HostPending when wasVerified:
                org.DomainCheckFailures++;
                if (org.DomainCheckFailures >= failuresBeforeDemotion)
                {
                    org.DomainVerificationStatus = DomainVerificationStatus.Failed;
                    org.DomainStatusDetail = check.Detail;
                    org.DomainVerifiedAt = null;
                    SetVercelChallenge(org, check.Challenge);
                }
                break;

            case Outcome.HostPending:
                org.DomainVerificationStatus = DomainVerificationStatus.Pending;
                org.DomainStatusDetail = check.Detail;
                org.DomainCheckFailures = 0;
                SetVercelChallenge(org, check.Challenge);
                break;

            case Outcome.Failed:
                org.DomainVerificationStatus = DomainVerificationStatus.Failed;
                org.DomainStatusDetail = check.Detail;
                org.DomainVerifiedAt = null;
                org.DomainCheckFailures = 0;
                SetVercelChallenge(org, null);
                break;
        }
    }

    private static void SetVercelChallenge(Org org, VercelVerificationChallenge? challenge)
    {
        org.DomainVercelTxtHost = challenge is null ? null : Truncate(challenge.Domain, 253);
        org.DomainVercelTxtValue = challenge is null ? null : Truncate(challenge.Value, 500);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private async Task<IReadOnlyList<string>> LookupAsync(
        Func<CancellationToken, Task<IReadOnlyList<string>>> lookup,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.DnsLookupTimeoutSeconds)));

        try
        {
            return await lookup(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out, not caller-cancelled: a lookup miss.
            return [];
        }
    }
}

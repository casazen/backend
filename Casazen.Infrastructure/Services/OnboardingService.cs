using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class OnboardingService(
    AppDbContext db,
    ILegalDocumentService legalDocumentService,
    IUserAuthorizationCache authorizationCache,
    PublicSiteLinks publicSiteLinks) : IOnboardingService
{
    public (bool Success, ConsentValidationError? Error) ValidateConsents(
        OnboardingConsentsInput? consents,
        bool requireConsents)
    {
        if (consents is null)
        {
            if (requireConsents)
                return (false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "ConsentsIncomplete"));

            return (true, null);
        }

        var stale = ValidateVersions(consents);
        if (stale.Length > 0)
            return (false, new ConsentValidationError(ConsentValidationErrorType.StaleVersion, "ConsentsStale", stale));

        if (!consents.TosAccepted || !consents.PrivacyAccepted || !consents.DpaAccepted || !consents.SubprocessorsAcknowledged)
            return (false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "ConsentsIncomplete"));

        return (true, null);
    }

    public async Task<(bool Success, ConsentValidationError? Error, bool ConsentsRecorded)> ValidateAndRecordConsentsAsync(
        string userId,
        Guid orgId,
        OnboardingConsentsInput? consents,
        bool requireConsents,
        string? clientIpAddress,
        CancellationToken cancellationToken)
    {
        var (success, error) = ValidateConsents(consents, requireConsents);
        if (!success)
            return (false, error, false);

        if (consents is null)
            return (true, null, false);

        var now = DateTime.UtcNow;
        var records = new List<ConsentRecord>
        {
            new() { UserId = userId, OrgId = orgId, Type = ConsentType.Tos, Version = consents.TosVersion, IpAddress = clientIpAddress, RecordedAt = now },
            new() { UserId = userId, OrgId = orgId, Type = ConsentType.Privacy, Version = consents.PrivacyVersion, IpAddress = clientIpAddress, RecordedAt = now },
            new() { UserId = userId, OrgId = orgId, Type = ConsentType.Dpa, Version = consents.DpaVersion, IpAddress = clientIpAddress, RecordedAt = now },
            new() { UserId = userId, OrgId = orgId, Type = ConsentType.SubprocessorsAck, Version = consents.SubprocessorsVersion, IpAddress = clientIpAddress, RecordedAt = now },
        };

        if (consents.MarketingOptIn == true)
        {
            records.Add(new ConsentRecord
            {
                UserId = userId,
                OrgId = orgId,
                Type = ConsentType.Marketing,
                Version = consents.TosVersion,
                IpAddress = clientIpAddress,
                RecordedAt = now,
            });
        }

        db.ConsentRecords.AddRange(records);
        await db.SaveChangesAsync(cancellationToken);
        // The host onboarding gate reads the consents from the cached authorization snapshot (PL-02).
        authorizationCache.Invalidate(userId);
        return (true, null, true);
    }

    public async Task<OnboardingActivationStatus> GetActivationStatusAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return ActivationChecklist.Build(new ActivationFacts(false, false, false, null, ActivationPropertyFacts.None, false));

        var roleChosen = user.RentalType.HasValue;
        var orgProvisioned = user.OrgId.HasValue;
        var orgId = user.OrgId;

        // IgnoreQueryFilters below: every query is scoped explicitly to the caller's own org, read from
        // Users just now. The tenant filter may still hold the null org cached before OrgContextResolver
        // provisioned the org earlier in this same request, and would then hide the caller's own rows.
        var consentsAccepted = false;
        if (orgId.HasValue)
        {
            var tos = legalDocumentService.GetTos();
            var privacy = legalDocumentService.GetPrivacy();
            var dpa = legalDocumentService.GetDpa();
            var subprocessors = legalDocumentService.GetSubprocessors();
            var userConsents = await db.ConsentRecords.IgnoreQueryFilters()
                .Where(c => c.UserId == userId && c.OrgId == orgId)
                .Select(c => new { c.Type, c.Version })
                .ToListAsync(cancellationToken);

            consentsAccepted =
                userConsents.Any(c => c.Type == ConsentType.Tos && c.Version == tos.Version)
                && userConsents.Any(c => c.Type == ConsentType.Privacy && c.Version == privacy.Version)
                && userConsents.Any(c => c.Type == ConsentType.Dpa && c.Version == dpa.Version)
                && userConsents.Any(c => c.Type == ConsentType.SubprocessorsAck && c.Version == subprocessors.Version);
        }

        Org? org = null;
        if (orgId.HasValue)
        {
            org = await db.Orgs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        }

        var properties = org is null
            ? ActivationPropertyFacts.None
            : await LoadPropertyFactsAsync(org.Id, cancellationToken);

        var firstBookingTaken = orgId.HasValue && await db.Bookings.IgnoreQueryFilters()
            .AnyAsync(
                b => b.OrgId == orgId
                     && b.Status == BookingStatus.Confirmed
                     && b.Source == BookingSource.Direct,
                cancellationToken);

        var status = ActivationChecklist.Build(new ActivationFacts(
            roleChosen, orgProvisioned, consentsAccepted, org, properties, firstBookingTaken));

        // Only a site that is really published has a link to share. On App:PublicSiteBaseUrl (D3, no fallback domain):
        // null also when it is not configured (Development/Testing).
        if (status.SitePublished && org is not null && !string.IsNullOrWhiteSpace(org.Slug))
            return status with { PublicBookingUrl = publicSiteLinks.TryPublicPage($"/book/{Uri.EscapeDataString(org.Slug)}") };

        return status;
    }

    /// <summary>
    /// Counts the org's properties for the checklist. Tenant filter only is lifted (PC-05): a soft-deleted property no
    /// longer counts as created, published or with a CIN. "Published" is <see cref="PublicListing.IsPublished"/> itself,
    /// evaluated by the database, so the checklist cannot drift from what the public site shows. The checklist is the
    /// short-rent one (booking site, CIN, payments), so only the short-rent properties count (PM-01): a property in
    /// long-term mode is neither "created", nor "without a CIN", nor waiting for the compliance activation. Internal for
    /// the tests.
    /// </summary>
    internal async Task<ActivationPropertyFacts> LoadPropertyFactsAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var ofOrg = db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.OrgId == orgId);

        var published = await ofOrg.Where(PublicListing.IsPublished).CountAsync(cancellationToken);
        var rows = await ofOrg
            .Select(p => new { p.IsActive, p.IsPaused, p.ComplianceStatus, p.CinCode })
            .ToListAsync(cancellationToken);

        return new ActivationPropertyFacts(
            rows.Count,
            // Computed on read, never stored (compliance.md): the same CinFormat as the compliance gate.
            rows.Count(r => CinFormat.IsValid(r.CinCode)),
            published,
            rows.Count(r => r.IsActive && r.IsPaused && r.ComplianceStatus == PropertyComplianceStatus.Active),
            rows.Count(r => r.IsActive && r.ComplianceStatus != PropertyComplianceStatus.Active));
    }

    private string[] ValidateVersions(OnboardingConsentsInput consents)
    {
        var stale = new List<string>();
        var tos = legalDocumentService.GetTos();
        var privacy = legalDocumentService.GetPrivacy();
        var dpa = legalDocumentService.GetDpa();
        var subprocessors = legalDocumentService.GetSubprocessors();

        if (consents.TosVersion != tos.Version) stale.Add("tos");
        if (consents.PrivacyVersion != privacy.Version) stale.Add("privacy");
        if (consents.DpaVersion != dpa.Version) stale.Add("dpa");
        if (consents.SubprocessorsVersion != subprocessors.Version) stale.Add("subprocessors");

        return stale.ToArray();
    }
}

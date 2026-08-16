using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public class OnboardingService(
    AppDbContext db,
    ILegalDocumentService legalDocumentService,
    IConfiguration configuration) : IOnboardingService
{
    public (bool Success, ConsentValidationError? Error) ValidateConsents(
        OnboardingConsentsInput? consents,
        bool requireConsents)
    {
        if (consents is null)
        {
            if (requireConsents)
                return (false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "Tutti i consensi obbligatori devono essere accettati."));

            return (true, null);
        }

        var stale = ValidateVersions(consents);
        if (stale.Length > 0)
            return (false, new ConsentValidationError(ConsentValidationErrorType.StaleVersion, "Alcuni documenti legali sono stati aggiornati. Accetta le versioni correnti.", stale));

        if (!consents.TosAccepted || !consents.PrivacyAccepted || !consents.DpaAccepted || !consents.SubprocessorsAcknowledged)
            return (false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "Tutti i consensi obbligatori devono essere accettati."));

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
        return (true, null, true);
    }

    public async Task<OnboardingActivationStatus> GetActivationStatusAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return new OnboardingActivationStatus(false, false, false, false, false, false, false, null);

        var roleChosen = user.RentalType.HasValue;
        var orgProvisioned = user.OrgId.HasValue;
        var orgId = user.OrgId;

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

        var propertyCreated = orgId.HasValue && await db.Properties.IgnoreQueryFilters()
            .AnyAsync(p => p.OrgId == orgId, cancellationToken);

        Org? org = null;
        if (orgId.HasValue)
        {
            org = await db.Orgs.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        }

        var hasActiveProperty = orgId.HasValue && await db.Properties.IgnoreQueryFilters()
            .AnyAsync(p => p.OrgId == orgId && p.IsActive, cancellationToken);

        var sitePublished = org is { IsActive: true } && hasActiveProperty;

        var firstBookingTaken = orgId.HasValue && await db.Bookings.IgnoreQueryFilters()
            .AnyAsync(
                b => b.OrgId == orgId
                     && b.Status == BookingStatus.Confirmed
                     && b.Source == BookingSource.Direct,
                cancellationToken);

        string? publicBookingUrl = null;
        if (sitePublished && org is not null && !string.IsNullOrWhiteSpace(org.Slug))
        {
            var baseUrl = (configuration["App:PublicSiteBaseUrl"] ?? "https://casazen.app").TrimEnd('/');
            publicBookingUrl = $"{baseUrl}/book/{org.Slug}";
        }

        var activated = roleChosen
                        && orgProvisioned
                        && consentsAccepted
                        && propertyCreated
                        && sitePublished
                        && firstBookingTaken;

        return new OnboardingActivationStatus(
            roleChosen,
            orgProvisioned,
            consentsAccepted,
            propertyCreated,
            sitePublished,
            firstBookingTaken,
            activated,
            publicBookingUrl);
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

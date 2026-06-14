using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public class OnboardingService(
    AppDbContext db,
    ILegalDocumentService legalDocumentService) : IOnboardingService
{
    public async Task<(bool Success, ConsentValidationError? Error, bool ConsentsRecorded)> ValidateAndRecordConsentsAsync(
        string userId,
        Guid orgId,
        OnboardingConsentsInput? consents,
        bool requireConsents,
        string? clientIpAddress,
        CancellationToken cancellationToken)
    {
        if (consents is null)
        {
            if (requireConsents)
                return (false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "Tutti i consensi obbligatori devono essere accettati."), false);

            return (true, null, false);
        }

        var stale = ValidateVersions(consents);
        if (stale.Length > 0)
            return (false, new ConsentValidationError(ConsentValidationErrorType.StaleVersion, "Alcuni documenti legali sono stati aggiornati. Accetta le versioni correnti.", stale), false);

        if (!consents.TosAccepted || !consents.PrivacyAccepted || !consents.DpaAccepted || !consents.SubprocessorsAcknowledged)
            return (false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "Tutti i consensi obbligatori devono essere accettati."), false);

        var now = DateTime.UtcNow;
        db.ConsentRecords.AddRange(
            new ConsentRecord { UserId = userId, OrgId = orgId, Type = ConsentType.Tos, Version = consents.TosVersion, IpAddress = clientIpAddress, RecordedAt = now },
            new ConsentRecord { UserId = userId, OrgId = orgId, Type = ConsentType.Privacy, Version = consents.PrivacyVersion, IpAddress = clientIpAddress, RecordedAt = now },
            new ConsentRecord { UserId = userId, OrgId = orgId, Type = ConsentType.Dpa, Version = consents.DpaVersion, IpAddress = clientIpAddress, RecordedAt = now },
            new ConsentRecord { UserId = userId, OrgId = orgId, Type = ConsentType.SubprocessorsAck, Version = consents.SubprocessorsVersion, IpAddress = clientIpAddress, RecordedAt = now }
        );

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

        var consentsAccepted = orgId.HasValue && await db.ConsentRecords.IgnoreQueryFilters()
            .AnyAsync(c => c.UserId == userId, cancellationToken);

        var propertyCreated = orgId.HasValue && await db.Properties.IgnoreQueryFilters()
            .AnyAsync(p => p.OrgId == orgId, cancellationToken);

        const bool sitePublished = false;

        var firstBookingTaken = orgId.HasValue && await db.Bookings.IgnoreQueryFilters()
            .AnyAsync(b => b.OrgId == orgId, cancellationToken);

        var activated = roleChosen && orgProvisioned && consentsAccepted && propertyCreated;

        return new OnboardingActivationStatus(roleChosen, orgProvisioned, consentsAccepted, propertyCreated, sitePublished, firstBookingTaken, activated, null);
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

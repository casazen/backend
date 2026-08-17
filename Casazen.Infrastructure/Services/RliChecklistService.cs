using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class RliChecklistService(
    ILeaseContractRepository leases,
    ILeaseRegistrationAuthorizationRepository authorizations,
    ILeaseEventRepository events,
    IOptions<RliOptions> rliOptions) : IRliChecklistService
{
    public async Task<RliChecklistResult?> GetAsync(
        Guid leaseId, string ownerId, CancellationToken cancellationToken = default)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease is null || lease.Property is null || lease.Property.OwnerId != ownerId)
            return null;

        var auth = await authorizations.GetByLeaseIdAsync(lease.Id);
        var leaseEvents = (await events.GetByLeaseIdAsync(lease.Id)).ToList();
        var daysRemaining = (int)(lease.RegistrationDeadline.Date - DateTime.UtcNow.Date).TotalDays;

        var items = new List<RliChecklistItem>
        {
            new("contract_signed", "Contratto firmato da tutte le parti",
                lease.Status is LeaseStatus.Signed or LeaseStatus.SentToProvider
                    or LeaseStatus.RegistrationPending or LeaseStatus.Registered),
            new("delega_captured", "Delega / attestazione RLI registrata", auth is { AttestationAccepted: true }),
            new("rli_exported", "Dataset RLI esportato per revisione",
                leaseEvents.Any(e => e.EventType == LeaseEventType.RliExported)),
            new("rli_submitted", "RLI inviato al canale di filing",
                lease.Registration is not null || leaseEvents.Any(e => e.EventType == LeaseEventType.RegistrationSubmitted)),
            new("rli_registered", "Ricevuta di registrazione disponibile",
                lease.Status == LeaseStatus.Registered || lease.Registration?.Status == RegistrationStatus.Registered),
        };

        if (lease.HasExtraEUTenant)
        {
            items.Add(new(
                "questura_extra_eu",
                "Comunicazione Questura (Art. 7 D.Lgs 286/1998) — cessione di fabbricato. Bozza da confermare con legale.",
                leaseEvents.Any(e =>
                    e.EventType == LeaseEventType.DeadlineReminderSent && e.Payload == "extra-eu")));
        }

        return new RliChecklistResult(
            lease.RegistrationDeadline,
            daysRemaining,
            rliOptions.Value.TosVersion,
            rliOptions.Value.AttestationText,
            items);
    }
}

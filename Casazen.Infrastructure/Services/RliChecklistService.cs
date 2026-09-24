using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public class RliChecklistService(
    ILeaseRegistrationAuthorizationRepository authorizations,
    ILeaseEventRepository events,
    IOptions<RliOptions> rliOptions,
    TimeProvider? timeProvider = null) : IRliChecklistService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RliChecklistResult> GetAsync(LeaseContract lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var auth = await authorizations.GetByLeaseIdAsync(lease.Id);
        var leaseEvents = (await events.GetByLeaseIdAsync(lease.Id)).ToList();
        var daysRemaining = (int)(lease.RegistrationDeadline.Date - _clock.TodayInRome()).TotalDays;

        var items = new List<RliChecklistItem>
        {
            new(RliChecklistKeys.ContractSigned,
                lease.Status is LeaseStatus.Signed or LeaseStatus.SentToProvider
                    or LeaseStatus.RegistrationPending or LeaseStatus.Registered),
            new(RliChecklistKeys.DelegaCaptured, auth is { AttestationAccepted: true }),
            new(RliChecklistKeys.RliExported, leaseEvents.Any(e => e.EventType == LeaseEventType.RliExported)),
            new(RliChecklistKeys.RliSubmitted,
                lease.Registration is not null || leaseEvents.Any(e => e.EventType == LeaseEventType.RegistrationSubmitted)),
            new(RliChecklistKeys.RliRegistered,
                lease.Status == LeaseStatus.Registered || lease.Registration?.Status == RegistrationStatus.Registered),
        };

        if (lease.HasExtraEUTenant)
        {
            items.Add(new(
                RliChecklistKeys.QuesturaExtraEu,
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

using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// RLI checklist of a lease (LT-01, A7-01): a step is ticked only when it really happened. The registration item is
/// done only with the registration recorded and its receipt stored (manual declaration or provider receipt), never
/// for a submission in progress; a failed attempt is reported as failed. The delega item exists only while the provider
/// path is available (flag on and configured provider), or once a delega was given.
/// </summary>
public class RliChecklistService(
    ILeaseRegistrationAuthorizationRepository authorizations,
    ILeaseEventRepository events,
    IOptions<RliOptions> rliOptions,
    IFeatureFlags featureFlags,
    ILeaseRegistrationProvider registrationProvider,
    TimeProvider? timeProvider = null) : IRliChecklistService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RliChecklistResult> GetAsync(LeaseContract lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var providerFilingAvailable = RliProviderFiling.IsAvailable(featureFlags, registrationProvider);
        var auth = await authorizations.GetByLeaseIdAsync(lease.Id);
        var leaseEvents = (await events.GetByLeaseIdAsync(lease.Id)).ToList();
        // min(stipula, start) + 30 on the Rome calendar, or null while it is to be determined (LT-04, A7-04).
        var today = _clock.TodayInRome();
        var deadline = RliRegistrationDeadline.Resolve(lease, today);
        int? daysRemaining = deadline is { } due ? RliRegistrationDeadline.DaysRemaining(due, today) : null;
        var registration = lease.Registration;

        var items = new List<RliChecklistItem>
        {
            new(RliChecklistKeys.ContractSigned,
                lease.Status is LeaseStatus.Signed or LeaseStatus.SentToProvider
                    or LeaseStatus.RegistrationPending or LeaseStatus.Registered),
        };

        var delegaCaptured = auth is { AttestationAccepted: true };
        if (providerFilingAvailable || delegaCaptured)
            items.Add(new(RliChecklistKeys.DelegaCaptured, delegaCaptured));

        items.Add(new(RliChecklistKeys.RliExported, leaseEvents.Any(e => e.EventType == LeaseEventType.RliExported)));
        items.Add(new(
            RliChecklistKeys.RliRegistered,
            Done: lease.Status == LeaseStatus.Registered
                && registration is { Status: RegistrationStatus.Registered }
                && !string.IsNullOrWhiteSpace(registration.ReceiptStoragePath),
            Failed: registration is { Status: RegistrationStatus.Failed }));

        if (lease.HasExtraEUTenant)
        {
            items.Add(new(
                RliChecklistKeys.QuesturaExtraEu,
                leaseEvents.Any(e =>
                    e.EventType == LeaseEventType.DeadlineReminderSent && e.Payload == "extra-eu")));
        }

        return new RliChecklistResult(
            deadline,
            daysRemaining,
            rliOptions.Value.TosVersion,
            rliOptions.Value.AttestationText,
            providerFilingAvailable,
            items);
    }
}

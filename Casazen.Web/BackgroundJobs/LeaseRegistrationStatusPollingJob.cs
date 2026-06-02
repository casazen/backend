using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

public class LeaseRegistrationStatusPollingJob(
    ILeaseContractRepository leaseRepository,
    ILeaseRegistrationRepository registrationRepository,
    ILeaseRegistrationService registrationService,
    ILeaseEventRepository eventRepository,
    ILogger<LeaseRegistrationStatusPollingJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task ExecuteAsync()
    {
        var pending = await registrationRepository.GetByStatusAsync(RegistrationStatus.SentToProvider);
        logger.LogInformation("Polling registration status for {Count} registrations", pending.Count());

        foreach (var registration in pending)
        {
            try
            {
                if (registration.ExternalRegistrationId is null) continue;

                var statusResult = await registrationService.PollStatusAsync(registration.ExternalRegistrationId);

                if (!statusResult.IsConfirmed) continue;

                registration.Status = RegistrationStatus.Registered;
                registration.RegistrationCode = statusResult.RegistrationCode;
                registration.ConfirmedAt = DateTime.UtcNow;
                await registrationRepository.UpdateAsync(registration);

                var lease = await leaseRepository.GetByIdAsync(registration.LeaseContractId);
                if (lease is not null)
                {
                    lease.Status = LeaseStatus.Registered;
                    await leaseRepository.UpdateAsync(lease);
                    await eventRepository.AddAsync(new LeaseEvent
                    {
                        LeaseContractId = lease.Id,
                        EventType = LeaseEventType.RegistrationConfirmed,
                        Payload = statusResult.RegistrationCode
                    });
                }

                logger.LogInformation("Registration confirmed. LeaseId={LeaseId} Code={Code}",
                    registration.LeaseContractId, statusResult.RegistrationCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling registration status for ExternalId={ExternalId}",
                    registration.ExternalRegistrationId);
            }
        }
    }
}

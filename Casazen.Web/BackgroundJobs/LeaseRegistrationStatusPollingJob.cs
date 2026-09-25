using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Polls the RLI filing provider (LT-01): registrations confirmed with a receipt become Registered, rejected ones
/// Failed, and reservations left without an outcome are failed so the landlord can retry or register manually.
/// Registered as a recurring job only with <c>Features:RliProvider</c> on; without a configured provider it does
/// nothing (<see cref="IRliRegistrationService.SyncProviderRegistrationsAsync"/>).
/// </summary>
public class LeaseRegistrationStatusPollingJob(
    IRliRegistrationService registrations,
    ILogger<LeaseRegistrationStatusPollingJob> logger)
{
    public const string RecurringJobId = "lease-registration-status-poll";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task ExecuteAsync()
    {
        var result = await registrations.SyncProviderRegistrationsAsync();
        logger.LogInformation(
            "RLI provider sync: {Registered} registered, {Failed} failed, {InProgress} in progress, {Errors} errors",
            result.Registered, result.Failed, result.StillInProgress, result.Errors);
    }
}

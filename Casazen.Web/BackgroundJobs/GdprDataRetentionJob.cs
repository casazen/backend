using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly retention of personal data (docs/runbooks/gdpr.md): <see cref="IGuestDataRetentionService"/> applies each
/// configured <c>Gdpr:Retention:*</c> guest period to every org (CO-15), then <see cref="ILeasePartyPrivacyService"/>
/// anonymizes the parties of the ended leases with an erasure request or past <c>Gdpr:Retention:LeaseParties</c> (LT-12).
/// Periods without a cited source are skipped with a warning. Idempotent: a second run the same night changes nothing.
/// </summary>
public class GdprDataRetentionJob(
    IGuestDataRetentionService retentionService,
    ILeasePartyPrivacyService leasePartyPrivacy,
    ILogger<GdprDataRetentionJob> logger)
{
    public const string RecurringJobId = "gdpr-data-retention";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var result = await retentionService.ApplyAsync();
        logger.LogInformation(
            "GDPR retention job done: {Configured} of {Total} categories configured",
            result.Categories.Count(c => c.Configured),
            result.Categories.Count);

        var leases = await leasePartyPrivacy.ApplyRetentionAsync();
        logger.LogInformation(
            "GDPR lease party retention done: period configured {Configured}, {Leases} leases anonymized",
            leases.RetentionConfigured,
            leases.Leases);
    }
}

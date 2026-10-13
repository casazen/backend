using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly retention of personal data (docs/runbooks/gdpr.md): <see cref="IGuestDataRetentionService"/> applies each
/// configured <c>Gdpr:Retention:*</c> guest period to every org (CO-15), then <see cref="ILeasePartyPrivacyService"/>
/// anonymizes the parties of the ended leases with an erasure request or past <c>Gdpr:Retention:LeaseParties</c> (LT-12), and
/// <see cref="IServiceCustomerPrivacyService"/> the private customers of the suppliers (and the place of their requests) past
/// <c>Gdpr:Retention:SupplierCustomers</c> (SP-10). Periods without a cited source are skipped with a warning. Idempotent: a
/// second run the same night changes nothing.
/// </summary>
public class GdprDataRetentionJob(
    IGuestDataRetentionService retentionService,
    ILeasePartyPrivacyService leasePartyPrivacy,
    IServiceCustomerPrivacyService serviceCustomerPrivacy,
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

        // SP-10: the customers of the suppliers' public showcases. Without a period and its source nothing is anonymized.
        var customers = await serviceCustomerPrivacy.ApplyRetentionAsync();
        logger.LogInformation(
            "GDPR supplier customer retention done: period configured {Configured}, {Customers} customers and the place of {Requests} requests anonymized",
            customers.RetentionConfigured,
            customers.Customers,
            customers.Requests);
    }
}

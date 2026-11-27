using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly retention of personal data (docs/runbooks/gdpr.md): <see cref="IGuestDataRetentionService"/> applies each
/// configured <c>Gdpr:Retention:*</c> guest period to every org (CO-15), then <see cref="ILeasePartyPrivacyService"/>
/// anonymizes the parties of the ended leases with an erasure request or past <c>Gdpr:Retention:LeaseParties</c> (LT-12), and
/// <see cref="IServiceCustomerPrivacyService"/> the private customers of the suppliers (and the place of their requests) past
/// <c>Gdpr:Retention:SupplierCustomers</c> (SP-10), and last <see cref="IWizardDraftRetentionService"/> anonymizes the wizard drafts
/// past their 30 days from the last save (ADR-024: a fixed rule of the product, not a configured period), and last
/// <see cref="IOtaMessageRetentionService"/> the copy of the conversations of the guests with the channels (E05/F01/US01/T03, ADR-032-R13), which
/// follows the retention of the guest data (<c>Gdpr:Retention:AlloggiatiData</c>, no period of its own). Periods without a cited
/// source are skipped with a warning. Idempotent: a second run the same night changes nothing.
/// </summary>
public class GdprDataRetentionJob(
    IGuestDataRetentionService retentionService,
    ILeasePartyPrivacyService leasePartyPrivacy,
    IServiceCustomerPrivacyService serviceCustomerPrivacy,
    IWizardDraftRetentionService wizardDraftRetention,
    IOtaMessageRetentionService otaMessageRetention,
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

        // ADR-024: the drafts of the wizards past their 30 days lose the data and the person; the row stays for the audit trail.
        var drafts = await wizardDraftRetention.ApplyRetentionAsync();
        logger.LogInformation("GDPR wizard draft retention done: {Drafts} drafts anonymized", drafts.Drafts);

        // ADR-032-R13: the copy of the OTA conversations follows the retention of the guest data of its stay. Without the period and its source
        // nothing is anonymized (the service says so in the log).
        var conversations = await otaMessageRetention.ApplyRetentionAsync();
        logger.LogInformation(
            "GDPR OTA message retention done: period configured {Configured}, {Threads} conversations and {Messages} messages anonymized",
            conversations.RetentionConfigured,
            conversations.Threads,
            conversations.Messages);
    }
}

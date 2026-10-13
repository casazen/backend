using Casazen.Core.Options;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Placeholder job for pulling official IRPEF and STR fiscal rates into <see cref="ShortStayFiscalOptions"/> (CO-18,
/// PO 2026-10-08: rates come from official sources, updated by a job, no invented values).
///
/// When a verified official source (e.g. the Agenzia delle Entrate open-data API or a curated legislative feed) is
/// configured, this job will fetch the current cedolare secca rates, OTA withholding rate and the apartment threshold,
/// compare them with the running configuration, and — if changed — write the new values to a writable configuration
/// store (e.g. a dedicated DB table) and notify the admin. Until then it is a no-op that logs its own status so
/// operators know the feature is registered but not yet wired to a data source.
///
/// DO NOT invent rates here: the only values in production must come from a cited official source.
/// </summary>
public class FiscalRatesUpdateJob(
    IOptions<ShortStayFiscalOptions> fiscalOptions,
    ILogger<FiscalRatesUpdateJob> logger)
{
    public const string RecurringJobId = "fiscal-rates-update";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public Task ExecuteAsync()
    {
        // TODO (CO-18): connect to an official Italian legislative data source to pull current rates.
        // Until a source is configured this job is intentionally a no-op.
        var rules = fiscalOptions.Value;
        logger.LogInformation(
            "FiscalRatesUpdateJob: no official source configured yet. " +
            "Current rates — cedolare: {Cedolare26}%/{Cedolare21}%, OTA withholding: {Withholding}%, " +
            "threshold: {Threshold} apartments (from tax year {From}). " +
            "Source: {ThresholdSource}",
            rules.CedolareRate * 100,
            rules.CedolareReducedRate * 100,
            rules.OtaWithholdingRate * 100,
            rules.MaxApartmentsPerTaxpayer,
            rules.ThresholdFromTaxYear,
            rules.ThresholdSource);

        return Task.CompletedTask;
    }
}

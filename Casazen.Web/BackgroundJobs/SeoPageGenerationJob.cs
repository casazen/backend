using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Generates SEO pages (bootstrap and admin "Genera"). Every text is stored as a draft revision that waits for a human
/// review in the admin dashboard: this job never approves anything (SE-01, A8-04).
/// </summary>
public class SeoPageGenerationJob(ISeoContentService seoContentService, ILogger<SeoPageGenerationJob> logger)
{
    /// <summary>Bootstrap: the compliance guide and the tourist tax page of every comune of <paramref name="comuneCodes"/>.</summary>
    public Task ExecuteAsync(IReadOnlyList<string> comuneCodes) =>
        ExecuteAsync(comuneCodes, [SeoPageType.ComplianceGuide, SeoPageType.TouristTaxCalc], forceRegenerate: false);

    public async Task ExecuteAsync(
        IReadOnlyList<string> comuneCodes,
        IReadOnlyList<SeoPageType> pageTypes,
        bool forceRegenerate)
    {
        logger.LogInformation(
            "Starting SEO page generation for {ComuneCount} comuni, {PageTypeCount} page types",
            comuneCodes.Count,
            pageTypes.Count);

        var generated = await seoContentService.GeneratePagesForComuneBatchAsync(
            comuneCodes,
            pageTypes,
            forceRegenerate,
            CancellationToken.None);

        logger.LogInformation(
            "SEO page generation completed: {GeneratedCount} draft revisions stored, waiting for review",
            generated);
    }

    /// <summary>
    /// Signature of the bootstrap jobs queued before SE-01, kept so that such a job still runs. The auto-approval flag is
    /// ignored: nothing is approved automatically any more.
    /// </summary>
    public Task ExecuteAsync(IReadOnlyList<string> comuneCodes, bool autoApproveCounsel)
    {
        LogIgnoredAutoApproval(autoApproveCounsel);
        return ExecuteAsync(comuneCodes);
    }

    /// <summary>Signature of the admin jobs queued before SE-01; the auto-approval flag is ignored (see above).</summary>
    public Task ExecuteAsync(
        IReadOnlyList<string> comuneCodes,
        IReadOnlyList<SeoPageType> pageTypes,
        bool forceRegenerate,
        bool autoApproveCounsel)
    {
        LogIgnoredAutoApproval(autoApproveCounsel);
        return ExecuteAsync(comuneCodes, pageTypes, forceRegenerate);
    }

    private void LogIgnoredAutoApproval(bool autoApproveCounsel)
    {
        if (autoApproveCounsel)
            logger.LogWarning("SEO job queued before SE-01 asked for auto-approval: ignored, the pages stay drafts");
    }
}

using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.BackgroundJobs;

public class SeoPageGenerationJob(ISeoContentService seoContentService, ILogger<SeoPageGenerationJob> logger)
{
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

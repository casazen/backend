using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.BackgroundJobs;

public class SeoPageGenerationJob(ISeoContentService seoContentService, ILogger<SeoPageGenerationJob> logger)
{
    public Task ExecuteAsync(IReadOnlyList<string> comuneCodes, bool autoApproveCounsel) =>
        ExecuteAsync(
            comuneCodes,
            [SeoPageType.ComplianceGuide, SeoPageType.TouristTaxCalc],
            forceRegenerate: false,
            autoApproveCounsel);

    public async Task ExecuteAsync(
        IReadOnlyList<string> comuneCodes,
        IReadOnlyList<SeoPageType> pageTypes,
        bool forceRegenerate,
        bool autoApproveCounsel = false)
    {
        logger.LogInformation(
            "Starting SEO page generation for {ComuneCount} comuni, {PageTypeCount} page types (autoApprove={AutoApprove})",
            comuneCodes.Count,
            pageTypes.Count,
            autoApproveCounsel);

        var generated = await seoContentService.GeneratePagesForComuneBatchAsync(
            comuneCodes,
            pageTypes,
            forceRegenerate,
            CancellationToken.None);

        if (autoApproveCounsel && generated > 0)
        {
            var approved = await seoContentService.ApproveAllDraftPagesAsync(counselApproved: true, CancellationToken.None);
            logger.LogInformation("SEO bootstrap auto-approved {ApprovedCount} draft pages", approved);
        }

        logger.LogInformation("SEO page generation completed: {GeneratedCount} pages generated/refreshed", generated);
    }
}

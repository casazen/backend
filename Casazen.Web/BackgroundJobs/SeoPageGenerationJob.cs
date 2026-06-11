using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.BackgroundJobs;

public class SeoPageGenerationJob(ISeoContentService seoContentService, ILogger<SeoPageGenerationJob> logger)
{
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

        logger.LogInformation("SEO page generation completed: {GeneratedCount} pages generated/refreshed", generated);
    }
}

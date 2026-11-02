using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IShowcaseAlternativesService"/>
/// <remarks>
/// <para><b>One ranking.</b> The candidates come from <see cref="ISupplierMatchService.RankActiveAsync"/>: the <c>Active</c> suppliers that cover
/// the comune (by ISTAT code when the customer chose it, by name otherwise) and declared the category, ordered by the score that recommends a
/// supplier to a host (<see cref="SupplierRanking"/>: load, urgency, bio and the reliability of the last days, weights in <c>SupplierRanking:*</c>).
/// There is no second formula here, and the score itself never leaves this class: only the order does.</para>
/// <para><b>Who qualifies.</b> The supplier of the request is skipped; of the others, in order, the first
/// <see cref="ShowcaseAlternativesLimits.Max"/> whose showcase is published, not paused and takes bookings (<see cref="ShowcaseAlternativeRules.Qualifies"/>).
/// At most <see cref="ShowcaseAlternativesLimits.MaxCandidates"/> are looked at. A supplier that cannot be described (an error reading its page) is
/// skipped and logged by id: the customer still gets the others.</para>
/// <para><b>What leaves.</b> The name, the slug, the cover, «Verificato», the typical time to answer and the «da €» of the service that
/// corresponds, all as the showcase already says them; read through <see cref="IPublicSupplierShowcaseService"/>, which is the only reader of the
/// supplier's catalog and agenda. No contact, no address, no score, no id, and no way to list suppliers without a request that lapsed.</para>
/// </remarks>
public sealed class ShowcaseAlternativesService(
    ISupplierMatchService match,
    IPublicSupplierShowcaseService showcase,
    IFeatureFlags features,
    ILogger<ShowcaseAlternativesService> logger) : IShowcaseAlternativesService
{
    public async Task<IReadOnlyList<ShowcaseAlternative>> FindAsync(ShowcaseAlternativesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var target = new ComuneTarget(
            string.IsNullOrWhiteSpace(query.ComuneIstat) ? null : query.ComuneIstat.Trim(),
            string.IsNullOrWhiteSpace(query.City) ? null : query.City.Trim());
        if (target.IsEmpty || string.IsNullOrWhiteSpace(query.Category))
            return [];

        // «Takes bookings» (decision VT-D7) includes the payments flag: while it is off no showcase takes any, and there is nothing to look for.
        if (!features.IsEnabled(FeatureFlags.SupplierOnlinePayments))
            return [];

        IReadOnlyList<RankedSupplier> ranked;
        try
        {
            ranked = await match.RankActiveAsync(target, query.Category, ServiceRequestUrgency.Normal, cancellationToken);
        }
        catch (DomainRuleException)
        {
            // A category that is not one of the platform's codes: no supplier covers it.
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The alternatives are a courtesy to a customer whose request lapsed: when they cannot be found the answer is that there are none to
            // suggest for now, and the error is logged. The page and the mail of the lapsed request do not fail because of them.
            logger.LogError(ex, "The suppliers to offer as alternatives could not be ranked");
            return [];
        }

        var found = new List<ShowcaseAlternative>(ShowcaseAlternativesLimits.Max);
        var looked = 0;
        foreach (var candidate in ranked)
        {
            var supplier = candidate.Profile;
            if (supplier.OrgId == query.ExcludedSupplierOrgId || string.IsNullOrWhiteSpace(supplier.ShowcaseSlug))
                continue;

            if (looked++ >= ShowcaseAlternativesLimits.MaxCandidates)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            var alternative = await TryDescribeAsync(supplier, query.Category, cancellationToken);
            if (alternative is null)
                continue;

            found.Add(alternative);
            if (found.Count == ShowcaseAlternativesLimits.Max)
                break;
        }

        return found;
    }

    /// <summary>The supplier as an alternative, or <c>null</c> when its showcase is not one a customer can book from now (or cannot be read).</summary>
    private async Task<ShowcaseAlternative?> TryDescribeAsync(SupplierProfile supplier, string category, CancellationToken cancellationToken)
    {
        try
        {
            var page = await showcase.GetPublishedPageAsync(supplier, cancellationToken);
            if (page is null || !ShowcaseAlternativeRules.Qualifies(page))
                return null;

            var extension = await showcase.GetExtensionAsync(supplier, cancellationToken);
            var cover = page.Content.Cover;
            var hasCover = !string.IsNullOrWhiteSpace(cover?.Url);

            return new ShowcaseAlternative(
                supplier.LegalName,
                supplier.ShowcaseSlug!,
                hasCover ? cover!.Url : null,
                hasCover && !string.IsNullOrWhiteSpace(cover!.Alt) ? cover.Alt : null,
                page.Verified,
                extension.MedianResponseMinutes,
                ShowcaseAlternativeRules.ServiceFor(extension.Services, category));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The showcase of supplier {SupplierOrgId} could not be read to offer it as an alternative; skipped", supplier.OrgId);
            return null;
        }
    }
}

using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Ranks the platform suppliers for a property (rules, not AI). The AI parts, an AI-written match reason and the
/// external discovery when no supplier covers the zone, run only with <see cref="FeatureFlags.AiSupplierDiscovery"/>
/// on (D11); the prompt carries only the category, the urgency and the supplier's open load: never the host's notes,
/// guest data or names (A8-15).
/// </summary>
public class SupplierMatchService(
    AppDbContext db,
    ISupplierService supplierService,
    IAiSupplierDiscoveryService aiDiscovery,
    IAiProvider aiProvider,
    IFeatureFlags featureFlags,
    ILogger<SupplierMatchService> logger,
    IAiResponseCache? aiCache = null,
    ISupplierReliabilityService? reliability = null,
    IOptions<SupplierRankingOptions>? rankingOptions = null) : ISupplierMatchService
{
    private static readonly ServiceRequestStatus[] OpenStatuses =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    private readonly SupplierRankingOptions _ranking = rankingOptions?.Value ?? new SupplierRankingOptions();

    public async Task<SupplierMatchResult> MatchAsync(
        Guid orgId,
        Guid propertyId,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default)
    {
        category = ServiceCategories.Require(category);

        var property = await db.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found for supplier match")
            {
                Code = "property_not_found",
                MessageKey = "PropertyNotFound",
            };

        if (property.OrgId != orgId)
            throw new UnauthorizedAccessException("Proprietà non appartiene all'organizzazione.");

        var aiEnabled = featureFlags.IsEnabled(FeatureFlags.AiSupplierDiscovery);
        var suppliers = await supplierService.GetActiveByComuneAsync(ComuneTarget.ForProperty(property), category, cancellationToken);
        if (suppliers.Count == 0)
        {
            if (!aiEnabled)
                return new SupplierMatchResult(null, [], [], false);

            var external = await aiDiscovery.SearchNearbyAsync(orgId, property.City, category, cancellationToken);
            return new SupplierMatchResult(null, [], external, external.Count > 0);
        }

        var scored = await ScoreAsync(suppliers, urgency, cancellationToken);

        var top = scored[0];
        var (reason, reasonByAi) = aiEnabled && ServiceCategories.IsKnown(category)
            ? await BuildMatchReasonAsync(orgId, top.Profile, category, urgency, top.Load, cancellationToken)
            : (BuildStaticReason(top.Profile, top.Load), false);
        var recommended = ToCandidate(top.Profile, top.Score, reason, reasonByAi);
        var alternatives = scored
            .Skip(1)
            .Take(3)
            .Select(x => ToCandidate(x.Profile, x.Score, BuildStaticReason(x.Profile, x.Load)))
            .ToList();

        logger.LogInformation(
            "Supplier match for property {PropertyId}: recommended {SupplierOrgId} (score {Score})",
            propertyId, recommended.OrgId, recommended.MatchScore);

        return new SupplierMatchResult(recommended, alternatives, [], false);
    }

    public async Task<IReadOnlyList<RankedSupplier>> RankActiveAsync(
        ComuneTarget target,
        string category,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default)
    {
        category = ServiceCategories.Require(category);
        var suppliers = await supplierService.GetActiveByComuneAsync(target, category, cancellationToken);
        if (suppliers.Count == 0)
            return [];

        var scored = await ScoreAsync(suppliers, urgency, cancellationToken);
        return [.. scored.Select(x => new RankedSupplier(x.Profile, x.Score, x.Load))];
    }

    public async Task<IReadOnlyDictionary<Guid, int>> ScoresAsync(
        IReadOnlyList<Casazen.Core.Entities.SupplierProfile> suppliers,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suppliers);
        if (suppliers.Count == 0)
            return new Dictionary<Guid, int>();

        var scored = await ScoreAsync(suppliers, urgency, cancellationToken);
        return scored.ToDictionary(x => x.Profile.OrgId, x => x.Score);
    }

    private async Task<List<(Casazen.Core.Entities.SupplierProfile Profile, int Score, int Load)>> ScoreAsync(
        IReadOnlyList<Casazen.Core.Entities.SupplierProfile> suppliers,
        ServiceRequestUrgency urgency,
        CancellationToken cancellationToken)
    {
        var supplierIds = suppliers.Select(s => s.OrgId).ToList();
        var openCounts = await db.ServiceRequests
            .AsNoTracking()
            .OnlyHoldingSupplierTime()
            .Where(sr => supplierIds.Contains(sr.SupplierOrgId) && OpenStatuses.Contains(sr.Status))
            .GroupBy(sr => sr.SupplierOrgId)
            .Select(g => new { SupplierOrgId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SupplierOrgId, x => x.Count, cancellationToken);

        IReadOnlyDictionary<Guid, int> reliabilityPoints = reliability is null || !_ranking.ReliabilityEnabled
            ? new Dictionary<Guid, int>()
            : await reliability.PointsAsync(supplierIds, cancellationToken);

        return suppliers
            .Select(sp =>
            {
                var load = openCounts.GetValueOrDefault(sp.OrgId, 0);
                var score = SupplierRanking.Score(
                    _ranking,
                    load,
                    urgency,
                    !string.IsNullOrWhiteSpace(sp.Bio),
                    reliabilityPoints.GetValueOrDefault(sp.OrgId, 0));
                return (Profile: sp, Score: score, Load: load);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Load)
            .ThenBy(x => x.Profile.LegalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Profile.OrgId)
            .ToList();
    }

    private async Task<(string Reason, bool GeneratedByAi)> BuildMatchReasonAsync(
        Guid orgId,
        Casazen.Core.Entities.SupplierProfile profile,
        string category,
        ServiceRequestUrgency urgency,
        int openLoad,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = BuildMatchReasonPrompt(category, urgency, openLoad);
            var cacheKey = $"supplier-match:{category}:{urgency}:{openLoad}";
            AiGenerationResult? ai = null;
            aiCache?.TryGet(orgId, cacheKey, out ai);
            if (ai is null)
            {
                ai = await aiProvider.GenerateAsync(prompt, AiModelTier.Economy, cacheKey, cancellationToken);
                if (ai.ProviderConfigured && !string.IsNullOrWhiteSpace(ai.Content))
                    aiCache?.Set(orgId, cacheKey, ai);
            }

            var text = ai.Content.Trim();
            if (text.Length > 160)
                text = text[..160];
            return ai.ProviderConfigured && !string.IsNullOrWhiteSpace(text)
                ? (text, true)
                : (BuildStaticReason(profile, openLoad), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI match reason failed for supplier {OrgId}", profile.OrgId);
            return (BuildStaticReason(profile, openLoad), false);
        }
    }

    public static string BuildMatchReasonPrompt(string category, ServiceRequestUrgency urgency, int openLoad) =>
        $"""
        Spiega perché un fornitore è adatto a una richiesta di servizio per un affitto breve.
        Categoria: {category}
        Urgenza: {urgency}
        Richieste aperte del fornitore: {openLoad}
        Rispondi in una frase italiana (max 120 caratteri).
        """;

    private static string BuildStaticReason(Casazen.Core.Entities.SupplierProfile profile, int openLoad) =>
        openLoad == 0
            ? $"{profile.LegalName} è attivo nella zona con carico di lavoro basso."
            : $"{profile.LegalName} copre la categoria richiesta con {openLoad} interventi aperti.";

    private static SupplierMatchCandidate ToCandidate(
        Casazen.Core.Entities.SupplierProfile profile,
        int score,
        string reason,
        bool reasonGeneratedByAi = false) =>
        new(
            profile.OrgId,
            profile.LegalName,
            profile.Phone,
            profile.Email,
            profile.Bio,
            score,
            reason,
            "platform",
            reasonGeneratedByAi);
}

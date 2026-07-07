using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class SupplierMatchService(
    AppDbContext db,
    ISupplierService supplierService,
    IGooglePlacesDiscoveryService googlePlaces,
    IAiProvider aiProvider,
    IPropertyAuthorizationService propertyAuthorization,
    ILogger<SupplierMatchService> logger) : ISupplierMatchService
{
    private static readonly ServiceRequestStatus[] OpenStatuses =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    public async Task<SupplierMatchResult> MatchAsync(
        Guid orgId,
        string userId,
        Guid propertyId,
        string category,
        ServiceRequestUrgency urgency,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var property = await db.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new InvalidOperationException("Proprietà non trovata.");

        if (property.OrgId != orgId)
            throw new UnauthorizedAccessException("Proprietà non appartiene all'organizzazione.");

        if (!await propertyAuthorization.CanAccessPropertyAsync(
                userId, propertyId, ["PropertyOwner", "Admin", "PropertyManager"]))
            throw new UnauthorizedAccessException("Accesso negato alla proprietà.");

        var suppliers = await supplierService.GetActiveByComune(property.City, category, cancellationToken);
        if (suppliers.Count == 0)
        {
            var external = await googlePlaces.SearchNearbyAsync(property.City, category, cancellationToken);
            return new SupplierMatchResult(null, [], external, external.Count > 0);
        }

        var supplierIds = suppliers.Select(s => s.OrgId).ToList();
        var openCounts = await db.ServiceRequests
            .AsNoTracking()
            .Where(sr => supplierIds.Contains(sr.SupplierOrgId) && OpenStatuses.Contains(sr.Status))
            .GroupBy(sr => sr.SupplierOrgId)
            .Select(g => new { SupplierOrgId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SupplierOrgId, x => x.Count, cancellationToken);

        var scored = suppliers
            .Select(sp =>
            {
                var load = openCounts.GetValueOrDefault(sp.OrgId, 0);
                var score = 70;
                score -= Math.Min(load * 8, 40);
                score += urgency switch
                {
                    ServiceRequestUrgency.Emergency => 15,
                    ServiceRequestUrgency.High => 8,
                    _ => 0,
                };
                if (!string.IsNullOrWhiteSpace(sp.Bio))
                    score += 5;
                return (Profile: sp, Score: Math.Clamp(score, 1, 100), Load: load);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Load)
            .ToList();

        var top = scored[0];
        var reason = await BuildMatchReasonAsync(top.Profile, category, urgency, top.Load, notes, cancellationToken);
        var recommended = ToCandidate(top.Profile, top.Score, reason);
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

    private async Task<string> BuildMatchReasonAsync(
        Casazen.Core.Entities.SupplierProfile profile,
        string category,
        ServiceRequestUrgency urgency,
        int openLoad,
        string? notes,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = $"""
                Seleziona un fornitore per affitti brevi.
                Fornitore: {profile.LegalName}
                Categoria: {category}
                Urgenza: {urgency}
                Richieste aperte: {openLoad}
                Note host: {notes ?? "nessuna"}
                Rispondi in una frase italiana (max 120 caratteri) spiegando perché è la scelta migliore.
                """;
            var cacheKey = $"supplier-match:{profile.OrgId}:{category}:{urgency}:{openLoad}";
            var ai = await aiProvider.GenerateAsync(prompt, AiModelTier.Economy, cacheKey, cancellationToken);
            var text = ai.Content.Trim();
            if (text.Length > 160)
                text = text[..160];
            return string.IsNullOrWhiteSpace(text) ? BuildStaticReason(profile, openLoad) : text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI match reason failed for supplier {OrgId}", profile.OrgId);
            return BuildStaticReason(profile, openLoad);
        }
    }

    private static string BuildStaticReason(Casazen.Core.Entities.SupplierProfile profile, int openLoad) =>
        openLoad == 0
            ? $"{profile.LegalName} è attivo nella zona con carico di lavoro basso."
            : $"{profile.LegalName} copre la categoria richiesta con {openLoad} interventi aperti.";

    private static SupplierMatchCandidate ToCandidate(
        Casazen.Core.Entities.SupplierProfile profile,
        int score,
        string reason) =>
        new(
            profile.OrgId,
            profile.LegalName,
            profile.Phone,
            profile.Email,
            profile.Bio,
            score,
            reason,
            "platform");
}

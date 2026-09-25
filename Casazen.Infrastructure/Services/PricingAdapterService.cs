using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Seasonal price suggestions (D4, PC-15, A2-14 / A2-34): the property's real nightly rate times the host's rule of the
/// day (<see cref="SeasonalPriceCalculator"/>), stored as one row per stay date and regenerated in place.
/// </summary>
public class PricingAdapterService(
    AppDbContext db,
    IPricingAdapterConfigRepository configRepository,
    ILogger<PricingAdapterService> logger,
    TimeProvider timeProvider) : IPricingAdapterService
{
    public async Task<PricingAdapterConfig?> GetConfigAsync(Guid propertyId)
    {
        return await configRepository.GetByPropertyIdAsync(propertyId);
    }

    public async Task<PricingAdapterConfig> SaveConfigAsync(PricingAdapterConfig config)
    {
        config.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (config.Id == Guid.Empty)
        {
            config.Id = Guid.NewGuid();
            return await configRepository.AddAsync(config);
        }

        await configRepository.UpdateAsync(config);
        return config;
    }

    public async Task DisableConfigAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SeasonalPriceSuggestions, propertyId.ToString("N")));

        var config = await db.PricingAdapterConfigs.FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (config is null)
            return;

        config.IsEnabled = false;
        config.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        db.SeasonalPriceSuggestions.RemoveRange(
            await db.SeasonalPriceSuggestions.Where(s => s.PropertyId == propertyId).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Disabled seasonal price suggestions for property {PropertyId}", propertyId);
    }

    public async Task<SeasonalSuggestionRunResult> RegenerateSuggestionsAsync(
        Guid propertyId,
        bool onlyIfDue,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RegenerateLockedAsync(propertyId, onlyIfDue, cancellationToken);
        }
        catch
        {
            // The job reuses the context for the next property: nothing of a failed run may be saved with it.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<SeasonalSuggestionRunResult> RegenerateLockedAsync(
        Guid propertyId,
        bool onlyIfDue,
        CancellationToken cancellationToken)
    {
        // One computation per property at a time (job, manual recalculation, save): the upsert by date never races.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.SeasonalPriceSuggestions, propertyId.ToString("N")));

        var config = await db.PricingAdapterConfigs.FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);
        if (config is not { IsEnabled: true })
            return new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.NotEnabled, 0, null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = timeProvider.TodayInRomeAsDateOnly();
        if (onlyIfDue && !SeasonalSuggestionSchedule.IsDue(config.AdaptationFrequency, config.LastAdaptedAt, today))
            return new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.NotDue, 0, null);

        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.NightlyRate, p.OrgId })
            .SingleAsync(cancellationToken);
        var existing = await db.SeasonalPriceSuggestions
            .Where(s => s.PropertyId == propertyId)
            .ToListAsync(cancellationToken);

        if (property.NightlyRate <= 0)
        {
            // Without a real base there is nothing to suggest: no invented base price. The run stays due.
            db.SeasonalPriceSuggestions.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            logger.LogWarning(
                "Seasonal price suggestions not computed for property {PropertyId}: no nightly rate", propertyId);
            return new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.BasePriceMissing, 0, null);
        }

        var window = SeasonalPriceCalculator.Window(today, property.NightlyRate, SeasonalPricingRules.From(config));
        var byDate = existing.ToDictionary(s => s.StayDate);
        foreach (var day in window)
        {
            if (!byDate.Remove(day.Date, out var row))
            {
                row = new SeasonalPriceSuggestion { PropertyId = propertyId, OrgId = property.OrgId, StayDate = day.Date };
                db.SeasonalPriceSuggestions.Add(row);
            }

            row.BasePrice = day.BasePrice;
            row.SuggestedPrice = day.SuggestedPrice;
            row.Multiplier = day.Multiplier;
            row.Rule = day.Rule;
            row.Holiday = day.Holiday;
            row.ComputedAt = now;
        }

        // Dates left over are past or beyond the window: no history of old suggestions is kept.
        db.SeasonalPriceSuggestions.RemoveRange(byDate.Values);

        config.LastAdaptedAt = now;
        config.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Computed {Days} seasonal price suggestions for property {PropertyId}", window.Count, propertyId);
        return new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.Computed, window.Count, now);
    }

    public async Task<IReadOnlyList<SeasonalPriceSuggestion>> GetSuggestionsAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        return await db.SeasonalPriceSuggestions
            .AsNoTracking()
            .Where(s => s.PropertyId == propertyId)
            .OrderBy(s => s.StayDate)
            .ToListAsync(cancellationToken);
    }
}

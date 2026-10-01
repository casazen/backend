using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// No personal data is read or kept (<see cref="SeoEvent"/>): the caller's IP is only used by the rate limiter of the
/// endpoint, which keeps it in memory for the window and never passes it here.
/// </remarks>
public sealed class SeoEventService(
    AppDbContext db,
    IOptions<SeoEventOptions> options,
    ILogger<SeoEventService> logger,
    TimeProvider? timeProvider = null) : ISeoEventService
{
    /// <summary>Most rows the admin report returns.</summary>
    public const int MaxTopLimit = 50;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Wire names of the events.</summary>
    public static class WireNames
    {
        public const string CtaClick = "cta_click";
        public const string SignupStart = "signup_start";
    }

    public async Task RecordAsync(SeoEventInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var type = input.Event switch
        {
            WireNames.CtaClick => SeoEventType.CtaClick,
            WireNames.SignupStart => SeoEventType.SignupStart,
            _ => (SeoEventType?)null,
        };
        if (type is null)
            throw new DomainRuleException(ISeoEventService.UnknownEventCode, "SeoEventUnknown");

        var comune = SignupAttributionRules.ResolveComune(SignupAttributionRules.Normalize(input.Comune));
        if (comune is null)
            throw new DomainRuleException(ISeoEventService.UnknownComuneCode, SignupAttributionRules.UnknownComuneKey);

        db.SeoEvents.Add(new SeoEvent
        {
            Event = type.Value,
            ComuneCode = comune.Code,
            UtmSource = SignupAttributionRules.Normalize(input.UtmSource),
            UtmMedium = SignupAttributionRules.Normalize(input.UtmMedium),
            UtmCampaign = SignupAttributionRules.Normalize(input.UtmCampaign),
            ReferrerHost = SignupAttributionRules.Normalize(input.ReferrerHost),
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SeoTopComuniResult> GetTopComuniAsync(int days, int limit, CancellationToken cancellationToken = default)
    {
        var retention = options.Value.EffectiveRetentionDays;
        // Older events are deleted: a longer window would show less than it claims.
        days = Math.Clamp(days, 1, retention);
        limit = Math.Clamp(limit, 1, MaxTopLimit);

        var to = _clock.GetUtcNow().UtcDateTime;
        var from = to.AddDays(-days);

        var events = await db.SeoEvents.AsNoTracking()
            .Where(e => e.OccurredAt >= from && e.OccurredAt <= to)
            .GroupBy(e => e.ComuneCode)
            .Select(g => new
            {
                ComuneCode = g.Key,
                CtaClicks = g.Count(e => e.Event == SeoEventType.CtaClick),
                SignupStarts = g.Count(e => e.Event == SeoEventType.SignupStart),
            })
            .ToListAsync(cancellationToken);

        // Platform report of the AdminOnly endpoint: the signups of every org, counted, never listed.
        var signups = (await db.SignupAttributions.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.ComuneCode != null && a.RecordedAt >= from && a.RecordedAt <= to)
                .GroupBy(a => a.ComuneCode!)
                .Select(g => new { ComuneCode = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.ComuneCode, x => x.Count);

        var items = events
            .Where(e => e.CtaClicks > 0)
            .OrderByDescending(e => e.CtaClicks)
            .ThenByDescending(e => e.SignupStarts)
            .ThenBy(e => e.ComuneCode, StringComparer.Ordinal)
            .Take(limit)
            .Select(e => new SeoTopComune(
                e.ComuneCode,
                SignupAttributionRules.ResolveComune(e.ComuneCode)?.Name ?? e.ComuneCode,
                e.CtaClicks,
                e.SignupStarts,
                signups.GetValueOrDefault(e.ComuneCode)))
            .ToList();

        return new SeoTopComuniResult(days, from, to, retention, items);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-options.Value.EffectiveRetentionDays);
        var deleted = await db.SeoEvents.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
            logger.LogInformation("Deleted {Deleted} SEO funnel events older than {Cutoff:O}", deleted, cutoff);

        return deleted;
    }
}

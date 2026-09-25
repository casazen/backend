using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
public sealed class SignupAttributionService(
    AppDbContext db,
    ILogger<SignupAttributionService> logger) : ISignupAttributionService
{
    public async Task<bool> RecordAsync(
        string userId,
        SignupAttributionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(input);

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.OrgId, u.OnboardingCompletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        // The attribution belongs to the org created by the first onboarding: none before it.
        if (user?.OrgId is not Guid orgId || user.OnboardingCompletedAt is null)
        {
            throw new DomainRuleException(
                ISignupAttributionService.OnboardingRequiredCode,
                "SignupAttributionOnboardingRequired");
        }

        var comune = SignupAttributionRules.Normalize(input.Comune);
        var comuneInfo = comune is null ? null : SignupAttributionRules.ResolveComune(comune);
        if (comune is not null && comuneInfo is null)
        {
            // Refused, not dropped: the rule is "invalid values are rejected", the web app then forgets the values.
            throw new DomainRuleException(
                ISignupAttributionService.UnknownComuneCode,
                SignupAttributionRules.UnknownComuneKey);
        }

        // Fast path for the retries of the web app; the unique index on OrgId is what guarantees one row per org.
        if (await db.SignupAttributions.AnyAsync(a => a.OrgId == orgId, cancellationToken))
            return false;

        var attribution = new SignupAttribution
        {
            OrgId = orgId,
            UtmSource = SignupAttributionRules.Normalize(input.UtmSource),
            UtmMedium = SignupAttributionRules.Normalize(input.UtmMedium),
            UtmCampaign = SignupAttributionRules.Normalize(input.UtmCampaign),
            UtmTerm = SignupAttributionRules.Normalize(input.UtmTerm),
            UtmContent = SignupAttributionRules.Normalize(input.UtmContent),
            ComuneCode = comuneInfo?.Code,
            LandingPath = SignupAttributionRules.Normalize(input.LandingPath),
            ReferrerHost = SignupAttributionRules.Normalize(input.ReferrerHost),
            RecordedAt = DateTime.UtcNow,
        };
        db.SignupAttributions.Add(attribution);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request (two tabs finishing the onboarding) stored the first attribution: keep that one.
            db.Entry(attribution).State = EntityState.Detached;
            logger.LogInformation("Signup attribution of org {OrgId} already recorded by a parallel request", orgId);
            return false;
        }

        logger.LogInformation(
            "Signup attribution recorded for org {OrgId} (source {UtmSource}, comune {ComuneCode})",
            orgId,
            attribution.UtmSource,
            attribution.ComuneCode);
        return true;
    }

    public async Task<(IReadOnlyList<SignupAttributionRecord> Items, int TotalCount)> ListAsync(
        DateTime? recordedFromUtc,
        string? comuneCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Platform report of the AdminOnly endpoint: the attributions of every org, not only the caller's.
        var query = db.SignupAttributions.IgnoreQueryFilters().AsNoTracking();
        if (recordedFromUtc is DateTime from)
            query = query.Where(a => a.RecordedAt >= from);
        if (!string.IsNullOrWhiteSpace(comuneCode))
        {
            var code = comuneCode.Trim();
            query = query.Where(a => a.ComuneCode == code);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(a => a.RecordedAt)
            .ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(a => new SignupAttributionRecord(
                a.OrgId,
                a.RecordedAt,
                a.UtmSource,
                a.UtmMedium,
                a.UtmCampaign,
                a.UtmTerm,
                a.UtmContent,
                a.ComuneCode,
                a.ComuneCode is null ? null : ItalianComuneRegistry.GetByCode(a.ComuneCode)?.Name,
                a.LandingPath,
                a.ReferrerHost))
            .ToList();

        return (items, total);
    }
}

using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.OrgTeam;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed record DirectPortalDates(DateOnly? Alloggiati, DateOnly? Istat);

public sealed class DirectPortalRoutes(AppDbContext db, TimeProvider clock, IActivityLog activity, IPropertySenderStore senders)
    : IDirectAlloggiatiRoute, IRoss1000DirectRoute
{
    public async Task<DirectPortalDates> ReadAsync(Guid orgId, Guid propertyId, CancellationToken ct)
        => await db.PropertyComplianceSenders.AsNoTracking()
            .Where(s => s.OrgId == orgId && s.PropertyId == propertyId)
            .Select(s => new DirectPortalDates(s.DirectAlloggiatiFrom, s.DirectIstatFrom))
            .SingleOrDefaultAsync(ct) ?? new(null, null);

    async Task<DateOnly?> IDirectAlloggiatiRoute.EffectiveFromAsync(Guid orgId, Guid propertyId, CancellationToken ct)
        => (await ReadAsync(orgId, propertyId, ct)).Alloggiati;

    async Task<DateOnly?> IRoss1000DirectRoute.EffectiveFromAsync(Guid orgId, Guid propertyId, CancellationToken ct)
        => (await ReadAsync(orgId, propertyId, ct)).Istat;

    public async Task<DirectPortalDates> ActivateAsync(Guid orgId, Guid propertyId, bool police, string userId, Func<CancellationToken, Task> checkAndGrant, CancellationToken ct)
    {
        await using var gate = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(db,
            PostgresAdvisoryLocks.Scope.InstitutionalSenderCutover, propertyId.ToString("N"), ct);
        if (gate is null)
            throw new DomainConflictException("direct_sender_busy", "SenderChangedConcurrently");

        if (!police && !await db.PropertyIstatCredentials.AnyAsync(
                r => r.OrgId == orgId && r.PropertyId == propertyId && r.StructureCode != null, ct))
            throw new DomainConflictException("direct_credentials_changed", "SenderChangedConcurrently");

        var row = await db.PropertyComplianceSenders.SingleOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId, ct);
        if ((police ? row?.DirectAlloggiatiFrom : row?.DirectIstatFrom).HasValue)
            return new(row!.DirectAlloggiatiFrom, row.DirectIstatFrom);
        var previous = await senders.GetAsync(propertyId, ct);
        if (!previous.Applies || previous.OrgId != orgId)
            throw new DomainRuleException("sender_not_applicable", "SenderNotApplicable");
        await checkAndGrant(ct);
        var utcNow = clock.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new PropertyComplianceSender { OrgId = orgId, PropertyId = propertyId, CreatedAt = utcNow };
            db.PropertyComplianceSenders.Add(row);
        }

        row.AlloggiatiSender = row.IstatSender = ComplianceSenderType.Manual;
        if (police) row.DirectAlloggiatiFrom = clock.TodayInRomeAsDateOnly();
        else row.DirectIstatFrom = clock.TodayInRomeAsDateOnly();
        row.LastChangedAt = utcNow;
        row.LastChangedByUserId = userId;
        row.ActivationDeclaredAt = null;
        row.ActivationDeclaredByUserId = null;
        activity.Record(OrgActivity.Of(orgId, OrgActivityType.PropertySenderChanged, userId, propertyId.ToString(),
            (OrgActivityDetailKeys.FromSender, (police ? previous.AlloggiatiSender : previous.IstatSender).ToString()),
            (OrgActivityDetailKeys.ToSender, ComplianceSenderType.CasaZen.ToString())));
        await db.SaveChangesAsync(ct);
        return new(row.DirectAlloggiatiFrom, row.DirectIstatFrom);
    }
}

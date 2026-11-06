using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The reach of a caller on the properties an onboarding row is about (AM-03, E10/F01/US03): the one rule the panel steps and the «Do it for me»
/// requests share. A row of a property belongs to the caller when the property is one of its org and within its scope (<c>InScope</c>, the
/// database decides); a row of the whole org (no property) belongs only to a scope over the whole org. Nothing is «in scope» by default: a
/// property that is not found, is another org's or is outside the scope is the same answer.
/// </summary>
internal static class OnboardingReach
{
    /// <summary>The properties of the scope's org the scope reaches, as a query that stays in SQL (a sub-select of the list that uses it).</summary>
    public static IQueryable<Guid> PropertyIds(AppDbContext db, HostScope scope) =>
        db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);

    /// <summary>
    /// True when the caller reaches the row of <paramref name="propertyId"/>: the property is one of the org and within the scope; with no property,
    /// when the scope covers the whole org.
    /// </summary>
    public static async Task<bool> CanReachAsync(AppDbContext db, HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is not { } id)
            return scope.IsOrgWide;

        return await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == id)
            .InScope(scope)
            .AnyAsync(ct);
    }

    /// <summary>
    /// Refuses a property the caller does not reach with the 404 of the activation (the same answer as an id that does not exist) and a row of the
    /// whole org with the 403 <see cref="OnboardingPanelStepCodes.NeedsWholeOrg"/> when the scope does not cover the whole org.
    /// </summary>
    public static async Task EnsureAsync(AppDbContext db, HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is null)
        {
            if (!scope.IsOrgWide)
                throw new DomainForbiddenException(OnboardingPanelStepCodes.NeedsWholeOrg, "OnboardingNeedsWholeOrg");

            return;
        }

        if (!await CanReachAsync(db, scope, propertyId, ct))
            throw PropertyNotFound();
    }

    public static NotFoundException PropertyNotFound() =>
        new("Property not found for the onboarding step.")
        {
            Code = OnboardingActivationCodes.PropertyNotFound,
            MessageKey = "PropertyNotFound",
        };
}

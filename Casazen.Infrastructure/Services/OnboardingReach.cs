using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

internal static class OnboardingReach
{
    public static IQueryable<Guid> PropertyIds(AppDbContext db, HostScope scope) =>
        db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);

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

    public static async Task EnsureItalianComplianceAsync(AppDbContext db, HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is not { } id)
            return;

        var country = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == id).InScope(scope)
            .Select(p => p.Country).SingleOrDefaultAsync(ct);
        if (!Casazen.Core.TouristTax.TouristTaxComune.IsItalianCountry(country))
            throw new DomainRuleException(PropertySenderStore.NotApplicableCode, "SenderNotApplicable");
    }

    public static NotFoundException PropertyNotFound() =>
        new("Property not found for the onboarding step.")
        {
            Code = OnboardingActivationCodes.PropertyNotFound,
            MessageKey = "PropertyNotFound",
        };
}

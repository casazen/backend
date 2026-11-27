using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed record PropertyActivationServicesView(
    string[]? RequiredPortals, string[] PendingPortals, bool ComplianceAvailable, bool ComplianceApplies, string? PreparationStatus);

public sealed class PropertyActivationServices(AppDbContext db, IOnboardingAssistService assist, IHostEnvironment host)
{
    public async Task<PropertyActivationServicesView> GetAsync(HostScope scope, Guid propertyId, CancellationToken ct) =>
        await ViewAsync(await PropertyAsync(scope, propertyId, false, ct), ct);

    public async Task<PropertyActivationServicesView> SetPortalsAsync(HostScope scope, Guid propertyId, string[] portals, CancellationToken ct)
    {
        var property = await PropertyAsync(scope, propertyId, true, ct);
        if (portals.Length > 40 || portals.Any(c => c is null || c.Length is < 1 or > 18 || !c.All(char.IsAsciiDigit))
            || portals.Distinct(StringComparer.Ordinal).Count() != portals.Length)
            throw Invalid("activation_portals_invalid");
        var known = await db.MarketplacePortals.Where(p => p.Category == MarketplacePortalCategory.Ota && portals.Contains(p.PortalCode))
            .Select(p => p.PortalCode).CountAsync(ct);
        if (known != portals.Length)
            throw Invalid("activation_portals_invalid");
        property.RequiredActivationPortalsJson = JsonSerializer.Serialize(portals);
        await db.SaveChangesAsync(ct);
        return await ViewAsync(property, ct);
    }

    public async Task<AssistRequestView> RequestAsync(
        HostScope scope, string userId, Guid propertyId, string service, CancellationToken ct)
    {
        var property = await PropertyAsync(scope, propertyId, false, ct);
        var state = await ViewAsync(property, ct);
        if (!state.ComplianceAvailable || !state.ComplianceApplies)
            throw Invalid("activation_compliance_not_available");
        var context = service switch
        {
            "alloggiati" => OnboardingAssistContexts.PanelPolice,
            "istat" => OnboardingAssistContexts.PanelIstat,
            _ => throw Invalid("activation_compliance_not_available"),
        };
        return await assist.RequestAsync(scope, userId, new AssistRequestInput(OnboardingPartKey.Compliance, context, propertyId), ct);
    }

    private async Task<PropertyActivationServicesView> ViewAsync(Property property, CancellationToken ct)
    {
        var required = property.RequiredActivationPortalsJson is { } json ? JsonSerializer.Deserialize<string[]>(json) : null;
        var pending = Array.Empty<string>();
        if (required is { Length: > 0 })
        {
            var active = await db.MarketplaceActivations.AsNoTracking()
                .Where(a => a.OrgId == property.OrgId && a.PropertyId == property.Id && a.Kind == MarketplaceActivationKind.Channel
                    && a.Status == MarketplaceActivationStatus.Active && a.VerifiedAt != null)
                .Select(a => a.TargetCode).ToArrayAsync(ct);
            pending = required.Except(active, StringComparer.Ordinal).ToArray();
        }
        var environment = OctorateEnvironments.Of(host);
        var preparation = await db.OctoratePreparations.AsNoTracking()
            .Where(r => r.OrgId == property.OrgId && r.PropertyId == property.Id && r.Environment == environment)
            .Select(r => (OctoratePreparationStatus?)r.Status).SingleOrDefaultAsync(ct);
        return new PropertyActivationServicesView(required, pending, property.IsActive && property.PublishedOnSite && required is not null && pending.Length == 0,
            property.Country == "IT", preparation?.ToString());
    }

    private Task<Property> PropertyAsync(HostScope scope, Guid propertyId, bool track, CancellationToken ct)
    {
        var query = db.Properties.Where(p => p.Id == propertyId && p.OrgId == scope.OrgId && p.RentalMode == RentalMode.Short).InScope(scope);
        return FindAsync(track ? query : query.AsNoTracking(), ct);
    }

    private static async Task<Property> FindAsync(IQueryable<Property> query, CancellationToken ct) =>
        await query.SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Property not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

    private static DomainConflictException Invalid(string code) => new(code, "PropertyActivationServicesUnavailable");
}

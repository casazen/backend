using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestRegistrationRegimeResolver" />
public sealed class GuestRegistrationRegimeResolver(AppDbContext db, IPropertySwissPlaceResolver places, IFeatureFlags featureFlags)
    : IGuestRegistrationRegimeResolver
{
    public async Task<GuestRegistrationResolution> ResolveAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        // The tenant filter of the context: another org's property is not found.
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
        return await ResolveAsync(property, cancellationToken);
    }

    public async Task<GuestRegistrationResolution> ResolveAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (SwissPlace.IsSwissCountry(property.Country))
        {
            // A place that is not known (none derived, or several left open) is the unknown Swiss place, never a canton chosen by the resolver.
            var place = await places.ResolveAsync(property, cancellationToken);
            var regime = place is { IsKnown: true }
                ? GuestRegistrationRegimeCatalog.ForCanton(place.CantonCode)
                : GuestRegistrationRegimeCatalog.UnknownSwissPlace;

            // The setting of the host is read only while the module is on: off, the duty is the one of the regime, whatever the column holds.
            var hostChoice = featureFlags.IsEnabled(FeatureFlags.GuestRegistrationRegimes) ? property.GuestNotificationOverride : null;
            return new GuestRegistrationResolution(
                property.Id,
                regime,
                place,
                hostChoice,
                GuestRegistrationDutyRules.EffectiveDuty(regime, hostChoice),
                GuestRegistrationReadiness.MissingOf(property.Country, place, property.TicinoStructureId));
        }

        // Spaces and case are ignored for Italy as they are for Switzerland (the check of Switzerland above trims), so « it » is never «another country».
        if (TouristTaxComune.IsItalianCountry(property.Country?.Trim()))
        {
            // The existing Alloggiati flows: no place, no setting of the host (the Italian duty is not the host's to waive), nothing to lack here.
            var italy = GuestRegistrationRegimeCatalog.Italian;
            return new GuestRegistrationResolution(property.Id, italy, null, null, GuestRegistrationDutyRules.DefaultDuty(italy), []);
        }

        // Neither Italy nor Switzerland: the catalog has nothing to say, and nothing is invented.
        return new GuestRegistrationResolution(property.Id, null, null, null, GuestRegistrationEffectiveDuty.Ask, []);
    }
}

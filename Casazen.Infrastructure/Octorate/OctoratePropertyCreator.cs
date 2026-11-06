using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctoratePropertyCreator"/> (E02/F03/US01/T06, section 9.1, PO-19, D6, D11, ADR-023, ADR-027-R8): creates on Octorate a property a
/// person declared never published, and makes the new accommodation a Confirmed mapping of it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only for new inventory.</b> The guards run before any call to Octorate and in this order: the property is the org's and within the reach
/// of the person; its stored scenario is <c>newInventory</c> (<see cref="IInventoryScenarioGuard"/>: <c>onOctorate</c>, <c>onOtas</c> and no declaration
/// at all are refused, because a missing mapping is not proof that the inventory is new); it has no confirmed mapping; it sits in no proposed or
/// ambiguous mapping row (the accommodation may already exist on Octorate and has to be confirmed or rejected first, so the import never meets a
/// duplicate). The org needs no connection yet: a new host has none, and the creation returns the token. An org that already has the connection reads
/// its listings again first (<see cref="IChannelMappingService.RefreshAsync"/>): an accommodation an earlier creation made and lost track of is then
/// proposed for the property and blocks a second creation, and a refresh that fails stops the creation.</item>
/// <item><b>The refresh token comes once.</b> Octorate answers the creation with the refresh token and no access token: it is saved through
/// <see cref="IOctorateTokenService.SaveTokensAsync"/> first, before the mapping can fail, and never logged. A new accommodation is created with
/// the partner token, since it does not exist yet to have a token of its own.</item>
/// <item><b>Payload.</b> From CasaZen's data: the address, the country, the position (a property without one is refused on the field
/// <c>location</c>), the currency of the stays, the contact of the organization and the person who publishes as the referent. <c>welcome_user</c> is
/// false and the credentials Octorate would send go to the CasaZen alternative address <c>octorate-{orgId}@{Octorate:OwnerEmailDomain}</c> (PO-19):
/// the owner gets nothing from Octorate, and the real address of the organization is never used for it. An agency's existing network is named so the
/// same refresh token covers the new property. The payload is personal data: never logged.</item>
/// <item><b>Errors.</b> Octorate's field errors come back as 422 <c>octorate_property_invalid</c> with the fields, so the wizard shows them on the
/// right step; an unknown network is 409 <c>octorate_network_unknown</c>. Octorate has no idempotency key: a creation whose answer was lost is not
/// repeated here (the caller does not retry it by itself), because the token would not be recoverable.</item>
/// </list>
/// </remarks>
public sealed class OctoratePropertyCreator(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateTokenService tokens,
    IChannelMappingService mappings,
    IInventoryScenarioGuard scenarios,
    IHostScopeResolver hostScopeResolver,
    IOptions<OctorateOptions> options,
    IHostEnvironment hostEnvironment,
    ILogger<OctoratePropertyCreator> logger) : IOctoratePropertyCreator
{
    private static readonly IReadOnlySet<string> NoTokenRoles = new HashSet<string>(StringComparer.Ordinal);

    private static readonly EventId AuditEvent = new(52_251, "OctoratePropertyCreated");

    public async Task<string> CreateAsync(Guid propertyId, string userId, CancellationToken ct, IReadOnlySet<string>? tokenRoles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // The property is read through the tenant filter: in a request only the caller's org has it, in a job (no tenant) the reach below decides.
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, ct);
        if (property is null
            || !await hostScopeResolver.CanReachPropertyAsync(userId, tokenRoles ?? NoTokenRoles, property.OrgId, property.Id, property.OwnerId, ct))
        {
            throw PropertyNotFound();
        }

        var orgId = property.OrgId;

        // Only new inventory is ever created (D6): the declaration of a person, not the absence of a mapping.
        await scenarios.EnsureNewInventoryAsync(orgId, propertyId, ct);

        if (await db.OctorateListingMappings
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .AnyAsync(m => m.OrgId == orgId && m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed, ct))
        {
            throw new DomainConflictException("octorate_property_exists", "OctoratePropertyExists");
        }

        // The org already has the connection: an accommodation an earlier creation made and then lost track of (Octorate answered, the refresh or the
        // confirmation after it did not work) is read again now and proposed for this property, so the block below stops a second creation of it. A
        // refresh that fails stops the creation too: nothing is created that could not be checked first.
        var connected = await IsConnectedAsync(orgId, ct);
        if (connected)
            await mappings.RefreshAsync(orgId, ct);

        // It may already exist on Octorate: confirmed or rejected first, so the import never meets a duplicate.
        if (await mappings.IsWriteBlockedAsync(propertyId, ct))
            throw new DomainConflictException("octorate_mapping_unconfirmed", "OctorateMappingUnconfirmed");

        var request = await BuildRequestAsync(property, userId, connected, ct);

        OctoratePropertyCreated created;
        try
        {
            using (orgScope.Use(orgId))
                created = await client.CreatePropertyAsync(request, ct);
        }
        catch (OctoratePropertyRejectedException ex)
        {
            throw PropertyInvalid(ex.Fields);
        }

        // The refresh token arrives once: saved before anything else can fail (it creates the connection of the org when there is none).
        await tokens.SaveTokensAsync(orgId, new OctorateTokenResponse(null, created.RefreshToken, null), ct);

        // The new accommodation is read back with the connection just made and becomes a Confirmed mapping of the property. The room of the unit is
        // created by the publication, which then sets it.
        await mappings.RefreshAsync(orgId, ct);
        await mappings.ConfirmAsync(orgId, created.AccommodationId, propertyId, null, userId, ct, tokenRoles);

        logger.LogInformation(
            AuditEvent,
            "Octorate property created: property {PropertyId} of org {OrgId} is accommodation {AccommodationId}, by user {UserId}",
            propertyId, orgId, created.AccommodationId, userId);
        return created.AccommodationId;
    }

    /// <summary>
    /// The data of the new accommodation, checked before the call: the country, the position, the address and the contact of the organization.
    /// Every field that is missing is named, so the wizard shows them all at once on their steps.
    /// </summary>
    private async Task<OctoratePropertyCreate> BuildRequestAsync(Property property, string userId, bool connected, CancellationToken ct)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == property.OrgId, ct);
        var person = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

        var invalid = new List<OctorateFieldError>();
        void Require(bool ok, string field, string message)
        {
            if (!ok)
                invalid.Add(new OctorateFieldError(field, message));
        }

        var country = property.Country?.Trim().ToUpperInvariant();
        var name = property.Name?.Trim();
        var email = org?.ContactEmail?.Trim();
        var phone = org?.ContactPhone?.Trim();
        var firstName = person?.FirstName?.Trim();
        var lastName = person?.LastName?.Trim();

        Require(!string.IsNullOrEmpty(name), "name", "required");
        Require(!string.IsNullOrWhiteSpace(property.Address), "address", "required");
        Require(!string.IsNullOrWhiteSpace(property.City), "city", "required");
        Require(!string.IsNullOrWhiteSpace(property.PostalCode), "zipCode", "required");
        Require(country is { Length: 2 } && country.All(char.IsAsciiLetterUpper), "country", "required");
        Require(
            property is { Latitude: >= -90m and <= 90m, Longitude: >= -180m and <= 180m } && !(property.Latitude == 0m && property.Longitude == 0m),
            "location",
            "required");
        Require(!string.IsNullOrEmpty(email) && new EmailAddressAttribute().IsValid(email), "contact.email", "required");
        Require(!string.IsNullOrEmpty(phone), "contact.phone", "required");
        Require(!string.IsNullOrEmpty(firstName), "contact.firstName", "required");
        Require(!string.IsNullOrEmpty(lastName), "contact.lastName", "required");
        if (invalid.Count > 0)
            throw PropertyInvalid(invalid);

        // PO-19: the credentials of the user Octorate creates go to this address only. No domain, no creation: the real address is never a fallback.
        var domain = options.Value.OwnerEmailDomain?.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(domain) || domain.Contains('@', StringComparison.Ordinal) || domain.Contains(' ', StringComparison.Ordinal))
            throw new DomainConflictException("octorate_owner_email_not_configured", "OctorateOwnerEmailNotConfigured");

        return new OctoratePropertyCreate
        {
            Name = name!,
            Address = property.Address.Trim(),
            City = property.City.Trim(),
            ZipCode = property.PostalCode.Trim(),
            Country = country!,
            Latitude = property.Latitude,
            Longitude = property.Longitude,
            Currency = StayCurrency.ForProperty(property),
            TimeZone = string.IsNullOrWhiteSpace(property.Timezone) ? null : property.Timezone.Trim(),
            ContactEmail = email!,
            ContactPhone = phone!,
            ContactFirstName = firstName!,
            ContactLastName = lastName!,
            AlternativeEmail = $"octorate-{property.OrgId:N}@{domain}",
            NetworkName = await NetworkOfAsync(property.OrgId, connected, ct),
            CheckinStart = property.CheckInFrom?.Hour,
            CheckinEnd = property.CheckInTo?.Hour,
            Checkout = property.CheckOutBy?.Hour,
        };
    }

    /// <summary>
    /// The network of the org's connection when it has one (an agency: the same refresh token then covers the new property); <c>null</c> for a new
    /// host with no connection. The network must exist, otherwise Octorate answers 412.
    /// </summary>
    private async Task<string?> NetworkOfAsync(Guid orgId, bool connected, CancellationToken ct)
    {
        if (!connected)
            return null;

        using (orgScope.Use(orgId))
        {
            var listings = await client.GetPropertiesAsync(ct);
            return listings.Select(listing => listing.NetworkId).FirstOrDefault(network => !string.IsNullOrWhiteSpace(network));
        }
    }

    /// <summary>Whether the org has a usable connection to Octorate in this environment (a new host has none, and the creation returns the token).</summary>
    private Task<bool> IsConnectedAsync(Guid orgId, CancellationToken ct)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        return db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected, ct);
    }

    private static DomainRuleException PropertyInvalid(IReadOnlyList<OctorateFieldError> fields) =>
        new("octorate_property_invalid", "OctoratePropertyInvalid")
        {
            Extensions = new Dictionary<string, object?>
            {
                ["fields"] = fields.Select(f => new { machineName = f.Field, message = f.Message }).ToList(),
            },
        };

    private static NotFoundException PropertyNotFound() =>
        new("Property not found for the Octorate creation.") { Code = "octorate_property_not_found", MessageKey = "OctoratePropertyNotFound" };
}

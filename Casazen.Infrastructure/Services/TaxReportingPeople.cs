using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The recipients and the properties the Certificazione Unica and the communication of the short lets name (E18/F01/US02, NF-117): the owners of the registry with the fiscal profile in
/// force on the day of the last payment, and the properties as the registry has them. Read once for a whole year (every query names the org, the deleted properties stay reachable:
/// the fiscal history of a stay never disappears with its property), then looked up in memory. Nothing here is written, and the personal data it reads (fiscal code, address)
/// is never logged.
/// </summary>
internal sealed class TaxReportingPeople
{
    private readonly Dictionary<Guid, PropertyOwner> _owners;
    private readonly ILookup<Guid, OwnerFiscalProfile> _profiles;
    private readonly Dictionary<Guid, CuProperty> _properties;

    private TaxReportingPeople(
        Dictionary<Guid, PropertyOwner> owners,
        ILookup<Guid, OwnerFiscalProfile> profiles,
        Dictionary<Guid, CuProperty> properties)
    {
        _owners = owners;
        _profiles = profiles;
        _properties = properties;
    }

    public static async Task<TaxReportingPeople> ReadAsync(
        AppDbContext db,
        Guid orgId,
        IReadOnlyCollection<Guid> ownerIds,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ownerIds);
        ArgumentNullException.ThrowIfNull(propertyIds);

        var owners = new Dictionary<Guid, PropertyOwner>();
        var profiles = new List<OwnerFiscalProfile>();
        if (ownerIds.Count > 0)
        {
            owners = await db.PropertyOwners.AsNoTracking()
                .Where(o => o.OrgId == orgId && ownerIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, cancellationToken);
            profiles = await db.OwnerFiscalProfiles.AsNoTracking()
                .Where(p => p.OrgId == orgId && ownerIds.Contains(p.PropertyOwnerId))
                .ToListAsync(cancellationToken);
        }

        var properties = new Dictionary<Guid, CuProperty>();
        if (propertyIds.Count > 0)
        {
            properties = await db.Properties.AsNoTracking()
                .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .Select(p => new CuProperty
                {
                    Id = p.Id,
                    Name = p.Name,
                    Address = p.Address,
                    Unit = p.Unit,
                    City = p.City,
                    PostalCode = p.PostalCode,
                    ComuneIstatCode = p.ComuneIstatCode,
                    Cin = p.CinCode,
                    CadastralSheet = p.CadastralSheet,
                    CadastralParcel = p.CadastralParcel,
                    CadastralSubaltern = p.CadastralSubaltern,
                    CadastralCategory = p.CadastralCategory,
                })
                .ToDictionaryAsync(p => p.Id, cancellationToken);
        }

        return new TaxReportingPeople(owners, profiles.ToLookup(p => p.PropertyOwnerId), properties);
    }

    /// <summary>The property as the registry has it, <c>null</c> for an id that is not one of the org's.</summary>
    public CuProperty? PropertyOf(Guid propertyId) => _properties.GetValueOrDefault(propertyId);

    /// <summary>The name the owner accounting gives an owner: the legal name, else first and last name; <c>null</c> for an owner that is not in the registry.</summary>
    public string? NameOf(Guid ownerId) =>
        _owners.TryGetValue(ownerId, out var owner)
            ? Clean(owner.LegalName) ?? Clean($"{owner.FirstName} {owner.LastName}")
            : null;

    /// <summary>
    /// Who the data of the owner makes out its Certificazione Unica to: the fiscal profile in force on <paramref name="day"/> (the latest that started on or before it and has not ended,
    /// else the latest there is), then the registry for what the profile has not got. What is missing is said, never guessed.
    /// </summary>
    public CuRecipient RecipientOf(Guid ownerId, DateOnly day)
    {
        if (!_owners.TryGetValue(ownerId, out var owner))
            return new CuRecipient { Missing = [Missing.Name, Missing.FiscalCode, Missing.Address] };

        var profile = ProfileOf(ownerId, day);
        var kind = profile?.EntityKind ?? owner.EntityKind;
        var natural = kind != OwnerEntityKind.LegalEntity;
        var name = Clean(profile?.LegalName) ?? NameOf(ownerId);
        var fiscalCode = Clean(profile?.FiscalCode) ?? Clean(owner.FiscalCode);
        var vatNumber = Clean(profile?.VatNumber) ?? Clean(owner.VatNumber);
        var street = Clean(owner.BillingStreet);
        var city = Clean(owner.BillingCity);
        var legalAddress = Clean(profile?.LegalAddress);

        var missing = new List<string>();
        if (name is null)
            missing.Add(Missing.Name);
        if (fiscalCode is null && (natural || vatNumber is null))
            missing.Add(Missing.FiscalCode);
        if (street is null && city is null && legalAddress is null)
            missing.Add(Missing.Address);

        return new CuRecipient
        {
            Name = name,
            FirstName = natural ? Clean(owner.FirstName) : null,
            LastName = natural ? Clean(owner.LastName) : null,
            EntityKind = kind?.ToString(),
            FiscalCode = fiscalCode,
            VatNumber = vatNumber,
            TaxResidenceCountry = Clean(profile?.TaxResidenceCountry) ?? Clean(owner.TaxResidenceCountry),
            Street = street,
            Civic = Clean(owner.BillingCivic),
            PostalCode = Clean(owner.BillingPostalCode),
            City = city,
            Province = Clean(owner.BillingProvince),
            Country = Clean(owner.BillingCountry),
            LegalAddress = legalAddress,
            Missing = missing,
        };
    }

    private OwnerFiscalProfile? ProfileOf(Guid ownerId, DateOnly day)
    {
        var profiles = _profiles[ownerId].ToList();
        return profiles.Where(p => p.ValidFrom <= day && (p.ValidTo is not { } last || last >= day)).MaxBy(p => p.ValidFrom)
            ?? profiles.MaxBy(p => p.ValidFrom);
    }

    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>The codes of <see cref="CuRecipient.Missing"/>.</summary>
    internal static class Missing
    {
        public const string Name = "name";
        public const string FiscalCode = "fiscal_code";
        public const string Address = "address";
    }
}

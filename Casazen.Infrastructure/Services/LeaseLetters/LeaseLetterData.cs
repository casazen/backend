using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services.LeaseLetters;

/// <summary>
/// The values of the placeholders of a letter (LR-OPS-1), computed from the lease: the parties by name and residence (read here, on the server:
/// the residence is encrypted at rest and never part of a DTO), the property, the dates. A datum the lease does not have is <c>null</c>, and the
/// letter prints «[da completare]». No fiscal code and no e-mail of a party is ever put in a letter, and nothing here is logged.
/// </summary>
internal static class LeaseLetterData
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static Dictionary<string, string?> Common(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var landlords = Parties(lease, PartyRole.Landlord);
        var tenants = Parties(lease, PartyRole.Tenant);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LeaseLetterPlaceholders.Landlords] = Names(landlords),
            [LeaseLetterPlaceholders.LandlordsDetail] = WithResidence(landlords),
            [LeaseLetterPlaceholders.Tenants] = Names(tenants),
            [LeaseLetterPlaceholders.TenantsDetail] = WithResidence(tenants),
            [LeaseLetterPlaceholders.PropertyAddress] = Address(lease.Property),
            [LeaseLetterPlaceholders.ContractType] = ContractTypeText(lease.ContractType),
            [LeaseLetterPlaceholders.StartDate] = Day(RomeCalendar.DateInRome(lease.StartDate)),
            [LeaseLetterPlaceholders.PeriodEnd] = Day(RomeCalendar.DateInRome(lease.CurrentTermEndDate)),
            [LeaseLetterPlaceholders.NeedReason] = OneLine(lease.TransitoryNeed),
        };
    }

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ',', ';', ':');
        return line.Length == 0 ? null : line;
    }

    public static string Day(DateOnly day) => day.ToString("d MMMM yyyy", Italian);

    public static string YearInterestLine(int year, decimal ratePercent, int days, int daysInYear, decimal interest) =>
        $"{year}: tasso {Percent(ratePercent)}% per {days} giorni su {daysInYear}: euro {Money(interest)}";

    public static string Money(decimal amount) => amount.ToString("N2", Italian);

    public static string Percent(decimal value) => value.ToString("0.####", Italian);

    private static List<Party> Parties(LeaseContract lease, PartyRole role) =>
        lease.Parties
            .Where(p => p.Role == role && p.AnonymizedAt is null)
            .OrderBy(p => p.Position)
            .ToList();

    private static string FullName(Party party) => $"{party.FirstName} {party.LastName}".Trim();

    private static string? Names(IReadOnlyList<Party> parties)
    {
        var names = parties.Select(FullName).Where(n => n.Length > 0).ToList();
        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} e {names[^1]}",
        };
    }

    private static string? WithResidence(IReadOnlyList<Party> parties)
    {
        var lines = parties
            .Where(p => FullName(p).Length > 0)
            .Select(p => $"{FullName(p)}, residente in {(string.IsNullOrWhiteSpace(p.Residence) ? LeaseLetterDocument.ToComplete : p.Residence.Trim())}")
            .ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static string? Address(Property? property)
    {
        if (property is null || string.IsNullOrWhiteSpace(property.Address))
            return null;

        var locality = $"{property.PostalCode} {property.City}".Trim();
        return locality.Length == 0 ? property.Address.Trim() : $"{property.Address.Trim()}, {locality}";
    }

    private static string ContractTypeText(LeaseContractType type) => type switch
    {
        LeaseContractType.Libero => "canone libero (4+4)",
        LeaseContractType.Concordato => "canone concordato (3+2)",
        LeaseContractType.Transitorio => "transitorio",
        _ => type.ToString(),
    };
}

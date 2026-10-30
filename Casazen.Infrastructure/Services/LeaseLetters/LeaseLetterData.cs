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

    /// <summary>The values every letter of a lease shares. The lease comes with its property and its parties.</summary>
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

    /// <summary>A text the landlord typed, as it goes into a sentence: on one line, without the stops that would meet the one the sentence has; <c>null</c> when there is none.</summary>
    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ',', ';', ':');
        return line.Length == 0 ? null : line;
    }

    /// <summary>A day as a person writes it in a letter: «31 ottobre 2030».</summary>
    public static string Day(DateOnly day) => day.ToString("d MMMM yyyy", Italian);

    /// <summary>The parties of a role in the order they were entered, without the ones whose data were anonymized.</summary>
    private static List<Party> Parties(LeaseContract lease, PartyRole role) =>
        lease.Parties
            .Where(p => p.Role == role && p.AnonymizedAt is null)
            .OrderBy(p => p.Position)
            .ToList();

    private static string FullName(Party party) => $"{party.FirstName} {party.LastName}".Trim();

    /// <summary>«Mario Rossi», «Mario Rossi e Anna Bianchi», «A, B e C»; <c>null</c> without parties.</summary>
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

    /// <summary>One line per party: «Mario Rossi, residente in Monza, via Roma 1»; the residence the lease does not have is «[da completare]».</summary>
    private static string? WithResidence(IReadOnlyList<Party> parties)
    {
        var lines = parties
            .Where(p => FullName(p).Length > 0)
            .Select(p => $"{FullName(p)}, residente in {(string.IsNullOrWhiteSpace(p.Residence) ? LeaseLetterDocument.ToComplete : p.Residence.Trim())}")
            .ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>«Via Roma 1, 20900 Monza».</summary>
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

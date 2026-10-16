using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services.LeaseDrafts;

/// <summary>
/// The two directions between what the landlord typed in the wizard (<see cref="LeaseDraftValues"/>) and the lease as every other
/// part of the system reads it (LR-12): the columns of <see cref="LeaseContract"/> and the rows of <see cref="Party"/>. A pure
/// function of its inputs, no database: the draft service reads the state, applies a step to it, and writes the projection.
/// </summary>
/// <remarks>
/// <para><b>The columns hold the valid values and only them.</b> A value that was not typed or is not valid is written as its
/// starting value (<see cref="LeaseDraftDefaults"/>), whatever the column held before, so the lease is always a function of the typed
/// state and never keeps what the landlord cleared.</para>
/// <para><b>A party is a row only when it is complete</b> (name, fiscal code, citizenship, residence, an e-mail that is an address
/// or none): the entries that are not stay in the typed state. The id of the entry is the id of the row.</para>
/// </remarks>
internal static class LeaseDraftProjection
{
    /// <summary>
    /// The values typed so far: <c>DraftData</c> when the draft keeps one (the property is always the column's), otherwise rebuilt
    /// from the columns and the parties, which are then the truth (a complete draft, a lease created in one call, a lease that
    /// existed before the guided draft).
    /// </summary>
    public static LeaseDraftValues ReadValues(LeaseContract lease)
    {
        if (LeaseDraftValues.FromJson(lease.DraftData) is { } typed)
            return typed with { PropertyId = lease.PropertyId };

        return FromColumns(lease);
    }

    public static LeaseDraftValues FromColumns(LeaseContract lease)
    {
        var parties = lease.Parties.OrderBy(p => p.Role).ThenBy(p => p.Position).ToList();
        return new LeaseDraftValues
        {
            PropertyId = lease.PropertyId,
            Landlords = parties.Where(p => p.Role == PartyRole.Landlord).Select(ToEntry).ToList(),
            Tenants = parties.Where(p => p.Role == PartyRole.Tenant).Select(ToEntry).ToList(),
            ContractType = lease.ContractType.ToString(),
            TaxRegime = lease.TaxRegime?.ToString(),
            MonthlyRent = lease.MonthlyRent > 0m ? lease.MonthlyRent : null,
            SecurityDeposit = lease.SecurityDeposit,
            Concordato = lease.ConcordatoAssessment is { } assessment ? ToEntry(assessment) : null,
            StartDate = LeaseDraftDefaults.IsPlaceholder(lease.StartDate)
                ? null
                : RomeCalendar.DateInRome(lease.StartDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static LeaseDraftParty ToEntry(Party party) => new()
    {
        Id = party.Id,
        FirstName = party.FirstName,
        LastName = party.LastName,
        FiscalCode = party.FiscalCode,
        Citizenship = party.Citizenship,
        Residence = party.Residence,
        ContactEmail = string.IsNullOrEmpty(party.ContactEmail) ? null : party.ContactEmail,
        DocumentsVerifiedOn = party.DocumentsVerifiedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static LeaseDraftConcordato ToEntry(LeaseConcordatoAssessment a) => new()
    {
        Sqm = a.Sqm,
        TypeAElementCount = a.TypeAElementCount,
        TypeBElementCount = a.TypeBElementCount,
        TypeCElementCount = a.TypeCElementCount,
        TypeDElementCount = a.TypeDElementCount,
        QualifyingTypeDElementCount = a.QualifyingTypeDElementCount,
        IsFurnished = a.IsFurnished,
        StoveHeating = a.StoveHeating,
        AirConditioning = a.AirConditioning,
        GarageSqm = a.GarageSqm,
        BalconySqm = a.BalconySqm,
        OtherAppurtenanceSqm = a.OtherAppurtenanceSqm,
        PrivateGreenSqm = a.PrivateGreenSqm,
        ZoneName = a.ZoneName,
        CadastralSheet = a.CadastralSheet,
    };

    /// <summary>
    /// The rows of the parties the typed values give: the complete entries, landlords then tenants, each role numbered from 0 in the
    /// order entered. The row keeps the id of its entry. The e-mail is stored as an empty string when there is none (the column is
    /// NOT NULL, as it always was), the citizenship upper case, the fiscal code normalized. The day the documents were checked is
    /// kept when it is a real day not after <paramref name="today"/> (otherwise the typed value stays in the draft, reported).
    /// </summary>
    public static List<Party> ToRows(LeaseDraftValues values, Guid leaseId, DateOnly today)
    {
        var rows = new List<Party>();
        foreach (var (role, entries) in new[] { (PartyRole.Landlord, values.Landlords), (PartyRole.Tenant, values.Tenants) })
        {
            var position = 0;
            foreach (var entry in entries.Where(LeaseDraftRules.IsPartyComplete))
            {
                var citizenship = EuMemberStates.NormalizeCode(entry.Citizenship);
                rows.Add(new Party
                {
                    Id = entry.Id,
                    LeaseContractId = leaseId,
                    Role = role,
                    Position = position++,
                    FirstName = LeaseDraftRules.Clean(entry.FirstName)!,
                    LastName = LeaseDraftRules.Clean(entry.LastName)!,
                    FiscalCode = ItalianFiscalCode.Normalize(entry.FiscalCode),
                    Citizenship = citizenship,
                    ContactEmail = LeaseDraftRules.Clean(entry.ContactEmail) ?? string.Empty,
                    Residence = LeaseDraftRules.Clean(entry.Residence),
                    DocumentsVerifiedOn = LeaseDraftRules.HasInvalidDocumentsDate(entry, today) ? null : LeaseDraftRules.ParseDate(entry.DocumentsVerifiedOn),
                    // Not an EU citizen (the 27 member states): the Questura communication applies (LT-07).
                    IsExtraEU = !EuMemberStates.IsEuCitizenship(citizenship),
                });
            }
        }

        return rows;
    }

    /// <summary>True when the rows already are the ones the values give (nothing to rewrite).</summary>
    public static bool SameRows(ICollection<Party> existing, ICollection<Party> desired)
    {
        if (existing.Count != desired.Count)
            return false;

        var byId = existing.ToDictionary(p => p.Id);
        return desired.All(d =>
            byId.TryGetValue(d.Id, out var e)
            && e.Role == d.Role
            && e.Position == d.Position
            && e.FirstName == d.FirstName
            && e.LastName == d.LastName
            && e.FiscalCode == d.FiscalCode
            && e.Citizenship == d.Citizenship
            && e.ContactEmail == d.ContactEmail
            && e.Residence == d.Residence
            && e.DocumentsVerifiedOn == d.DocumentsVerifiedOn
            && e.IsExtraEU == d.IsExtraEU);
    }

    /// <summary>
    /// Writes the valid values into the columns that cannot be empty and into the ones that can: the type and the regime (and the
    /// legacy combined regime they give), the rent, the deposit, the first period, and the range of a canone concordato when it was
    /// computed and the rent does not break it. Nothing that was not valid is written: it takes its starting value (but for the
    /// transitory type a lease already has, below). The end of the first period is always the one the type gives (48 or 36 months from
    /// the start): the wizard owns the term, so a draft made by the old form with another term gets the term of its type at its first save.
    /// </summary>
    public static void ApplyColumns(LeaseContract lease, LeaseDraftReading reading, ConcordatoAssessmentResult? concordato)
    {
        // A type the old form accepted and the wizard cannot offer (the transitory lease) stays on a lease that already has it until the
        // landlord chooses another: the first save of any step must not turn it into a libero of four years on its own. For every other
        // draft the type is not available only because the landlord has just typed it, and the column keeps its starting value.
        var keepsItsType = !reading.TypeAvailable && reading.Type is { } named && named == lease.ContractType;
        var type = reading.TypeAvailable ? reading.Type!.Value : keepsItsType ? lease.ContractType : LeaseDraftDefaults.PlaceholderType;
        var startDate = reading.Start is { } start ? LeaseDraftRules.ToStoredDate(start) : LeaseDraftDefaults.PlaceholderDate;
        var keepsItsEnd = keepsItsType && startDate == lease.StartDate;

        lease.SetContractTerms(type, reading.TaxRegime);
        lease.MonthlyRent = reading.Rent ?? LeaseDraftDefaults.PlaceholderRent;
        lease.SecurityDeposit = reading.Deposit;
        lease.StartDate = startDate;
        if (!keepsItsEnd)
            lease.EndDate = reading.End is { } end ? LeaseDraftRules.ToStoredDate(end) : LeaseDraftDefaults.PlaceholderDate;

        // A range is stored only as the complete creation of a lease stores it: for a concordato, with the rent inside it or
        // with data that are only indicative (A7-23). A rent outside a verified range is a block, never stored.
        lease.ConcordatoAssessment = type == LeaseContractType.Concordato
            && reading.Rent is not null
            && concordato is { Kind: ConcordatoAssessmentKind.Assessed, Assessment: { } assessment }
            && (concordato.RentWithinRange || concordato.Indicative)
                ? assessment
                : null;
    }
}

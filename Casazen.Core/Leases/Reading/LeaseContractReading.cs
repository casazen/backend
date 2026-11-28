using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases.Reading;

/// <summary>
/// A value read from the text of a contract (NL-DOCS), with how sure the reader is of it. Nothing is invented: a value the text does
/// not state is simply not there. <see cref="Derived"/> is <c>true</c> when the value is not written in the contract as such but worked
/// out from what is (a monthly rent from the annual one, a deposit from "three months", the end of the term from its length).
/// </summary>
/// <param name="Value">The value, in the type the draft uses.</param>
/// <param name="Confidence">0 to 100; <see cref="LeaseContractTextParser.AcceptedConfidence"/> and above is what the draft takes.</param>
/// <param name="Derived">Worked out from other values of the contract, not read as such.</param>
public sealed record ReadValue<T>(T Value, int Confidence, bool Derived = false)
{
    public bool IsAccepted => Confidence >= LeaseContractTextParser.AcceptedConfidence;
}

/// <summary>
/// A landlord or a tenant as the contract introduces them: identified by a fiscal code that passed the check character (a party
/// without one is not read: a name alone could be anybody). Contains personal data: it is only ever written to the encrypted draft.
/// </summary>
public sealed record ReadParty
{
    public required PartyRole Role { get; init; }

    public required int RoleConfidence { get; init; }

    public required string FiscalCode { get; init; }

    public bool FiscalCodeRepaired { get; init; }

    public int FiscalCodeConfidence { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public int NameConfidence { get; init; }

    public string? Residence { get; init; }

    public int ResidenceConfidence { get; init; }

    public string? Citizenship { get; init; }

    public int CitizenshipConfidence { get; init; }
}

public enum PropertyAddressMatch
{
    Match,

    Mismatch,
}

public sealed record LeaseContractParseContext(string? PropertyAddress = null, string? PropertyCity = null);

public sealed record LeaseContractReading
{
    public static readonly LeaseContractReading Empty = new();

    public IReadOnlyList<ReadParty> Parties { get; init; } = [];

    public ReadValue<LeaseContractType>? ContractType { get; init; }

    public ReadValue<LeaseTaxRegime>? TaxRegime { get; init; }

    public ReadValue<decimal>? MonthlyRent { get; init; }

    public ReadValue<decimal>? SecurityDeposit { get; init; }

    public ReadValue<DateOnly>? StartDate { get; init; }

    public ReadValue<DateOnly>? EndDate { get; init; }

    public ReadValue<int>? TermMonths { get; init; }

    public ReadValue<DateOnly>? StipulaDate { get; init; }

    public ReadValue<PropertyAddressMatch>? Property { get; init; }

    public bool IsEmpty =>
        Parties.Count == 0 && ContractType is null && TaxRegime is null && MonthlyRent is null && SecurityDeposit is null
        && StartDate is null && EndDate is null && StipulaDate is null;
}

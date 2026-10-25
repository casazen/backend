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
    /// <summary>The reader is sure enough for the value to go into a draft (to be checked by the person all the same).</summary>
    public bool IsAccepted => Confidence >= LeaseContractTextParser.AcceptedConfidence;
}

/// <summary>
/// A landlord or a tenant as the contract introduces them: identified by a fiscal code that passed the check character (a party
/// without one is not read: a name alone could be anybody). Contains personal data: it is only ever written to the encrypted draft.
/// </summary>
public sealed record ReadParty
{
    public required PartyRole Role { get; init; }

    /// <summary>How sure the reader is of the role (the contract says "Locatore" / "Conduttore" next to the party, or it does not).</summary>
    public required int RoleConfidence { get; init; }

    /// <summary>The fiscal code, upper case, valid (check character included).</summary>
    public required string FiscalCode { get; init; }

    /// <summary>The check character did not pass as printed; a character the OCR confuses with another had to be put right.</summary>
    public bool FiscalCodeRepaired { get; init; }

    public int FiscalCodeConfidence { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public int NameConfidence { get; init; }

    /// <summary>Municipality and address of the residence (or of the registered office), as written.</summary>
    public string? Residence { get; init; }

    public int ResidenceConfidence { get; init; }

    /// <summary>Two letters (ISO 3166-1 alpha-2); only "italiana" is read, any other nationality is left to be typed.</summary>
    public string? Citizenship { get; init; }

    public int CitizenshipConfidence { get; init; }
}

/// <summary>Whether the address of the property in the contract is the one of the property chosen for the draft.</summary>
public enum PropertyAddressMatch
{
    /// <summary>The contract names the property the draft is for.</summary>
    Match,

    /// <summary>The contract names another address: the person may have chosen the wrong property or the wrong contract.</summary>
    Mismatch,
}

/// <summary>What the property of the draft is, to compare with the one the contract names (nothing about the people).</summary>
public sealed record LeaseContractParseContext(string? PropertyAddress = null, string? PropertyCity = null);

/// <summary>
/// What <see cref="LeaseContractTextParser"/> read in a contract: the data the wizard of an imported contract asks in steps 2 to 4
/// and the day of the signature. Every member is optional; a value the text does not state, or states in a way the reader is not sure
/// of, is not there (and the wizard asks for it).
/// </summary>
public sealed record LeaseContractReading
{
    public static readonly LeaseContractReading Empty = new();

    public IReadOnlyList<ReadParty> Parties { get; init; } = [];

    public ReadValue<LeaseContractType>? ContractType { get; init; }

    public ReadValue<LeaseTaxRegime>? TaxRegime { get; init; }

    /// <summary>The monthly rent in euros; <see cref="ReadValue{T}.Derived"/> when the contract states the annual one.</summary>
    public ReadValue<decimal>? MonthlyRent { get; init; }

    /// <summary>The security deposit in euros; <see cref="ReadValue{T}.Derived"/> when the contract states it in months of rent.</summary>
    public ReadValue<decimal>? SecurityDeposit { get; init; }

    public ReadValue<DateOnly>? StartDate { get; init; }

    /// <summary>The last day of the first period, when the contract states it or states its length.</summary>
    public ReadValue<DateOnly>? EndDate { get; init; }

    /// <summary>The length of the first period in whole months, when the contract states it.</summary>
    public ReadValue<int>? TermMonths { get; init; }

    /// <summary>The day the contract was signed.</summary>
    public ReadValue<DateOnly>? StipulaDate { get; init; }

    /// <summary>Whether the property the contract names is the one chosen; absent when the text names none or the match is not clear.</summary>
    public ReadValue<PropertyAddressMatch>? Property { get; init; }

    /// <summary>The reader found nothing a draft can use.</summary>
    public bool IsEmpty =>
        Parties.Count == 0 && ContractType is null && TaxRegime is null && MonthlyRent is null && SecurityDeposit is null
        && StartDate is null && EndDate is null && StipulaDate is null;
}

using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-12: the values of a draft the tests start from. Every fiscal code is a valid one (the check character is computed), so a test
/// that wants an invalid value changes exactly that one.
/// </summary>
internal static class LeaseDraftTestData
{
    public const string LandlordFiscalCode = "RSSMRA80A01H501U";

    public const string TenantFiscalCode = "VRDGLI85B02F205A";

    /// <summary>Another valid code of a person (the check character is computed).</summary>
    public static readonly string SecondTenantFiscalCode = "BNCMRC90C15F205" + Casazen.Core.Regulatory.ItalianFiscalCode.CheckCharacter("BNCMRC90C15F205");

    /// <summary>An 11-digit code with its check digit: a company.</summary>
    public static readonly string CompanyFiscalCode = Enumerable.Range(0, 10)
        .Select(digit => "1234567890" + digit)
        .Single(Casazen.Core.Regulatory.ItalianFiscalCode.IsValidNumericCode);

    /// <summary>"Today" of the draft tests: 9 October 2026 in Rome.</summary>
    public static readonly DateOnly Today = new(2026, 10, 9);

    public static readonly Guid PropertyId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static LeaseDraftParty Landlord(string fiscalCode = LandlordFiscalCode) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Mario",
        LastName = "Rossi",
        FiscalCode = fiscalCode,
        Citizenship = "IT",
        Residence = "Bari, via Sparano 1",
        ContactEmail = "mario.rossi@example.com",
    };

    public static LeaseDraftParty Tenant(string fiscalCode = TenantFiscalCode, string citizenship = "IT") => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Giulia",
        LastName = "Verdi",
        FiscalCode = fiscalCode,
        Citizenship = citizenship,
        Residence = "Bari, via Argiro 7",
    };

    /// <summary>A canone libero with one landlord and one tenant: nothing is missing in the data steps.</summary>
    public static LeaseDraftValues Complete() => new()
    {
        PropertyId = PropertyId,
        Landlords = [Landlord()],
        Tenants = [Tenant()],
        ContractType = "Libero",
        TaxRegime = "CedolareSecca",
        MonthlyRent = 900m,
        SecurityDeposit = 1800m,
        StartDate = "2026-11-01",
    };

    public static LeaseDraftFacts GoodFacts { get; } = new() { PropertyExists = true, HasApe = true, HasCadastralData = true };

    public static LeaseDraftProgressInput Input(
        LeaseDraftValues? values = null,
        int wizardMaxStep = 5,
        LeaseOrigin origin = LeaseOrigin.Created,
        LeaseStatus status = LeaseStatus.Draft,
        DateTime? reviewConfirmedAt = null,
        DateTime? sentForSignatureAt = null,
        LeaseDraftFacts? facts = null,
        LeaseDraftConcordatoOutcome? concordato = null) => new()
    {
        Origin = origin,
        Status = status,
        WizardMaxStep = wizardMaxStep,
        ReviewConfirmedAt = reviewConfirmedAt,
        SentForSignatureAt = sentForSignatureAt,
        Values = values ?? Complete(),
        Facts = facts ?? GoodFacts,
        Concordato = concordato,
        Today = Today,
    };

    public static readonly DateTime Moment = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<string> NeedCodes(LeaseDraftFlow flow, int step) => flow[step].Needs.Select(n => n.Code).ToList();

    public static IReadOnlyList<string> AllNeedCodes(LeaseDraftFlow flow) =>
        flow.Steps.SelectMany(s => s.Needs).Select(n => n.Code).ToList();
}

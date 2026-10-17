using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>LR-12: how the values typed in the wizard are read — what is valid, what is not, and what a valid value means.</summary>
public class LeaseDraftRulesTests
{
    // ---- type, regime, dates -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Libero", LeaseContractType.Libero)]
    [InlineData("libero", LeaseContractType.Libero)]
    [InlineData(" Concordato ", LeaseContractType.Concordato)]
    [InlineData("Transitorio", LeaseContractType.Transitorio)]
    public void ParseContractType_NamesOfTheTypes_AreRead(string text, LeaseContractType expected)
    {
        Assert.Equal(expected, LeaseDraftRules.ParseContractType(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Studenti")] // there is no such type
    [InlineData("7")] // a number that names no type
    [InlineData("4+4")]
    public void ParseContractType_AnythingElse_IsNull(string? text)
    {
        Assert.Null(LeaseDraftRules.ParseContractType(text));
    }

    [Theory]
    [InlineData("CedolareSecca", LeaseTaxRegime.CedolareSecca)]
    [InlineData("ordinario", LeaseTaxRegime.Ordinario)]
    public void ParseTaxRegime_NamesOfTheRegimes_AreRead(string text, LeaseTaxRegime expected)
    {
        Assert.Equal(expected, LeaseDraftRules.ParseTaxRegime(text));
        Assert.Null(LeaseDraftRules.ParseTaxRegime("cedolare"));
        Assert.Null(LeaseDraftRules.ParseTaxRegime(null));
    }

    [Theory]
    [InlineData("2026-11-01", 2026, 11, 1)]
    [InlineData(" 2026-02-28 ", 2026, 2, 28)]
    [InlineData("2028-02-29", 2028, 2, 29)]
    [InlineData("1970-01-01", 1970, 1, 1)]
    [InlineData("2100-12-31", 2100, 12, 31)]
    public void ParseDate_ARealDayWrittenIsoInRange_IsRead(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), LeaseDraftRules.ParseDate(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01/11/2026")]
    [InlineData("2026-11")]
    [InlineData("2026-13-01")]
    [InlineData("2027-02-29")] // not a leap year
    [InlineData("2026-11-01T00:00:00Z")]
    [InlineData("1969-12-31")]
    [InlineData("2101-01-01")]
    public void ParseDate_AnythingElse_IsNull(string? text)
    {
        Assert.Null(LeaseDraftRules.ParseDate(text));
    }

    // ---- amounts -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(900, true)]
    [InlineData(1_000_000, true)]
    [InlineData(850.55, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1_000_000.01, false)]
    [InlineData(850.555, false)] // more than cents: the database would round it silently
    public void IsValidRent_AMoneyAmountAboveZero_IsValid(double amount, bool expected)
    {
        Assert.Equal(expected, LeaseDraftRules.IsValidRent((decimal)amount));
    }

    [Fact]
    public void IsValidRent_NothingTyped_IsNotValid()
    {
        Assert.False(LeaseDraftRules.IsValidRent(null));
        Assert.False(LeaseDraftRules.IsValidDeposit(null));
    }

    [Theory]
    [InlineData(0, true)] // a deposit of zero is a deposit that was thought about
    [InlineData(1800, true)]
    [InlineData(-0.01, false)]
    [InlineData(1_000_001, false)]
    public void IsValidDeposit_ZeroOrMore_IsValid(double amount, bool expected)
    {
        Assert.Equal(expected, LeaseDraftRules.IsValidDeposit((decimal)amount));
    }

    // ---- parties -------------------------------------------------------------------------------------------------

    [Fact]
    public void PartyProblems_ACompleteEntry_HasNone()
    {
        Assert.Empty(LeaseDraftRules.PartyProblems(LeaseDraftTestData.Landlord()));
        Assert.Empty(LeaseDraftRules.PartyProblems(LeaseDraftTestData.Tenant() with { ContactEmail = null }));
    }

    public static TheoryData<string, string> PartyCases => new()
    {
        { "first name missing", "firstName" },
        { "first name too long", "firstName" },
        { "last name missing", "lastName" },
        { "fiscal code missing", "fiscalCode" },
        { "fiscal code with a wrong check character", "fiscalCode" },
        { "fiscal code too short", "fiscalCode" },
        { "citizenship missing", "citizenship" },
        { "citizenship of three letters", "citizenship" },
        { "citizenship with a digit", "citizenship" },
        { "residence missing", "residence" },
        { "residence of 201 characters", "residence" },
        { "email that is not an address", "contactEmail" },
    };

    private static LeaseDraftParty WithProblem(string name) => name switch
    {
        "first name missing" => LeaseDraftTestData.Landlord() with { FirstName = "  " },
        "first name too long" => LeaseDraftTestData.Landlord() with { FirstName = new string('a', 101) },
        "last name missing" => LeaseDraftTestData.Landlord() with { LastName = null },
        "fiscal code missing" => LeaseDraftTestData.Landlord() with { FiscalCode = "" },
        "fiscal code with a wrong check character" => LeaseDraftTestData.Landlord() with { FiscalCode = "RSSMRA80A01H501V" },
        "fiscal code too short" => LeaseDraftTestData.Landlord() with { FiscalCode = "RSSMRA80A01H50" },
        "citizenship missing" => LeaseDraftTestData.Landlord() with { Citizenship = null },
        "citizenship of three letters" => LeaseDraftTestData.Landlord() with { Citizenship = "ITA" },
        "citizenship with a digit" => LeaseDraftTestData.Landlord() with { Citizenship = "I1" },
        "residence missing" => LeaseDraftTestData.Landlord() with { Residence = "" },
        "residence of 201 characters" => LeaseDraftTestData.Landlord() with { Residence = new string('x', 201) },
        "email that is not an address" => LeaseDraftTestData.Landlord() with { ContactEmail = "not-an-address" },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(PartyCases))]
    public void PartyProblems_AMissingOrWrongField_IsNamedAndOnlyThatOne(string name, string field)
    {
        var problems = LeaseDraftRules.PartyProblems(WithProblem(name));

        Assert.Equal([field], problems);
    }

    [Fact]
    public void PartyProblems_ResidenceOf200Characters_IsValid_TheLimitIsIncluded()
    {
        Assert.Empty(LeaseDraftRules.PartyProblems(LeaseDraftTestData.Landlord() with { Residence = new string('x', 200) }));
    }

    [Fact]
    public void PartyProblems_ACompanyFiscalCodeOf11Digits_IsValid_AndIsACompany()
    {
        var company = LeaseDraftTestData.Tenant(LeaseDraftTestData.CompanyFiscalCode);

        Assert.Empty(LeaseDraftRules.PartyProblems(company));
        Assert.True(LeaseDraftRules.IsCompany(company));
        Assert.False(LeaseDraftRules.IsCompany(LeaseDraftTestData.Tenant()));
        Assert.False(LeaseDraftRules.IsCompany(LeaseDraftTestData.Tenant("not a code")));
    }

    [Fact]
    public void PartyProblems_EverythingMissing_NamesEveryRequiredField_NotTheOptionalEmail()
    {
        var problems = LeaseDraftRules.PartyProblems(new LeaseDraftParty { Id = Guid.NewGuid() });

        Assert.Equal(["firstName", "lastName", "fiscalCode", "citizenship", "residence"], problems);
    }

    [Fact]
    public void IsBlank_AnEntryNobodyTypedIn_IsBlank_AnyTextMakesItAnEntry()
    {
        Assert.True(LeaseDraftRules.IsBlank(new LeaseDraftParty { Id = Guid.NewGuid(), FirstName = " ", LastName = "" }));
        Assert.False(LeaseDraftRules.IsBlank(new LeaseDraftParty { Id = Guid.NewGuid(), LastName = "R" }));
        Assert.False(LeaseDraftRules.IsBlank(new LeaseDraftParty { Id = Guid.NewGuid(), DocumentsVerifiedOn = "2026-10-01" }));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("2026-10-09", false)] // today
    [InlineData("2026-10-08", false)]
    [InlineData("2026-10-10", true)] // tomorrow: a document cannot have been checked tomorrow
    [InlineData("yesterday", true)]
    [InlineData("1969-01-01", true)]
    public void HasInvalidDocumentsDate_ADateThatIsThereAndNotAPastDay_IsInvalid(string? text, bool invalid)
    {
        var party = LeaseDraftTestData.Tenant() with { DocumentsVerifiedOn = text };

        Assert.Equal(invalid, LeaseDraftRules.HasInvalidDocumentsDate(party, LeaseDraftTestData.Today));
    }

    // ---- canone concordato ---------------------------------------------------------------------------------------

    [Fact]
    public void IsConcordatoValid_TheLimitsOfTheCompleteCreation_AreTheLimits()
    {
        var ok = new LeaseDraftConcordato { Sqm = 70, TypeAElementCount = 2, GarageSqm = 12, PrivateGreenSqm = 100_000, ZoneName = new string('z', 100) };

        Assert.True(LeaseDraftRules.IsConcordatoValid(ok));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { Sqm = 0 }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { Sqm = 10_001 }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { TypeBElementCount = -1 }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { TypeDElementCount = 101 }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { BalconySqm = 10_001 }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { PrivateGreenSqm = 100_001 }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { ZoneName = new string('z', 101) }));
        Assert.False(LeaseDraftRules.IsConcordatoValid(ok with { CadastralSheet = new string('1', 21) }));
    }

    [Fact]
    public void ToCharacteristics_CopiesEveryFieldAndTrimsTheText()
    {
        var typed = new LeaseDraftConcordato
        {
            Sqm = 80,
            TypeAElementCount = 1,
            TypeBElementCount = 2,
            TypeCElementCount = 3,
            TypeDElementCount = 4,
            QualifyingTypeDElementCount = 2,
            IsFurnished = true,
            StoveHeating = true,
            AirConditioning = true,
            GarageSqm = 10,
            BalconySqm = 5,
            OtherAppurtenanceSqm = 4,
            PrivateGreenSqm = 20,
            ZoneName = " Centro ",
            CadastralSheet = "  ",
        };

        var characteristics = LeaseDraftRules.ToCharacteristics(typed);

        Assert.Equal(80, characteristics.Sqm);
        Assert.Equal((1, 2, 3, 4, 2), (characteristics.TypeAElementCount, characteristics.TypeBElementCount, characteristics.TypeCElementCount, characteristics.TypeDElementCount, characteristics.QualifyingTypeDElementCount));
        Assert.True(characteristics.IsFurnished && characteristics.StoveHeating && characteristics.AirConditioning);
        Assert.Equal((10m, 5m, 4m, 20m), (characteristics.GarageSqm, characteristics.BalconySqm, characteristics.OtherAppurtenanceSqm, characteristics.PrivateGreenSqm));
        Assert.Equal("Centro", characteristics.ZoneName);
        Assert.Null(characteristics.CadastralSheet);
    }

    // ---- Read ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Read_ACompleteLibero_GivesTheFourYearsFirstPeriod()
    {
        var reading = LeaseDraftRules.Read(LeaseDraftTestData.Complete());

        Assert.Equal(LeaseContractType.Libero, reading.Type);
        Assert.True(reading.TypeAvailable);
        Assert.Equal(LeaseTaxRegime.CedolareSecca, reading.TaxRegime);
        Assert.Equal(900m, reading.Rent);
        Assert.Equal(1800m, reading.Deposit);
        Assert.Equal(new DateOnly(2026, 11, 1), reading.Start);
        Assert.Equal(new DateOnly(2030, 10, 31), reading.End);
        Assert.Equal(48, reading.Months);
    }

    [Fact]
    public void Read_AConcordato_GivesTheThreeYearsFirstPeriod()
    {
        var reading = LeaseDraftRules.Read(LeaseDraftTestData.Complete() with { ContractType = "Concordato" });

        Assert.Equal(new DateOnly(2029, 10, 31), reading.End);
        Assert.Equal(36, reading.Months);
    }

    [Fact]
    public void Read_ATransitoryType_IsNamedButNotAvailable_AndHasNoMonths()
    {
        var reading = LeaseDraftRules.Read(LeaseDraftTestData.Complete() with { ContractType = "Transitorio" });

        Assert.Equal(LeaseContractType.Transitorio, reading.Type);
        Assert.False(reading.TypeAvailable);
        Assert.Null(reading.Months);
    }

    [Fact]
    public void Read_AValidStartWithNoValidType_EndsAfterTheStartingType_SoTheTwoDatesAreAlwaysAFirstPeriodApart()
    {
        var reading = LeaseDraftRules.Read(LeaseDraftTestData.Complete() with { ContractType = null });

        Assert.Null(reading.Type);
        Assert.Equal(new DateOnly(2030, 10, 31), reading.End);
        Assert.Null(reading.Months);
    }

    [Fact]
    public void Read_ValuesThatAreNotValid_AreNull_NeverCorrected()
    {
        var reading = LeaseDraftRules.Read(new LeaseDraftValues
        {
            MonthlyRent = -5,
            SecurityDeposit = -1,
            StartDate = "soon",
            TaxRegime = "x",
        });

        Assert.Null(reading.Rent);
        Assert.Null(reading.Deposit);
        Assert.Null(reading.Start);
        Assert.Null(reading.End);
        Assert.Null(reading.TaxRegime);
    }

    [Fact]
    public void Read_NoDeposit_IsNoDeposit_NotAnInvalidOne()
    {
        Assert.Null(LeaseDraftRules.Read(LeaseDraftTestData.Complete() with { SecurityDeposit = null }).Deposit);
    }
}

using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;
using static Casazen.Tests.Unit.Leases.LeaseDraftTestData;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-12: the impression of the values of the five data steps. The autosave that repeats the confirmed values (with a space more, a lower
/// case fiscal code, the same number written another way, an entry deleted and typed again) leaves the review confirmed; a real change does not.
/// </summary>
public class LeaseDraftFingerprintTests
{
    [Fact]
    public void TheSameValues_GiveTheSameImpression_64LowerCaseHexCharacters()
    {
        var one = LeaseDraftFingerprint.Compute(Complete());
        var again = LeaseDraftFingerprint.Compute(Complete());

        Assert.Equal(one, again);
        Assert.Matches("^[0-9a-f]{64}$", one);
    }

    [Fact]
    public void TheIdsOfTheEntries_AreNotPartOfIt_ADeletedEntryTypedAgainIsNotAChange()
    {
        var original = Complete();
        var retyped = original with
        {
            Landlords = original.Landlords.Select(p => p with { Id = Guid.NewGuid() }).ToList(),
            Tenants = original.Tenants.Select(p => p with { Id = Guid.NewGuid() }).ToList(),
        };

        Assert.Equal(LeaseDraftFingerprint.Compute(original), LeaseDraftFingerprint.Compute(retyped));
    }

    [Fact]
    public void NoiseOfTheAutosave_IsNotAChange()
    {
        var original = Complete();
        var noisy = original with
        {
            ContractType = "libero",
            TaxRegime = " cedolaresecca ",
            MonthlyRent = 900.00m,
            SecurityDeposit = 1800.0m,
            StartDate = " 2026-11-01 ",
            Landlords = [original.Landlords[0] with
            {
                FirstName = "  Mario ",
                FiscalCode = " rssmra80a01h501u",
                Citizenship = "it",
                ContactEmail = "MARIO.ROSSI@Example.com ",
                Residence = " Bari, via Sparano 1 ",
            }],
        };

        Assert.Equal(LeaseDraftFingerprint.Compute(original), LeaseDraftFingerprint.Compute(noisy));
    }

    public static TheoryData<string> Changes => new()
    {
        "the property", "a first name", "a last name", "a fiscal code", "a citizenship", "a residence", "an email", "the day the documents were checked",
        "another tenant", "the order of the tenants", "the type", "the regime", "the rent", "the deposit", "no deposit", "the start date",
    };

    private static LeaseDraftValues Change(LeaseDraftValues v, string what) => what switch
    {
        "the property" => v with { PropertyId = Guid.NewGuid() },
        "a first name" => v with { Landlords = [v.Landlords[0] with { FirstName = "Maria" }] },
        "a last name" => v with { Tenants = [v.Tenants[0] with { LastName = "Bianchi" }] },
        "a fiscal code" => v with { Tenants = [v.Tenants[0] with { FiscalCode = SecondTenantFiscalCode }] },
        "a citizenship" => v with { Tenants = [v.Tenants[0] with { Citizenship = "US" }] },
        "a residence" => v with { Tenants = [v.Tenants[0] with { Residence = "Lecce" }] },
        "an email" => v with { Tenants = [v.Tenants[0] with { ContactEmail = "g@example.com" }] },
        "the day the documents were checked" => v with { Tenants = [v.Tenants[0] with { DocumentsVerifiedOn = "2026-10-01" }] },
        "another tenant" => v with { Tenants = [v.Tenants[0], Tenant(SecondTenantFiscalCode)] },
        "the order of the tenants" => v with { Tenants = [Tenant(SecondTenantFiscalCode), v.Tenants[0]] },
        "the type" => v with { ContractType = "Concordato" },
        "the regime" => v with { TaxRegime = "Ordinario" },
        "the rent" => v with { MonthlyRent = 901m },
        "the deposit" => v with { SecurityDeposit = 900m },
        "no deposit" => v with { SecurityDeposit = null },
        "the start date" => v with { StartDate = "2026-11-02" },
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void ARealChange_ChangesTheImpression(string what)
    {
        var original = Complete() with { Tenants = [Tenant()] };

        Assert.NotEqual(LeaseDraftFingerprint.Compute(original), LeaseDraftFingerprint.Compute(Change(original, what)));
    }

    private static LeaseDraftValues AConcordato(decimal sqm = 60m, decimal garage = 0m) =>
        Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = sqm, GarageSqm = garage } };

    [Fact]
    public void TheCharacteristicsOfTheUnit_AreAChangeOfAConcordato()
    {
        Assert.NotEqual(LeaseDraftFingerprint.Compute(AConcordato(60m)), LeaseDraftFingerprint.Compute(AConcordato(70m)));
        Assert.NotEqual(LeaseDraftFingerprint.Compute(AConcordato(garage: 0m)), LeaseDraftFingerprint.Compute(AConcordato(garage: 12m)));
    }

    [Fact]
    public void TheCharacteristicsOfTheUnit_AreNothingForAnyOtherType_TheirColumnsKeepNone()
    {
        // A form that goes on sending the characteristics after the landlord chose the libero must not withdraw the review at every save.
        var libero = Complete();
        var leftOver = libero with { Concordato = new LeaseDraftConcordato { Sqm = 70m } };

        Assert.Equal(LeaseDraftFingerprint.Compute(libero), LeaseDraftFingerprint.Compute(leftOver));
    }

    [Fact]
    public void TheAreas_AreKeptToTheCentsOfTheirColumns_SoWhatIsReadBackIsTheSameAsWhatWasTyped()
    {
        var typed = AConcordato(sqm: 70.004m, garage: 12.345m);
        var read = AConcordato(sqm: 70.00m, garage: 12.35m); // numeric(10,2): the database rounds half away from zero

        Assert.Equal(LeaseDraftFingerprint.Compute(typed), LeaseDraftFingerprint.Compute(read));
    }

    [Fact]
    public void AValueMovedFromOneFieldToTheNext_IsAChange_TheLengthOfEachIsPartOfIt()
    {
        var a = Complete() with { Landlords = [Landlord() with { FirstName = "Ab", LastName = "C" }] };
        var b = Complete() with { Landlords = [Landlord() with { FirstName = "A", LastName = "bC" }] };

        Assert.NotEqual(LeaseDraftFingerprint.Compute(a), LeaseDraftFingerprint.Compute(b));
    }

    [Fact]
    public void ABadValueIsPartOfIt_TheReviewCoversWhatWasTyped_NotWhatWasUnderstood()
    {
        var typed = Complete() with { StartDate = "soon" };
        var other = Complete() with { StartDate = "later" };

        Assert.NotEqual(LeaseDraftFingerprint.Compute(typed), LeaseDraftFingerprint.Compute(other));
        Assert.NotEqual(LeaseDraftFingerprint.Compute(Complete()), LeaseDraftFingerprint.Compute(typed));
    }

    [Fact]
    public void TheImpression_ContainsNothingOfTheValues()
    {
        var impression = LeaseDraftFingerprint.Compute(Complete());

        Assert.DoesNotContain("Mario", impression, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LandlordFiscalCode, impression, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstTermEnd_PassesTheTermRuleOfTheTypeForEveryStartDate_AFirstPeriodOfWholeMonths()
    {
        // Every day of five years, including the leap days and the 29th to 31st: the end the server computes is a first period of
        // exactly the months of the type, so the complete creation of the lease (EnsureTerm) would accept it.
        var day = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2028, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        for (; day <= last; day = day.AddDays(1))
        {
            foreach (var type in new[] { LeaseContractType.Libero, LeaseContractType.Concordato })
            {
                var months = LongRentRules.FirstTermMonths(type)!.Value;
                var end = LongRentRules.FirstTermEnd(day, months);

                LeaseContractTerms.EnsureTerm(type, day, end);
                Assert.Equal(new LeaseTerm(months, 0), LeaseTerm.Between(day, end));
            }
        }
    }
}

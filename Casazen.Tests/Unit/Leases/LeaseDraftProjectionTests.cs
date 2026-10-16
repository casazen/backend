using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Xunit;
using static Casazen.Tests.Unit.Leases.LeaseDraftTestData;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-12: the two directions between what the landlord typed and the lease every other reader sees. The columns hold the valid
/// values and only them (a value that was not typed or is not valid takes its starting value, whatever the column held), a party is a row
/// only when it is complete, and the lease of a complete draft gives back the same values.
/// </summary>
public class LeaseDraftProjectionTests
{
    private static LeaseContract NewLease() => new()
    {
        PropertyId = PropertyId,
        StartDate = LeaseDraftDefaults.PlaceholderDate,
        EndDate = LeaseDraftDefaults.PlaceholderDate,
        MonthlyRent = LeaseDraftDefaults.PlaceholderRent,
    };

    private static ConcordatoAssessmentResult Assessed(bool withinRange, bool indicative) => new(
        ConcordatoAssessmentKind.Assessed,
        Assessment: new LeaseConcordatoAssessment { Sqm = 70, RentWithinRange = withinRange, CanoneMinMensile = 400m, CanoneMaxMensile = 800m },
        Indicative: indicative);

    // ---- columns -------------------------------------------------------------------------------------------------

    [Fact]
    public void ApplyColumns_ValidValues_AreWrittenToTheColumns_AndTheLegacyRegimeFollows()
    {
        var lease = NewLease();

        LeaseDraftProjection.ApplyColumns(lease, LeaseDraftRules.Read(Complete()), concordato: null);

        Assert.Equal(LeaseContractType.Libero, lease.ContractType);
        Assert.Equal(LeaseTaxRegime.CedolareSecca, lease.TaxRegime);
        Assert.Equal(FiscalRegime.CedolareSecca, lease.FiscalRegime);
        Assert.Equal(900m, lease.MonthlyRent);
        Assert.Equal(1800m, lease.SecurityDeposit);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), lease.StartDate);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.Equal(DateTimeKind.Utc, lease.StartDate.Kind);
        Assert.Null(lease.ConcordatoAssessment);
    }

    [Fact]
    public void ApplyColumns_NothingValid_TakesTheStartingValues_EvenWhereTheColumnsHeldARealOne()
    {
        var lease = NewLease();
        LeaseDraftProjection.ApplyColumns(lease, LeaseDraftRules.Read(Complete()), null);

        // The landlord clears the rent, the start and the deposit, and types a type that cannot be created.
        var cleared = Complete() with { MonthlyRent = null, SecurityDeposit = null, StartDate = "", ContractType = "Transitorio", TaxRegime = null };
        LeaseDraftProjection.ApplyColumns(lease, LeaseDraftRules.Read(cleared), null);

        Assert.Equal(LeaseDraftDefaults.PlaceholderType, lease.ContractType);
        Assert.Null(lease.TaxRegime);
        Assert.Equal(LeaseDraftDefaults.PlaceholderRent, lease.MonthlyRent);
        Assert.Null(lease.SecurityDeposit);
        Assert.Equal(LeaseDraftDefaults.PlaceholderDate, lease.StartDate);
        Assert.Equal(LeaseDraftDefaults.PlaceholderDate, lease.EndDate);
        Assert.True(LeaseDraftDefaults.IsPlaceholder(lease.StartDate));
    }

    [Fact]
    public void TheStartingDates_AreFarInTheFuture_SoNoReaderActsOnThem()
    {
        // The RLI deadline starts from a start date that is not in the future; the agenda lists what falls in 90 days; the
        // retention of the parties counts from an end date long past; a fiscal year overlaps the dates of a lease.
        var today = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(LeaseDraftDefaults.PlaceholderDate > today.AddYears(900));
        Assert.Null(Casazen.Core.Regulatory.RliRegistrationDeadline.Resolve(LeaseStatus.Draft, null, LeaseDraftDefaults.PlaceholderDate, today));
        // Arithmetic the readers do on a date does not overflow.
        Assert.Equal(3001, LeaseDraftDefaults.PlaceholderDate.AddYears(1).Year);
        Assert.Equal(DateTimeKind.Utc, LeaseDraftDefaults.PlaceholderDate.Kind);
    }

    [Fact]
    public void ApplyColumns_AValidStartWithoutAValidType_EndsAFirstPeriodOfTheStartingTypeLater()
    {
        var lease = NewLease();

        LeaseDraftProjection.ApplyColumns(lease, LeaseDraftRules.Read(Complete() with { ContractType = null }), null);

        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), lease.StartDate);
        Assert.Equal(new DateTime(2030, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);
    }

    [Fact]
    public void ApplyColumns_AConcordato_WritesTheThreeYearsPeriod_AndTheRangeWhenTheRentIsInsideIt()
    {
        var lease = NewLease();
        var reading = LeaseDraftRules.Read(Complete() with { ContractType = "Concordato" });

        LeaseDraftProjection.ApplyColumns(lease, reading, Assessed(withinRange: true, indicative: false));

        Assert.Equal(LeaseContractType.Concordato, lease.ContractType);
        Assert.Equal(FiscalRegime.CanoneConcordato, lease.FiscalRegime);
        Assert.Equal(new DateTime(2029, 10, 31, 0, 0, 0, DateTimeKind.Utc), lease.EndDate);
        Assert.NotNull(lease.ConcordatoAssessment);
        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);
    }

    [Theory]
    [InlineData(true, false, true)] // inside a verified range
    [InlineData(false, true, true)] // outside an indicative one: stored, the assessment records it (A7-23)
    [InlineData(false, false, false)] // outside a verified one: a block, never stored
    public void ApplyColumns_TheRangeIsStoredAsTheCompleteCreationStoresIt(bool within, bool indicative, bool stored)
    {
        var lease = NewLease();
        var reading = LeaseDraftRules.Read(Complete() with { ContractType = "Concordato" });

        LeaseDraftProjection.ApplyColumns(lease, reading, Assessed(within, indicative));

        Assert.Equal(stored, lease.ConcordatoAssessment is not null);
    }

    [Fact]
    public void ApplyColumns_NoRangeWithoutAValidRent_AndNoRangeForAnotherType()
    {
        var withoutRent = NewLease();
        LeaseDraftProjection.ApplyColumns(withoutRent, LeaseDraftRules.Read(Complete() with { ContractType = "Concordato", MonthlyRent = null }), Assessed(true, false));
        var libero = NewLease();
        libero.ConcordatoAssessment = new LeaseConcordatoAssessment { Sqm = 50 };
        LeaseDraftProjection.ApplyColumns(libero, LeaseDraftRules.Read(Complete()), Assessed(true, false));

        Assert.Null(withoutRent.ConcordatoAssessment);
        Assert.Null(libero.ConcordatoAssessment);
    }

    // ---- parties -------------------------------------------------------------------------------------------------

    [Fact]
    public void ToRows_OnlyTheCompleteEntries_BecomeRows_EachRoleNumberedFromZeroInTheOrderEntered()
    {
        var first = Tenant();
        var broken = Tenant(SecondTenantFiscalCode) with { Residence = null };
        var second = Tenant(CompanyFiscalCode);
        var landlord = Landlord();
        var values = Complete() with { Landlords = [landlord], Tenants = [first, broken, second] };
        var leaseId = Guid.NewGuid();

        var rows = LeaseDraftProjection.ToRows(values, leaseId, Today);

        Assert.Equal(3, rows.Count);
        Assert.Equal([landlord.Id, first.Id, second.Id], rows.Select(r => r.Id));
        Assert.Equal([PartyRole.Landlord, PartyRole.Tenant, PartyRole.Tenant], rows.Select(r => r.Role));
        Assert.Equal([0, 0, 1], rows.Select(r => r.Position));
        Assert.All(rows, r => Assert.Equal(leaseId, r.LeaseContractId));
        Assert.DoesNotContain(rows, r => r.Id == broken.Id);
    }

    [Fact]
    public void ToRows_WritesTheValuesTheWayTheTableKeepsThem()
    {
        var entry = Tenant(citizenship: "us") with
        {
            FirstName = "  Giulia ",
            FiscalCode = " vrdgli85b02f205a",
            ContactEmail = null,
            Residence = " Bari ",
            DocumentsVerifiedOn = "2026-10-01",
        };

        var row = Assert.Single(LeaseDraftProjection.ToRows(Complete() with { Landlords = [], Tenants = [entry] }, Guid.NewGuid(), Today));

        Assert.Equal("Giulia", row.FirstName);
        Assert.Equal("VRDGLI85B02F205A", row.FiscalCode);
        Assert.Equal("US", row.Citizenship);
        Assert.Equal(string.Empty, row.ContactEmail); // the column is NOT NULL, an empty address is "none"
        Assert.Equal("Bari", row.Residence);
        Assert.Equal(new DateOnly(2026, 10, 1), row.DocumentsVerifiedOn);
        Assert.True(row.IsExtraEU);
    }

    [Theory]
    [InlineData("IT", false)]
    [InlineData("it", false)]
    [InlineData("DE", false)]
    [InlineData("CH", true)]
    [InlineData("US", true)]
    public void ToRows_ExtraEuIsDecidedByTheCitizenship(string citizenship, bool extraEu)
    {
        var row = Assert.Single(LeaseDraftProjection.ToRows(
            Complete() with { Landlords = [], Tenants = [Tenant(citizenship: citizenship)] }, Guid.NewGuid(), Today));

        Assert.Equal(extraEu, row.IsExtraEU);
    }

    [Theory]
    [InlineData("2026-10-09", true)]
    [InlineData("2026-10-10", false)] // after today: kept in the draft, reported, not in the row
    [InlineData("whenever", false)]
    [InlineData(null, false)]
    public void ToRows_TheDayTheDocumentsWereChecked_IsKeptOnlyWhenItIsARealDayNotAfterToday(string? typed, bool kept)
    {
        var row = Assert.Single(LeaseDraftProjection.ToRows(
            Complete() with { Landlords = [], Tenants = [Tenant() with { DocumentsVerifiedOn = typed }] }, Guid.NewGuid(), Today));

        Assert.Equal(kept, row.DocumentsVerifiedOn is not null);
    }

    [Fact]
    public void SameRows_TheSameValues_AreTheSameRows_AnyChangeIsNot()
    {
        var values = Complete();
        var leaseId = Guid.NewGuid();
        var rows = LeaseDraftProjection.ToRows(values, leaseId, Today);
        var copy = LeaseDraftProjection.ToRows(values, leaseId, Today);

        Assert.True(LeaseDraftProjection.SameRows(rows, copy));
        Assert.False(LeaseDraftProjection.SameRows(rows, copy.Take(1).ToList()));

        copy[1].Residence = "Lecce";
        Assert.False(LeaseDraftProjection.SameRows(rows, copy));

        var moved = LeaseDraftProjection.ToRows(values with { Tenants = [Tenant(SecondTenantFiscalCode), values.Tenants[0]] }, leaseId, Today);
        Assert.False(LeaseDraftProjection.SameRows(rows, moved));
    }

    // ---- reading back --------------------------------------------------------------------------------------------

    [Fact]
    public void ReadValues_ADraftThatKeepsItsData_GivesTheTypedState_WithThePropertyOfTheColumn()
    {
        var typed = new LeaseDraftValues { PropertyId = Guid.NewGuid(), MonthlyRent = -5, StartDate = "soon" };
        var lease = NewLease();
        lease.DraftData = typed.ToJson();

        var values = LeaseDraftProjection.ReadValues(lease);

        Assert.Equal(-5m, values.MonthlyRent);
        Assert.Equal("soon", values.StartDate);
        Assert.Equal(PropertyId, values.PropertyId); // the column is the truth for the property
    }

    [Fact]
    public void ReadValues_ACompleteDraft_IsRebuiltFromTheColumnsAndTheRows_ItGivesBackWhatWasTyped()
    {
        var typed = Complete();
        var lease = NewLease();
        LeaseDraftProjection.ApplyColumns(lease, LeaseDraftRules.Read(typed), null);
        foreach (var row in LeaseDraftProjection.ToRows(typed, lease.Id, Today))
            lease.Parties.Add(row);
        lease.DraftData = null;

        var values = LeaseDraftProjection.ReadValues(lease);

        Assert.Equal(typed.PropertyId, values.PropertyId);
        Assert.Equal(typed.ContractType, values.ContractType);
        Assert.Equal(typed.TaxRegime, values.TaxRegime);
        Assert.Equal(typed.MonthlyRent, values.MonthlyRent);
        Assert.Equal(typed.SecurityDeposit, values.SecurityDeposit);
        Assert.Equal(typed.StartDate, values.StartDate);
        Assert.Equal(typed.Landlords, values.Landlords);
        Assert.Equal(typed.Tenants.Select(t => t with { ContactEmail = null }), values.Tenants);
        // And what the typed values give is what the rows give back: the second projection changes nothing.
        Assert.True(LeaseDraftProjection.SameRows(lease.Parties, LeaseDraftProjection.ToRows(values, lease.Id, Today)));
    }

    [Fact]
    public void ReadValues_ALeaseWhoseDatesWereNeverTyped_HasNoRentAndNoStart()
    {
        var lease = NewLease();

        var values = LeaseDraftProjection.FromColumns(lease);

        Assert.Null(values.MonthlyRent);
        Assert.Null(values.StartDate);
        Assert.Empty(values.Landlords);
    }

    [Fact]
    public void ReadValues_AConcordatoLease_GivesBackTheCharacteristicsOfItsAssessment()
    {
        var lease = NewLease();
        lease.ContractType = LeaseContractType.Concordato;
        lease.ConcordatoAssessment = new LeaseConcordatoAssessment
        {
            Sqm = 70,
            TypeAElementCount = 2,
            IsFurnished = true,
            GarageSqm = 12,
            ZoneName = "Centro",
            CadastralSheet = "14",
        };

        var concordato = LeaseDraftProjection.FromColumns(lease).Concordato!;

        Assert.Equal(70m, concordato.Sqm);
        Assert.Equal(2, concordato.TypeAElementCount);
        Assert.True(concordato.IsFurnished);
        Assert.Equal(12m, concordato.GarageSqm);
        Assert.Equal("Centro", concordato.ZoneName);
        Assert.Equal("14", concordato.CadastralSheet);
    }

    [Fact]
    public void TheDraftData_IsTheJsonOfTheValues_AndSurvivesTheRoundTrip()
    {
        var values = Complete() with
        {
            Concordato = new LeaseDraftConcordato { Sqm = 70.5m, TypeAElementCount = 2, IsFurnished = true, ZoneName = "Centro" },
            StartDate = "soon",
        };

        var back = LeaseDraftValues.FromJson(values.ToJson());

        Assert.Equal(values.Landlords, back!.Landlords);
        Assert.Equal(values.Tenants, back.Tenants);
        Assert.Equal(values.Concordato, back.Concordato);
        Assert.Equal((values.PropertyId, values.ContractType, values.TaxRegime, values.MonthlyRent, values.SecurityDeposit, values.StartDate),
            (back.PropertyId, back.ContractType, back.TaxRegime, back.MonthlyRent, back.SecurityDeposit, back.StartDate));
        Assert.Null(LeaseDraftValues.FromJson(null));
        Assert.Null(LeaseDraftValues.FromJson("  "));
        Assert.Null(LeaseDraftValues.FromJson("{not json"));
    }
}

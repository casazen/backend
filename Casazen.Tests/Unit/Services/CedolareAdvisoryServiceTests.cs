using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LT-08 (A7-09): the lease tax advisory with the parameters committed in appsettings.json (fiscale.md L5-L12, C11-C12)
/// and the cases of the audit: canone concordato in Seveso at 800 €/month with the ATA listing not verified, the 70%
/// registration base, the 67 € minimum at 250 €/month, no registration tax with the cedolare, the stamp duty rule.
/// </summary>
public class CedolareAdvisoryServiceTests
{
    private const string OwnerId = "auth0|owner";
    private const string Seveso = "Seveso";

    /// <summary>A day of 2026, the tax year of the configured IRPEF brackets.</summary>
    private static readonly DateTimeOffset In2026 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateAsync_ConcordatoInSevesoWithAtaNotVerified_UsesStandardCedolareAndFullRegistroBase()
    {
        var lease = Lease(LeaseContractType.Concordato, LeaseTaxRegime.CedolareSecca, 800m, Seveso);

        var result = await Sut(lease, Ata(Seveso, verified: false)).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.Equal(HighTensionAreaStatus.Unverified, result.Ata);
        Assert.False(result.ConcordatoAtaReliefs);
        Assert.Equal(9600.00m, result.AnnualRent);
        Assert.Equal(0.21m, result.Cedolare.Rate);
        Assert.Equal(CedolareRateBasis.Standard, result.Cedolare.RateBasis);
        Assert.Equal(2016.00m, result.Cedolare.AnnualTaxEur);
        Assert.Equal(1m, result.Ordinary.Registro.BaseShare);
        Assert.Equal(192.00m, result.Ordinary.Registro.FirstYearEur);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.AtaUnverified, result.Notes);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.EmergencyComuniNotChecked, result.Notes);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.ConcordatoAttestationRequired, result.Notes);
    }

    [Fact]
    public async Task EvaluateAsync_ConcordatoInVerifiedAtaComune_UsesReducedRateAndSeventyPercentRegistroBase()
    {
        var lease = Lease(LeaseContractType.Concordato, LeaseTaxRegime.Ordinario, 800m, Seveso);

        var result = await Sut(lease, Ata(Seveso, verified: true)).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.True(result.ConcordatoAtaReliefs);
        Assert.Equal(0.10m, result.Cedolare.Rate);
        Assert.Equal(CedolareRateBasis.ConcordatoAta, result.Cedolare.RateBasis);
        Assert.Equal(960.00m, result.Cedolare.AnnualTaxEur);
        var registro = result.Ordinary.Registro;
        Assert.Equal(0.70m, registro.BaseShare);
        Assert.Equal(6720.00m, registro.TaxableBaseEur);
        Assert.Equal(134.40m, registro.ComputedEur);
        Assert.False(registro.MinimumApplied);
        Assert.Equal(134.40m, registro.FirstYearEur);
        Assert.DoesNotContain(LeaseTaxAdvisoryNoteCodes.AtaUnverified, result.Notes);
    }

    [Fact]
    public async Task EvaluateAsync_ConcordatoInComuneNotInAtaList_UsesStandardRateWithNote()
    {
        var lease = Lease(LeaseContractType.Concordato, LeaseTaxRegime.CedolareSecca, 800m, "Misinto");

        var result = await Sut(lease).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.Equal(HighTensionAreaStatus.NotListed, result.Ata);
        Assert.Equal(0.21m, result.Cedolare.Rate);
        Assert.Equal(192.00m, result.Ordinary.Registro.FirstYearEur);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.AtaNotListed, result.Notes);
    }

    [Fact]
    public async Task EvaluateAsync_OrdinaryLeaseAt250PerMonth_AppliesFirstYearRegistroMinimum()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 250m);

        var result = await Sut(lease).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        var registro = result.Ordinary.Registro;
        Assert.Equal(3000.00m, registro.TaxableBaseEur);
        Assert.Equal(60.00m, registro.ComputedEur);
        Assert.True(registro.MinimumApplied);
        Assert.Equal(67.00m, registro.FirstYearMinimumEur);
        Assert.Equal(67.00m, registro.FirstYearEur);
    }

    [Fact]
    public async Task EvaluateAsync_ConcordatoInVerifiedAtaAt300PerMonth_AppliesMinimumOnReducedBase()
    {
        var lease = Lease(LeaseContractType.Concordato, LeaseTaxRegime.Ordinario, 300m, Seveso);

        var result = await Sut(lease, Ata(Seveso, verified: true)).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.Equal(2520.00m, result.Ordinary.Registro.TaxableBaseEur);
        Assert.Equal(50.40m, result.Ordinary.Registro.ComputedEur);
        Assert.Equal(67.00m, result.Ordinary.Registro.FirstYearEur);
    }

    [Fact]
    public async Task EvaluateAsync_CedolareOption_HasNoRegistroNorBollo()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca, 1000m);

        var result = await Sut(lease).EvaluateAsync(lease.Id, new CedolareAdvisoryInput(WrittenPages: 8, Copies: 2));

        Assert.NotNull(result);
        Assert.Equal(0.21m, result.Cedolare.Rate);
        Assert.Equal(2520.00m, result.Cedolare.AnnualTaxEur);
        Assert.Equal(0m, result.Cedolare.RegistroEur);
        Assert.Equal(0m, result.Cedolare.BolloEur);
        // The ordinary option of the same lease does pay them.
        Assert.Equal(240.00m, result.Ordinary.Registro.FirstYearEur);
        Assert.Equal(64.00m, result.Ordinary.Bollo.AmountEur);
    }

    [Fact]
    public async Task EvaluateAsync_BolloWithoutPagesOrCopies_ReturnsTheRuleWithoutAmount()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 800m);
        var sut = Sut(lease);

        foreach (var input in new[]
                 {
                     CedolareAdvisoryInput.None,
                     new CedolareAdvisoryInput(WrittenPages: 6),
                     new CedolareAdvisoryInput(Copies: 2),
                 })
        {
            var bollo = (await sut.EvaluateAsync(lease.Id, input))!.Ordinary.Bollo;

            Assert.Equal(AdvisoryEstimateStatus.InputRequired, bollo.Status);
            Assert.Null(bollo.AmountEur);
            Assert.Equal(16.00m, bollo.EurPerUnit);
            Assert.Equal(4, bollo.PagesPerUnit);
            Assert.Equal(100, bollo.LinesPerUnit);
        }
    }

    [Theory]
    [InlineData(4, null, 1, 1, 16.00)]
    [InlineData(5, null, 2, 2, 64.00)]
    [InlineData(8, 150, 2, 2, 64.00)]
    [InlineData(4, 250, 1, 3, 48.00)]
    [InlineData(1, 100, 3, 1, 48.00)]
    [InlineData(12, 101, 2, 3, 96.00)]
    public async Task EvaluateAsync_Bollo_SixteenEurosEveryFourPagesOrHundredLinesForEachCopy(
        int pages, int? lines, int copies, int expectedUnits, double expectedAmount)
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 800m);

        var bollo = (await Sut(lease).EvaluateAsync(lease.Id, new CedolareAdvisoryInput(pages, lines, copies)))!.Ordinary.Bollo;

        Assert.Equal(AdvisoryEstimateStatus.Computed, bollo.Status);
        Assert.Equal(expectedUnits, bollo.Units);
        Assert.Equal(copies, bollo.Copies);
        Assert.Equal((decimal)expectedAmount, bollo.AmountEur);
        Assert.Equal(lines is not null, bollo.LinesConsidered);
    }

    [Fact]
    public async Task EvaluateAsync_TransitorioInVerifiedAtaComune_KeepsStandardRateAndNotesTheDoubt()
    {
        var lease = Lease(LeaseContractType.Transitorio, LeaseTaxRegime.CedolareSecca, 800m, Seveso, months: 6);

        var result = await Sut(lease, Ata(Seveso, verified: true)).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.False(result.ConcordatoAtaReliefs);
        Assert.Equal(0.21m, result.Cedolare.Rate);
        Assert.Equal(1m, result.Ordinary.Registro.BaseShare);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.TransitorioReliefsToConfirm, result.Notes);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.ShortTermAnnualized, result.Notes);
    }

    [Fact]
    public async Task EvaluateAsync_ExtraEuTenant_NotesThatTheRegistrationDoesNotReplaceTheQuestura()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca, 800m);
        lease.Parties = [new Party { Role = PartyRole.Tenant, IsExtraEU = true, FirstName = "A", LastName = "B" }];

        var result = await Sut(lease).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.QuesturaNotReplaced, result.Notes);
    }

    [Fact]
    public async Task EvaluateAsync_OlderConcordatoWithoutTaxRegime_NotesTheUnknownRegime()
    {
        var lease = Lease(LeaseContractType.Concordato, taxRegime: null, 800m, Seveso);

        var result = await Sut(lease, Ata(Seveso, verified: false)).EvaluateAsync(lease.Id);

        Assert.NotNull(result);
        Assert.Null(result.TaxRegime);
        Assert.Equal(FiscalRegime.CanoneConcordato, result.LeaseRegime);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.TaxRegimeUnknown, result.Notes);
    }

    [Fact]
    public async Task EvaluateAsync_IrpefWithoutOtherIncome_AsksForIt()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 800m);

        var irpef = (await Sut(lease).EvaluateAsync(lease.Id))!.Ordinary.Irpef;

        Assert.Equal(AdvisoryEstimateStatus.InputRequired, irpef.Status);
        Assert.Null(irpef.AdditionalGrossIrpefEur);
        Assert.Equal(2026, irpef.TaxYear);
        Assert.Equal(9120.00m, irpef.TaxableRentEur);
    }

    [Theory]
    [InlineData(20_000, 800, 2209.60)] // 28.000 × 23% + 1.120 × 33% − 20.000 × 23%
    [InlineData(45_000, 1000, 4402.00)] // 56.400 crosses into the 43% bracket
    [InlineData(0, 250, 655.50)] // 2.850 × 23%
    public async Task EvaluateAsync_IrpefWithOtherIncome_ComputesTheAdditionalGrossTaxWithTheBrackets(
        double otherIncome, double monthlyRent, double expected)
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, (decimal)monthlyRent);

        var irpef = (await Sut(lease).EvaluateAsync(
            lease.Id, new CedolareAdvisoryInput(OtherTaxableIncomeEur: (decimal)otherIncome)))!.Ordinary.Irpef;

        Assert.Equal(AdvisoryEstimateStatus.Computed, irpef.Status);
        Assert.Null(irpef.ReasonCode);
        Assert.Equal((decimal)expected, irpef.AdditionalGrossIrpefEur);
    }

    [Fact]
    public async Task EvaluateAsync_IrpefAboveTheIncomeLimit_IsNotComputed()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 800m);

        var irpef = (await Sut(lease).EvaluateAsync(
            lease.Id, new CedolareAdvisoryInput(OtherTaxableIncomeEur: 195_000m)))!.Ordinary.Irpef;

        Assert.Equal(AdvisoryEstimateStatus.NotComputed, irpef.Status);
        Assert.Equal(IrpefNotComputedReasons.IncomeOverLimit, irpef.ReasonCode);
        Assert.Null(irpef.AdditionalGrossIrpefEur);
    }

    [Fact]
    public async Task EvaluateAsync_IrpefBracketsOfAnEarlierYear_AreNotUsed()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 800m);
        var sut = Sut(lease, clock: new FakeTimeProvider(new DateTimeOffset(2027, 1, 2, 9, 0, 0, TimeSpan.Zero)));

        var irpef = (await sut.EvaluateAsync(lease.Id, new CedolareAdvisoryInput(OtherTaxableIncomeEur: 20_000m)))!
            .Ordinary.Irpef;

        Assert.Equal(AdvisoryEstimateStatus.NotComputed, irpef.Status);
        Assert.Equal(IrpefNotComputedReasons.BracketsOutdated, irpef.ReasonCode);
        Assert.Null(irpef.AdditionalGrossIrpefEur);
    }

    [Fact]
    public async Task EvaluateAsync_IrpefOfConcordatoInVerifiedAta_IsNotComputed()
    {
        var lease = Lease(LeaseContractType.Concordato, LeaseTaxRegime.Ordinario, 800m, Seveso);

        var irpef = (await Sut(lease, Ata(Seveso, verified: true)).EvaluateAsync(
            lease.Id, new CedolareAdvisoryInput(OtherTaxableIncomeEur: 20_000m)))!.Ordinary.Irpef;

        Assert.Equal(AdvisoryEstimateStatus.NotComputed, irpef.Status);
        Assert.Equal(IrpefNotComputedReasons.ConcordatoAtaReliefNotVerified, irpef.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAsync_IrpefWithoutConfiguredBrackets_IsNotComputed()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, 800m);
        var options = CedolareAdvisoryConfigurationTests.FromAppsettings();
        options.Irpef.Brackets.Clear();

        var irpef = (await Sut(lease, options: options).EvaluateAsync(
            lease.Id, new CedolareAdvisoryInput(OtherTaxableIncomeEur: 20_000m)))!.Ordinary.Irpef;

        Assert.Equal(AdvisoryEstimateStatus.NotComputed, irpef.Status);
        Assert.Equal(IrpefNotComputedReasons.NotConfigured, irpef.ReasonCode);
        Assert.Null(irpef.TaxYear);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvaluateAsync_AtaRule_IsTheSameAsTheCanoneConcordatoCalculator(bool? verified)
    {
        var lease = Lease(LeaseContractType.Concordato, LeaseTaxRegime.CedolareSecca, 800m, Seveso);
        var ata = verified is { } v ? Ata(Seveso, v) : null;

        var result = await Sut(lease, ata).EvaluateAsync(lease.Id);

        // The calculator shows "ATA verified" with HighTensionArea.ReliefsApply: the advisory applies the reliefs exactly then.
        Assert.Equal(HighTensionArea.ReliefsApply(ata), result!.ConcordatoAtaReliefs);
        Assert.Equal(verified == true ? 0.10m : 0.21m, result.Cedolare.Rate);
    }

    [Fact]
    public async Task EvaluateAsync_LeaseNotVisible_ReturnsNull()
    {
        var lease = Lease(LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca, 1000m);

        // Who may read the lease is decided by the controller (TN-3); an id outside the caller's org is not found.
        Assert.Null(await Sut(lease).EvaluateAsync(Guid.NewGuid()));
    }

    private static CedolareAdvisoryService Sut(
        LeaseContract lease,
        HighTensionAreaComune? ata = null,
        CedolareAdvisoryOptions? options = null,
        TimeProvider? clock = null)
    {
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

        var ataComuni = new Mock<IHighTensionAreaComuneRepository>();
        if (ata is not null)
        {
            ataComuni.Setup(r => r.GetByComuneAsync(ata.Comune, It.IsAny<CancellationToken>())).ReturnsAsync(ata);
        }

        return new CedolareAdvisoryService(
            leases.Object,
            ataComuni.Object,
            Options.Create(options ?? CedolareAdvisoryConfigurationTests.FromAppsettings()),
            clock ?? new FakeTimeProvider(In2026));
    }

    private static HighTensionAreaComune Ata(string comune, bool verified) => new()
    {
        Comune = comune,
        Region = "Lombardia",
        SourceReference = "test",
        VerifiedDirectly = verified,
    };

    private static LeaseContract Lease(
        LeaseContractType type, LeaseTaxRegime? taxRegime, decimal rent, string city = "Milano", int months = 48)
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            MonthlyRent = rent,
            StartDate = start,
            EndDate = start.AddMonths(months).AddDays(-1),
            Property = new Property { OwnerId = OwnerId, City = city, Name = "X" },
            Parties = [],
        };
        lease.SetContractTerms(type, taxRegime);
        return lease;
    }
}

using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CedolareAdvisoryServiceTests
{
    private const string OwnerId = "auth0|owner";

    [Fact]
    public async Task Evaluate_CedolareSecca_UsesConfigRateAndDisclaimer()
    {
        var options = Options.Create(new CedolareAdvisoryOptions
        {
            CedolareSeccaRate = 0.21m,
            CanoneConcordatoRate = 0.10m,
            RegistroRate = 0.02m,
            BolloEur = 16m,
            Disclaimer = "Informativa, non consulenza fiscale.",
            OrdinaryIrpefNote = "IRPEF note",
        });
        var lease = Lease(FiscalRegime.CedolareSecca, 1000m);
        var sut = new CedolareAdvisoryService(Repo(lease), AtaRepo(), options);

        var result = await sut.EvaluateAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.Equal(0.21m, result.CedolareRate);
        Assert.Equal(2520.00m, result.CedolareEstimateEur);
        Assert.Equal(240.00m, result.RegistroEstimateEur);
        Assert.Equal(16m, result.BolloEur);
        Assert.Contains("non consulenza fiscale", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_CanoneConcordato_WhenAtaVerified_UsesReducedRateFromConfig()
    {
        var options = Options.Create(new CedolareAdvisoryOptions
        {
            CedolareSeccaRate = 0.21m,
            CanoneConcordatoRate = 0.10m,
            RegistroRate = 0.02m,
            BolloEur = 16m,
            Disclaimer = "Informativa, non consulenza fiscale.",
        });
        var lease = Lease(FiscalRegime.CanoneConcordato, 500m);
        var sut = new CedolareAdvisoryService(Repo(lease), AtaRepo(verifiedDirectly: true), options);

        var result = await sut.EvaluateAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.Equal(0.10m, result.CedolareRate);
        Assert.Equal(600.00m, result.CedolareEstimateEur);
    }

    [Fact]
    public async Task Evaluate_CanoneConcordato_WhenAtaNotVerified_UsesStandardCedolareRate()
    {
        var options = Options.Create(new CedolareAdvisoryOptions
        {
            CedolareSeccaRate = 0.21m,
            CanoneConcordatoRate = 0.10m,
            RegistroRate = 0.02m,
            BolloEur = 16m,
            Disclaimer = "Informativa, non consulenza fiscale.",
        });
        var lease = Lease(FiscalRegime.CanoneConcordato, 500m);
        var sut = new CedolareAdvisoryService(Repo(lease), AtaRepo(verifiedDirectly: false), options);

        var result = await sut.EvaluateAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.Equal(0.21m, result.CedolareRate);
        Assert.Equal(1260.00m, result.CedolareEstimateEur);
    }

    [Fact]
    public async Task Evaluate_RegimeOrdinario_ReturnsRegistroBolloAndDisclaimer()
    {
        var options = Options.Create(new CedolareAdvisoryOptions
        {
            CedolareSeccaRate = 0.21m,
            CanoneConcordatoRate = 0.10m,
            RegistroRate = 0.02m,
            BolloEur = 16m,
            Disclaimer = "Informativa, non consulenza fiscale.",
            OrdinaryIrpefNote = "IRPEF a scaglioni",
        });
        var lease = Lease(FiscalRegime.RegimeOrdinario, 800m);
        var sut = new CedolareAdvisoryService(Repo(lease), AtaRepo(), options);

        var result = await sut.EvaluateAsync(lease.Id, OwnerId);

        Assert.NotNull(result);
        Assert.Equal(FiscalRegime.RegimeOrdinario, result.LeaseRegime);
        Assert.Equal(192.00m, result.RegistroEstimateEur);
        Assert.Equal(16m, result.BolloEur);
        Assert.Contains("IRPEF", result.OrdinaryIrpefNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non consulenza fiscale", result.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evaluate_WrongOwner_ReturnsNull()
    {
        var lease = Lease(FiscalRegime.CedolareSecca, 1000m);
        var sut = new CedolareAdvisoryService(
            Repo(lease), AtaRepo(), Options.Create(new CedolareAdvisoryOptions()));

        Assert.Null(await sut.EvaluateAsync(lease.Id, "auth0|other"));
    }

    private static ILeaseContractRepository Repo(LeaseContract lease)
    {
        var mock = new Mock<ILeaseContractRepository>();
        mock.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        return mock.Object;
    }

    private static IHighTensionAreaComuneRepository AtaRepo(bool? verifiedDirectly = null)
    {
        var mock = new Mock<IHighTensionAreaComuneRepository>();
        if (verifiedDirectly.HasValue)
        {
            mock.Setup(r => r.GetByComuneAsync("Milano", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HighTensionAreaComune
                {
                    Comune = "Milano",
                    Region = "Lombardia",
                    SourceReference = "test",
                    VerifiedDirectly = verifiedDirectly.Value,
                });
        }

        return mock.Object;
    }

    private static LeaseContract Lease(FiscalRegime regime, decimal rent) => new()
    {
        Id = Guid.NewGuid(),
        FiscalRegime = regime,
        MonthlyRent = rent,
        Property = new Property { OwnerId = OwnerId, City = "Milano", Name = "X" },
        Parties = [],
    };
}

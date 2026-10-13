using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services.LeaseContracts;

/// <summary>
/// LT-03 (A7-03): outside Development/Testing a fake approval of a lease contract template stops the startup
/// (<c>ValidateOnStart</c>); by default nothing is approved and the API starts.
/// </summary>
public sealed class LeaseTemplateStartupValidationTests : IDisposable
{
    private readonly LeaseTemplateTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Startup_ProductionWithDevStubApproved_Fails()
    {
        using var provider = BuildProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["LeaseTemplates:Variants:CedolareSecca:VersionId"] = "dev-stub",
            ["LeaseTemplates:Variants:CedolareSecca:Approved"] = "true",
            ["LeaseTemplates:Variants:CedolareSecca:ApprovalReference"] = "legacy config",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("CedolareSecca", StringComparison.Ordinal)
                                          && f.Contains("dev-stub", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithCommittedDefaults_Succeeds()
    {
        using var provider = BuildProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["LeaseTemplates:Variants:CedolareSecca:VersionId"] = "",
            ["LeaseTemplates:Variants:CedolareSecca:Approved"] = "false",
            ["LeaseTemplates:Variants:CanoneConcordato:VersionId"] = "",
            ["LeaseTemplates:Variants:CanoneConcordato:Approved"] = "false",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Startup_ProductionApprovedWithoutReferenceOrDate_Fails()
    {
        _files.Write(FiscalRegime.CanoneConcordato, "v1", LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CanoneConcordato));
        using var provider = BuildProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["LeaseTemplates:TemplatesDirectory"] = _files.Root,
            ["LeaseTemplates:Variants:CanoneConcordato:VersionId"] = "v1",
            ["LeaseTemplates:Variants:CanoneConcordato:Approved"] = "true",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("ApprovalReference or ApprovedAt", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionApprovedIncompleteTemplate_Fails()
    {
        _files.Write(
            FiscalRegime.RegimeOrdinario,
            "v1",
            LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.RegimeOrdinario, omit: ["deposito"]));
        using var provider = BuildProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["LeaseTemplates:TemplatesDirectory"] = _files.Root,
            ["LeaseTemplates:Variants:RegimeOrdinario:VersionId"] = "v1",
            ["LeaseTemplates:Variants:RegimeOrdinario:Approved"] = "true",
            ["LeaseTemplates:Variants:RegimeOrdinario:ApprovedAt"] = "2026-10-01",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("Incomplete", StringComparison.Ordinal)
                                          && f.Contains("'deposito'", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionApprovedCompleteTemplate_Succeeds()
    {
        _files.Write(FiscalRegime.CanoneConcordato, "v1", LeaseTemplateTestFiles.CompleteTemplate(FiscalRegime.CanoneConcordato));
        using var provider = BuildProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["LeaseTemplates:TemplatesDirectory"] = _files.Root,
            ["LeaseTemplates:Variants:CanoneConcordato:VersionId"] = "v1",
            ["LeaseTemplates:Variants:CanoneConcordato:Approved"] = "true",
            ["LeaseTemplates:Variants:CanoneConcordato:ApprovalReference"] = "parere legale di prova",
            ["LeaseTemplates:Variants:CanoneConcordato:ApprovedAt"] = "2026-10-01",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<LeaseTemplateOptions>>().Value;
        Assert.Equal(new DateOnly(2026, 10, 1), options.Variants["CanoneConcordato"].ApprovedAt);
    }

    [Fact]
    public void Startup_TestingWithDevStubApproved_StartsButTheApprovalDoesNotCount()
    {
        using var provider = BuildProvider("Testing", new Dictionary<string, string?>
        {
            ["LeaseTemplates:Variants:CedolareSecca:VersionId"] = "dev-stub",
            ["LeaseTemplates:Variants:CedolareSecca:Approved"] = "true",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
        var catalog = provider.GetRequiredService<Casazen.Infrastructure.Services.LeaseContracts.ILeaseContractTemplateCatalog>();
        Assert.False(catalog.Get(FiscalRegime.CedolareSecca).IsApproved);
    }

    private static ServiceProvider BuildProvider(string environment, Dictionary<string, string?> values)
    {
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);
        hostEnvironment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(hostEnvironment.Object);
        services.AddCasazenLeaseContractTemplates(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }
}

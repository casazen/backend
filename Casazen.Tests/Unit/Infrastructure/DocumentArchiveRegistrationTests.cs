using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-99, E07/F03/US03: what <c>AddDocumentArchive</c> registers (the exact list of the sources, so a new one is added here on purpose, and the
/// container can build the service), who may read each source, the keys of the types and the caps of the export.
/// </summary>
public class DocumentArchiveRegistrationTests
{
    [Fact]
    public void AddDocumentArchive_RegistersTheThreeSourcesTheServiceAndTheCaps_AndNothingElse()
    {
        var services = new ServiceCollection();

        services.AddDocumentArchive();

        var sources = services.Where(d => d.ServiceType == typeof(IDocumentArchiveSource)).ToList();
        Assert.Equal(
            [typeof(PropertyDocumentArchiveSource), typeof(LeaseDocumentArchiveSource), typeof(TouristTaxRemittanceArchiveSource)],
            sources.Select(s => s.ImplementationType));
        Assert.All(sources, s => Assert.Equal(ServiceLifetime.Scoped, s.Lifetime));
        var service = Assert.Single(services, d => d.ServiceType == typeof(IDocumentArchiveService));
        Assert.Equal(typeof(DocumentArchiveService), service.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, service.Lifetime);
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<DocumentArchiveOptions>));
    }

    [Fact]
    public void TheContainer_BuildsTheService_WithTheThreeSourcesInOrder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"document-archive-{Guid.NewGuid()}"));
        services.AddSingleton<IFileStorage, InMemoryFileStorage>();
        services.AddDocumentArchive();
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.IsType<DocumentArchiveService>(scope.ServiceProvider.GetRequiredService<IDocumentArchiveService>());
        Assert.Equal(
            [ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract, ArchiveDocumentType.TouristTax],
            scope.ServiceProvider.GetServices<IDocumentArchiveSource>().Select(s => s.Type));
        Assert.Equal(DocumentArchiveOptions.DefaultMaxExportFiles, provider.GetRequiredService<IOptions<DocumentArchiveOptions>>().Value.MaxExportFiles);
    }

    /// <summary>Who may read each source: the permission of the area its documents belong to, in the context that grants it.</summary>
    [Fact]
    public void EachSource_SaysWhichPermissionReadsIt()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var storage = new InMemoryFileStorage();
        IDocumentArchiveSource[] sources =
        [
            new PropertyDocumentArchiveSource(db, storage),
            new LeaseDocumentArchiveSource(db, storage),
            new TouristTaxRemittanceArchiveSource(db, storage),
        ];

        Assert.Equal(
            [
                (ArchiveDocumentType.PropertyDocument, "short-rent|long-rent", "property.read"),
                (ArchiveDocumentType.Contract, "long-rent", "lease.read"),
                (ArchiveDocumentType.TouristTax, "short-rent", "payment.read"),
            ],
            sources.Select(s => (s.Type, s.ContextKey, s.PermissionKey)));
    }

    [Fact]
    public void EveryType_HasAKey_ThatIsUnique_AndParsesBack_WhateverTheCase()
    {
        var types = Enum.GetValues<ArchiveDocumentType>();
        var keys = types.Select(ArchiveDocumentTypes.KeyOf).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var type in types)
        {
            var key = ArchiveDocumentTypes.KeyOf(type);
            Assert.True(ArchiveDocumentTypes.TryParse(key, out var parsed));
            Assert.Equal(type, parsed);
            Assert.True(ArchiveDocumentTypes.TryParse($"  {key.ToUpperInvariant()} ", out var shouted));
            Assert.Equal(type, shouted);
        }

        Assert.Equal(
            ["invoice", "receipt", "creditNote", "alloggiatiReceipt", "istatProof", "touristTax", "contract", "propertyDocument"],
            keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("7")]
    [InlineData("PropertyDocuments")]
    [InlineData("passport")]
    public void AWordThatIsNotAKey_IsNotAType(string? word)
    {
        Assert.False(ArchiveDocumentTypes.TryParse(word, out _));
    }

    [Fact]
    public void OnlyTheAlloggiatiReceiptsAndTheIstatProofs_AreComplianceProofs()
    {
        Assert.Equal(
            [ArchiveDocumentType.AlloggiatiReceipt, ArchiveDocumentType.IstatProof],
            Enum.GetValues<ArchiveDocumentType>().Where(ArchiveDocumentTypes.IsComplianceProof));
    }

    // ---------------------------------------------------------------- the caps

    [Fact]
    public void TheDefaultCaps_AreThreeHundredDocumentsAndTwoHundredMegabytes_AndAreValid()
    {
        var options = new DocumentArchiveOptions();

        Assert.Equal(300, options.MaxExportFiles);
        Assert.Equal(200L * 1024 * 1024, options.MaxExportBytes);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0, 209715200L, "DocumentArchive__MaxExportFiles")]
    [InlineData(2001, 209715200L, "DocumentArchive__MaxExportFiles")]
    [InlineData(300, 1024L, "DocumentArchive__MaxExportBytes")]
    [InlineData(300, 3L * 1024 * 1024 * 1024, "DocumentArchive__MaxExportBytes")]
    public void ACapOutOfRange_IsRefusedAtStartup_NamingTheVariable(int files, long bytes, string variable)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentArchive:MaxExportFiles"] = files.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DocumentArchive:MaxExportBytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDocumentArchive();
        using var provider = services.BuildServiceProvider();

        var refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DocumentArchiveOptions>>().Value);

        Assert.Contains(variable, string.Join(' ', refused.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCaps_AreReadFromTheConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DocumentArchive:MaxExportFiles"] = "50",
                ["DocumentArchive:MaxExportBytes"] = "5242880",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDocumentArchive();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<DocumentArchiveOptions>>().Value;

        Assert.Equal((50, 5242880L), (options.MaxExportFiles, options.MaxExportBytes));
    }
}

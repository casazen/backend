using System.Reflection;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.HostedServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E29/F01/US01/T01 (NF-124): the official Swiss localities and municipalities of swisstopo. The seed of the build is the one checked (its numbers
/// are the ones every Ticino rule depends on), the search and the lookup run on it, and the load is idempotent, corrects the old hand-written list
/// and never stops the API.
/// </summary>
public class SwissPlacesTests(SwissPlacesTests.SeededDirectory seeded) : IClassFixture<SwissPlacesTests.SeededDirectory>
{
    private const string SmallSeed =
        "postal_code;add_on;locality;municipality_code;municipality;canton\n" +
        "6600;00;Locarno;5113;Locarno;TI\n" +
        "6900;00;Lugano;5192;Lugano;TI\n";

    /// <summary>One in-memory database with the seed of the build, loaded once for the class (the reads never write).</summary>
    public sealed class SeededDirectory : IAsyncLifetime
    {
        public string DatabaseName { get; } = "swiss-places-" + Guid.NewGuid().ToString("N");

        public AppDbContext NewContext() => NewDb(DatabaseName);

        public async Task InitializeAsync()
        {
            await using var db = NewContext();
            await SwissMunicipalitySeed.ApplyAsync(db, SwissMunicipalitySeed.ReadEmbedded()!, TimeProvider.System, CancellationToken.None);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Data: the BFS numbers the whole Ticino logic depends on are the official ones, read from the seed this build loads (the old hand-written list
    /// had Lugano as 5113, Locarno as 5093, Mendrisio as 5190 and Ascona as 5237; the brief expected Ascona 5101, the official file says 5091).
    /// </summary>
    [Fact]
    public void SwissPlaces_KeyTicinoMunicipalities_HaveOfficialNumbers_Test()
    {
        var seed = SwissMunicipalitySeed.Parse(SwissMunicipalitySeed.ReadEmbedded()!);
        var municipalities = seed.Municipalities.ToDictionary(m => m.MunicipalityCode);

        foreach (var (code, name) in new[] { ("5192", "Lugano"), ("5113", "Locarno"), ("5002", "Bellinzona"), ("5254", "Mendrisio"), ("5091", "Ascona") })
        {
            var municipality = municipalities.GetValueOrDefault(code);
            Assert.NotNull(municipality);
            Assert.Equal((name, "TI"), (municipality.OfficialName, municipality.CantonCode));
        }

        Assert.DoesNotContain("5093", municipalities.Keys);
        Assert.DoesNotContain("5190", municipalities.Keys);
        Assert.NotEqual("Ascona", municipalities["5237"].OfficialName);
    }

    [Fact]
    public void Seed_HoldsEveryMunicipalityOfTheOfficialDirectory_InTheTwentySixCantons()
    {
        var seed = SwissMunicipalitySeed.Parse(SwissMunicipalitySeed.ReadEmbedded()!);

        Assert.True(seed.Municipalities.Count > 2000, $"{seed.Municipalities.Count} municipalities");
        Assert.Equal(SwissMunicipalitySeed.Cantons.Count, seed.Municipalities.Select(m => m.CantonCode).Distinct().Count());
        var codes = seed.Municipalities.Select(m => m.MunicipalityCode).ToHashSet();
        Assert.All(seed.Localities, l => Assert.Contains(l.MunicipalityCode, codes));
        Assert.Equal(seed.Localities.Count, seed.Localities.Select(l => (l.PostalCode, l.AddOn, l.MunicipalityCode, l.Name)).Distinct().Count());
    }

    [Fact]
    public async Task Search_ThePostalCode6900_ReturnsLugano()
    {
        await using var db = seeded.NewContext();

        var matches = await new SwissPlacesDirectory(db).SearchLocalitiesAsync("6900", 20);

        Assert.Contains(new SwissLocalityMatch("6900", "Lugano", "5192", "Lugano", "TI"), matches);
        Assert.All(matches, m => Assert.Equal("6900", m.PostalCode));
        Assert.Equal(matches, await new SwissPlacesDirectory(db).SearchLocalitiesAsync("CH-6900", 20));
    }

    [Fact]
    public async Task Search_AFragmentOfTheName_ReturnsLocarno()
    {
        await using var db = seeded.NewContext();

        var matches = await new SwissPlacesDirectory(db).SearchLocalitiesAsync("locarn", 20);

        Assert.Contains(new SwissLocalityMatch("6600", "Locarno", "5113", "Locarno", "TI"), matches);
        Assert.StartsWith("Locarno", matches[0].Name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zzqqxx")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("12345")]
    [InlineData("0999")]
    public async Task Search_AQueryThatMatchesNothing_ReturnsAnEmptyList(string query)
    {
        await using var db = seeded.NewContext();

        Assert.Empty(await new SwissPlacesDirectory(db).SearchLocalitiesAsync(query, 20));
    }

    [Fact]
    public async Task Search_IgnoresCaseAccentsAndSpaces_PutsTheExactNameFirst_AndKeepsToTheLimit()
    {
        await using var db = seeded.NewContext();
        var directory = new SwissPlacesDirectory(db);

        Assert.Contains(await directory.SearchLocalitiesAsync("  ZURICH ", 20), m => m.Name == "Zürich" && m.CantonCode == "ZH");
        Assert.Contains(await directory.SearchLocalitiesAsync("bre  sopra", 20), m => m.Name == "Brè sopra Lugano" && m.MunicipalityCode == "5192");
        Assert.Equal("Lugano", (await directory.SearchLocalitiesAsync("LUGANO", 20))[0].Name);
        Assert.Equal(3, (await directory.SearchLocalitiesAsync("6", 3)).Count);
        Assert.Equal(ISwissPlacesDirectory.MaxLimit, (await directory.SearchLocalitiesAsync("6", 500)).Count);
    }

    [Fact]
    public async Task FindMunicipality_ByItsNumber_ReadsTheDirectory()
    {
        await using var db = seeded.NewContext();
        var directory = new SwissPlacesDirectory(db);

        Assert.Equal(new SwissMunicipalityInfo("5192", "Lugano", "TI"), await directory.FindMunicipalityAsync(" 05192 "));
        Assert.Null(await directory.FindMunicipalityAsync("9999"));
        Assert.Null(await directory.FindMunicipalityAsync("Lugano"));
    }

    [Fact]
    public async Task Controller_TheMunicipalityOfANumber_And404ForAnUnknownOne()
    {
        await using var db = seeded.NewContext();
        var controller = new SwissPlacesController(new SwissPlacesDirectory(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };

        var found = Assert.IsType<OkObjectResult>((await controller.GetMunicipality("5113", CancellationToken.None)).Result);
        var municipality = Assert.IsType<SwissMunicipalityDto>(found.Value);
        Assert.Equal(("5113", "Locarno", "TI"), (municipality.MunicipalityCode, municipality.Name, municipality.CantonCode));

        var missing = Assert.IsType<ObjectResult>((await controller.GetMunicipality("9999", CancellationToken.None)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        Assert.Equal(SwissPlacesController.MunicipalityUnknownCode, Assert.IsAssignableFrom<ProblemDetails>(missing.Value).Extensions["code"]);

        var search = Assert.IsType<OkObjectResult>((await controller.SearchLocalities("6900", 20, CancellationToken.None)).Result);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<SwissLocalityDto>>(search.Value), l => l.Name == "Lugano" && l.MunicipalityCode == "5192");
    }

    /// <summary>Permission: the directory is read only by a signed-in user who may read properties; no action of the controller is public.</summary>
    [Fact]
    public void SwissPlacesApi_RequiresAuthentication_Test()
    {
        var controller = typeof(SwissPlacesController);
        var authorize = Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(CasazenPolicies.SharedPropertyRead, authorize.Policy);
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));

        var actions = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(2, actions.Length);
        Assert.All(actions, action => Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true)));
    }

    [Fact]
    public async Task Seed_LoadsTwice_WithoutDuplicates_AndRecordsItsVersion()
    {
        await using var db = NewDb();
        var seed = SwissMunicipalitySeed.ReadEmbedded()!;

        var first = await SwissMunicipalitySeed.ApplyAsync(db, seed, TimeProvider.System, CancellationToken.None);
        var second = await SwissMunicipalitySeed.ApplyAsync(db, seed, TimeProvider.System, CancellationToken.None);

        Assert.True(first.Imported);
        Assert.False(second.Imported);
        Assert.Equal(first.Municipalities, await db.SwissMunicipalities.CountAsync());
        Assert.Equal(first.Localities, await db.SwissLocalities.CountAsync());
        var stamp = Assert.Single(await db.OfficialSourceFetches.Where(f => f.Dataset == OfficialSourceDatasets.SwisstopoLocalities).ToListAsync());
        Assert.Equal(OfficialSourceFetchStatus.Imported, stamp.Status);
        Assert.Equal(first.Sha256, stamp.Sha256);
        Assert.Equal(SwissMunicipalitySeed.Authority, stamp.Authority);
    }

    [Fact]
    public async Task Seed_OnADatabaseWithTheOldHandWrittenList_CorrectsAndRemovesTheWrongRows()
    {
        await using var db = NewDb();
        db.SwissMunicipalities.AddRange(
            new SwissMunicipality { MunicipalityCode = "5113", CantonCode = "TI", OfficialName = "Lugano" },
            new SwissMunicipality { MunicipalityCode = "5093", CantonCode = "TI", OfficialName = "Locarno" },
            new SwissMunicipality { MunicipalityCode = "5237", CantonCode = "TI", OfficialName = "Ascona" });
        db.SwissLocalities.Add(new SwissLocality { PostalCode = "6900", AddOn = "00", Name = "Lugano", NormalizedName = "lugano", MunicipalityCode = "5113", CantonCode = "TI" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await SwissMunicipalitySeed.ApplyAsync(db, Encoding.UTF8.GetBytes(SmallSeed), TimeProvider.System, CancellationToken.None);

        Assert.Equal((true, 2, 1), (result.Imported, result.MunicipalitiesRemoved, result.LocalitiesRemoved));
        Assert.Equal(
            [("5113", "Locarno"), ("5192", "Lugano")],
            (await db.SwissMunicipalities.OrderBy(m => m.MunicipalityCode).ToListAsync()).Select(m => (m.MunicipalityCode, m.OfficialName)));
        Assert.Equal(
            [("6600", "Locarno", "5113"), ("6900", "Lugano", "5192")],
            (await db.SwissLocalities.OrderBy(l => l.PostalCode).ToListAsync()).Select(l => (l.PostalCode, l.Name, l.MunicipalityCode)));
    }

    /// <summary>
    /// Data: a municipality the new file no longer has (a merger) stays while a property still carries its number, of any org and deleted ones too:
    /// removing it would leave the stored place of the property, and the canton of the tax lines of its documents, without an answer. The ones nobody
    /// carries are removed; the load says how many were kept and for how many properties, now and on the next startup with the same file.
    /// </summary>
    [Fact]
    public async Task Seed_KeepsAMunicipalityThatAPropertyStillCarries_AndRemovesTheOthers()
    {
        await using var db = NewDb();
        db.SwissMunicipalities.AddRange(
            new SwissMunicipality { MunicipalityCode = "5093", CantonCode = "TI", OfficialName = "Locarno" },
            new SwissMunicipality { MunicipalityCode = "5237", CantonCode = "TI", OfficialName = "Ascona" });
        db.Properties.AddRange(
            new Property { OrgId = Guid.NewGuid(), Name = "TEST", Country = "CH", SwissMunicipalityCode = "5093" },
            new Property { OrgId = Guid.NewGuid(), Name = "TEST deleted", Country = "CH", SwissMunicipalityCode = "5093", IsDeleted = true },
            new Property { OrgId = Guid.NewGuid(), Name = "TEST Italy", Country = "IT" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var seed = Encoding.UTF8.GetBytes(SmallSeed);

        var first = await SwissMunicipalitySeed.ApplyAsync(db, seed, TimeProvider.System, CancellationToken.None);
        var second = await SwissMunicipalitySeed.ApplyAsync(db, seed, TimeProvider.System, CancellationToken.None);

        Assert.Equal((true, 1, 1, 2), (first.Imported, first.MunicipalitiesRemoved, first.MunicipalitiesKept, first.PropertiesOnKept));
        Assert.Equal((false, 0, 1, 2), (second.Imported, second.MunicipalitiesRemoved, second.MunicipalitiesKept, second.PropertiesOnKept));
        Assert.Equal(["5093", "5113", "5192"], (await db.SwissMunicipalities.Select(m => m.MunicipalityCode).ToListAsync()).Order(StringComparer.Ordinal));
        var stamp = Assert.Single(await db.OfficialSourceFetches.Where(f => f.Dataset == OfficialSourceDatasets.SwisstopoLocalities).ToListAsync());
        Assert.Contains("kept 1 municipalities", stamp.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedService_WithAMunicipalityKeptForAProperty_LogsAWarningToConfirmIt()
    {
        var name = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5093", CantonCode = "TI", OfficialName = "Locarno" });
            db.Properties.Add(new Property { OrgId = Guid.NewGuid(), Name = "TEST", Country = "CH", SwissMunicipalityCode = "5093" });
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger<SwissPlacesSeedHostedService>();
        var service = new SwissPlacesSeedHostedService(provider, Options.Create(new OfficialReferenceDataOptions()), logger);

        await service.SeedAsync(Encoding.UTF8.GetBytes(SmallSeed), CancellationToken.None);

        Assert.Equal([LogLevel.Information, LogLevel.Warning], logger.Entries.Select(e => e.Level));
        Assert.Equal<object?>(1, logger.Entries[1].Values["KeptMunicipalities"]);
        Assert.Equal<object?>(1, logger.Entries[1].Values["Properties"]);
    }

    [Theory]
    [InlineData("postal_code;add_on;locality;municipality_code;municipality;canton\n690;00;Lugano;5192;Lugano;TI\n", "line 2")]
    [InlineData("postal_code;add_on;locality;municipality_code;municipality;canton\n6900;00;Lugano;5192;Lugano;FL\n", "line 2")]
    [InlineData("postal_code;add_on;locality;municipality_code;municipality;canton\n6900;00;Lugano;5192;Lugano;TI\n6900;05;Massagno;5192;Massagno;TI\n", "line 3")]
    [InlineData("postal_code;add_on;locality;municipality_code;municipality;canton\n6900;00;Lugano;5192;Lugano\n", "line 2")]
    [InlineData("PLZ4;Ortschaftsname\n6900;Lugano\n", "line 1")]
    public async Task Seed_WithOneWrongLine_IsRefusedWhole_AndNothingIsWritten(string content, string line)
    {
        await using var db = NewDb();

        var error = await Assert.ThrowsAsync<FormatException>(() =>
            SwissMunicipalitySeed.ApplyAsync(db, Encoding.UTF8.GetBytes(content), TimeProvider.System, CancellationToken.None));

        Assert.Contains(line, error.Message, StringComparison.Ordinal);
        Assert.Empty(await db.SwissMunicipalities.ToListAsync());
        Assert.Empty(await db.SwissLocalities.ToListAsync());
    }

    [Fact]
    public async Task HostedService_WithoutTheSeedFile_OnlyLogsAWarning_AndAWrongFileOnlyAnError()
    {
        var name = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        var logger = new CapturingLogger<SwissPlacesSeedHostedService>();
        var service = new SwissPlacesSeedHostedService(provider, Options.Create(new OfficialReferenceDataOptions()), logger);

        await service.SeedAsync(null, CancellationToken.None);
        await service.SeedAsync(Encoding.UTF8.GetBytes("not a seed"), CancellationToken.None);
        await service.SeedAsync(Encoding.UTF8.GetBytes(SmallSeed), CancellationToken.None);

        Assert.Equal([LogLevel.Warning, LogLevel.Error, LogLevel.Information], logger.Entries.Select(e => e.Level));
        await using var scope = provider.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().SwissMunicipalities.CountAsync());
    }

    [Fact]
    public async Task HostedService_TurnedOff_LoadsNothing()
    {
        var name = Guid.NewGuid().ToString();
        await using var provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        var logger = new CapturingLogger<SwissPlacesSeedHostedService>();
        var service = new SwissPlacesSeedHostedService(provider, Options.Create(new OfficialReferenceDataOptions { SeedSwissPlacesOnStartup = false }), logger);

        await service.StartAsync(CancellationToken.None);

        await using var scope = provider.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SwissMunicipalities.ToListAsync());
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData("Zürich", "zurich")]
    [InlineData("  Brè   sopra Lugano ", "bre sopra lugano")]
    [InlineData("L’Abbaye", "l'abbaye")]
    [InlineData("S. Antonio (Val Morobbia)", "s. antonio (val morobbia)")]
    [InlineData(null, "")]
    public void NormalizeName_LowerCaseWithoutAccentsAndExtraSpaces(string? name, string expected)
    {
        Assert.Equal(expected, SwissLocality.NormalizeName(name));
    }

    private static AppDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);
}

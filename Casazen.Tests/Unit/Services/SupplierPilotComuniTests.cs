using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>SU-04: <c>Suppliers:PilotComuni</c> accepts ISTAT codes, validated against the official list.</summary>
public class SupplierPilotComuniTests
{
    [Fact]
    public async Task GetAsync_NothingConfigured_SelfServeIsOff()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var pilots = Create(db);

        Assert.Empty(await pilots.GetAsync());
        Assert.False(await pilots.IsSelfServeEnabledAsync());
    }

    [Fact]
    public async Task GetAsync_ListNotImported_PilotsAreAsConfiguredAndNotValidated()
    {
        await using var db = CreateDb();
        var pilots = Create(db, ("H501", "Roma"));

        var found = await pilots.GetAsync();

        Assert.Equal(new SupplierPilot("H501", "Roma", Validated: false), Assert.Single(found));
        Assert.Equal("H501", (await pilots.FindAsync("h501"))!.Code);
        Assert.Empty(await pilots.GetInvalidConfiguredCodesAsync());
    }

    [Fact]
    public async Task GetAsync_IstatCodeInTheList_TakesTheNameOfTheList()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var pilots = Create(db, (ComuneTestData.Torino, "Name written in the configuration"));

        var found = await pilots.GetAsync();

        Assert.Equal(new SupplierPilot("001272", "Torino", Validated: true), Assert.Single(found));
    }

    [Fact]
    public async Task GetAsync_CadastralCodeInTheConfiguration_IsTurnedIntoTheIstatCode()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var pilots = Create(db, ("H501", "Roma"));

        Assert.Equal("058091", Assert.Single(await pilots.GetAsync()).Code);
        // A registration form that still sends the cadastral code finds the same pilot.
        Assert.Equal("058091", (await pilots.FindAsync("H501"))!.Code);
        Assert.Equal("058091", (await pilots.FindAsync("058091"))!.Code);
        Assert.Null(await pilots.FindAsync("015146"));
    }

    [Fact]
    public async Task GetAsync_CodeNotInTheList_IsNotOfferedAndIsReported()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var pilots = Create(db, ("999999", "Typo"), (ComuneTestData.Como, "Como"));

        var found = await pilots.GetAsync();

        Assert.Equal(["013075"], found.Select(p => p.Code));
        Assert.Equal(["999999"], await pilots.GetInvalidConfiguredCodesAsync());
        Assert.Null(await pilots.FindAsync("999999"));
        Assert.True(await pilots.IsSelfServeEnabledAsync());
    }

    [Fact]
    public async Task IsSelfServeEnabledAsync_EveryPilotInvalid_IsOffNotOpenToAllComuni()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);

        Assert.False(await Create(db, ("999999", "Typo")).IsSelfServeEnabledAsync());
    }

    private static SupplierPilotComuni Create(AppDbContext db, params (string Code, string Name)[] configured) =>
        new(
            Options.Create(new SupplierRegistrationOptions
            {
                PilotComuni = configured.Select(c => new SupplierPilotComune { Code = c.Code, Name = c.Name }).ToList(),
            }),
            new ComuneDirectory(db));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}

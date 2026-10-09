using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03b: the export of the org's fiscal data (<c>GET /api/gdpr/org/export</c>) applies the caller's scope to the sections that
/// belong to a property (<c>propertyFiscalYears</c>, <c>propertyTaxpayers</c>). The endpoint is the holder's, whose scope is the whole
/// org and gets everything as before; a narrower scope (a collaborator "Solo alcuni", an account of before the team that created only
/// some of the properties) gets only the properties it reaches, soft-deleted ones included, and never another org's. The real
/// service over EF InMemory; the same on PostgreSQL in <c>OrgScopeHardeningPostgresTests</c> (CI).
/// </summary>
public class OrgExportScopeTests
{
    private const string Owner = "auth0|owner";
    private const string Anna = "auth0|anna";
    private const string SecondOwner = "auth0|second-owner";
    private const string TaxpayerGranted = "RSSMRA80A01H501U";
    private const string TaxpayerHidden = "VRDLGU75B12F205X";
    private const string TaxpayerSecond = "BNCLRA85C45L219Z";
    private const string TaxpayerDeleted = "NRIMRC70D10A944K";
    private const string TaxpayerForeign = "FRGSTR60E15H501Q";

    private readonly OrgInvitationTestKit _kit = new();

    private sealed record World(Guid OrgId, Guid Granted, Guid Hidden, Guid Second, Guid Deleted, Guid Foreign);

    private GdprService Service(AppDbContext db) => new(
        db,
        Mock.Of<IGuestRepository>(),
        eraser: null!,
        Options.Create(new GdprOptions()),
        _kit.Clock,
        NullLogger<GdprService>.Instance);

    private static Property NewProperty(Guid orgId, string creator, string name, string taxpayer) =>
        new()
        {
            OrgId = orgId,
            OwnerId = creator,
            Name = name,
            Address = $"Via {name} {Guid.NewGuid():N}",
            City = "Ostuni",
            PostalCode = "72017",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            TaxpayerFiscalCode = taxpayer,
        };

    private static PropertyFiscalYear Year(Property property, StrFiscalRegime regime) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        TaxYear = 2026,
        Regime = regime,
        IsPrimaryForCedolare = regime == StrFiscalRegime.CedolareSecca21,
    };

    /// <summary>
    /// An org with four properties, each with a taxpayer and a fiscal year: the first and the soft-deleted fourth are given to the
    /// collaborator Anna, the third was created by a second owner, and another org with its own property.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|foreign-owner");

        await using var db = _kit.NewDb();
        var granted = NewProperty(org.Id, Owner, "Trullo", TaxpayerGranted);
        var hidden = NewProperty(org.Id, Owner, "Casa Bianca", TaxpayerHidden);
        var second = NewProperty(org.Id, SecondOwner, "Masseria", TaxpayerSecond);
        var deleted = NewProperty(org.Id, Owner, "Vecchia Casa", TaxpayerDeleted);
        deleted.IsDeleted = true;
        deleted.DeletedAt = _kit.Now;
        var foreign = NewProperty(other.Id, "auth0|foreign-owner", "Villa Altrove", TaxpayerForeign);
        db.Properties.AddRange(granted, hidden, second, deleted, foreign);
        db.PropertyFiscalYears.AddRange(
            Year(granted, StrFiscalRegime.CedolareSecca21),
            Year(hidden, StrFiscalRegime.CedolareSecca26),
            Year(second, StrFiscalRegime.RegimeOrdinario),
            Year(deleted, StrFiscalRegime.CedolareSecca21),
            Year(foreign, StrFiscalRegime.CedolareSecca21));
        db.PropertyMemberAccesses.AddRange(
            new PropertyMemberAccess { OrgId = org.Id, UserId = Anna, PropertyId = granted.Id },
            new PropertyMemberAccess { OrgId = org.Id, UserId = Anna, PropertyId = deleted.Id });
        await db.SaveChangesAsync();

        return new World(org.Id, granted.Id, hidden.Id, second.Id, deleted.Id, foreign.Id);
    }

    private async Task<JsonElement> ExportAsync(World world, HostScope scope)
    {
        await using var db = _kit.NewDb();
        var export = await Service(db).ExportOrgFiscalDataAsync(world.OrgId, scope);
        return JsonDocument.Parse(JsonSerializer.Serialize(export)).RootElement;
    }

    private static HashSet<Guid> PropertiesOf(JsonElement export, string section) =>
        export.GetProperty(section).EnumerateArray().Select(row => row.GetProperty("PropertyId").GetGuid()).ToHashSet();

    [Fact]
    public async Task Export_TheWholeOrgScopeOfTheHolder_GetsEverythingOfTheOrg_AsBefore()
    {
        var world = await SeedAsync();

        var export = await ExportAsync(world, new HostScope(world.OrgId));

        Assert.True(PropertiesOf(export, "propertyFiscalYears").SetEquals([world.Granted, world.Hidden, world.Second, world.Deleted]));
        Assert.True(PropertiesOf(export, "propertyTaxpayers").SetEquals([world.Granted, world.Hidden, world.Second, world.Deleted]));
        Assert.Equal(
            [TaxpayerSecond, TaxpayerDeleted, TaxpayerGranted, TaxpayerHidden],
            export.GetProperty("propertyTaxpayers").EnumerateArray().Select(r => r.GetProperty("FiscalCode").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(["Active", "Active"], export.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("Status").GetString()));
    }

    [Fact]
    public async Task Export_ACollaboratorSoloAlcuni_GetsOnlyTheSectionsOfThePropertiesItWasGiven()
    {
        var world = await SeedAsync();

        var export = await ExportAsync(world, new HostScope(world.OrgId, GrantedToUserId: Anna));

        // The first property and the soft-deleted one it was given; not the hidden one, not the one of the second owner.
        Assert.True(PropertiesOf(export, "propertyFiscalYears").SetEquals([world.Granted, world.Deleted]));
        Assert.True(PropertiesOf(export, "propertyTaxpayers").SetEquals([world.Granted, world.Deleted]));
        var json = export.GetRawText();
        Assert.DoesNotContain(TaxpayerHidden, json);
        Assert.DoesNotContain(TaxpayerSecond, json);
    }

    [Fact]
    public async Task Export_ACollaboratorGivenNothing_GetsNoPropertySectionAtAll()
    {
        var world = await SeedAsync();

        var export = await ExportAsync(world, new HostScope(world.OrgId, GrantedToUserId: "auth0|given-nothing"));

        Assert.Empty(export.GetProperty("propertyFiscalYears").EnumerateArray());
        Assert.Empty(export.GetProperty("propertyTaxpayers").EnumerateArray());
    }

    [Fact]
    public async Task Export_AnAccountOfBeforeTheTeam_GetsOnlyThePropertiesItCreated()
    {
        var world = await SeedAsync();

        var ofSecond = await ExportAsync(world, new HostScope(world.OrgId, OwnerId: SecondOwner));
        var ofOwner = await ExportAsync(world, new HostScope(world.OrgId, OwnerId: Owner));

        Assert.True(PropertiesOf(ofSecond, "propertyFiscalYears").SetEquals([world.Second]));
        Assert.True(PropertiesOf(ofSecond, "propertyTaxpayers").SetEquals([world.Second]));
        Assert.True(PropertiesOf(ofOwner, "propertyTaxpayers").SetEquals([world.Granted, world.Hidden, world.Deleted]));
        Assert.DoesNotContain(TaxpayerSecond, ofOwner.GetRawText());
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("granted")]
    [InlineData("owned")]
    public async Task Export_NeverHoldsAPropertyOfAnotherOrg_WhateverTheScope(string kind)
    {
        var world = await SeedAsync();
        HostScope scope = kind switch
        {
            "whole" => new HostScope(world.OrgId),
            "granted" => new HostScope(world.OrgId, GrantedToUserId: Anna),
            _ => new HostScope(world.OrgId, OwnerId: Owner),
        };

        var export = await ExportAsync(world, scope);

        Assert.DoesNotContain(world.Foreign, PropertiesOf(export, "propertyFiscalYears"));
        Assert.DoesNotContain(world.Foreign, PropertiesOf(export, "propertyTaxpayers"));
        Assert.DoesNotContain(TaxpayerForeign, export.GetRawText());
        Assert.DoesNotContain("auth0|foreign-owner", export.GetRawText());
    }

    [Fact]
    public async Task Export_TheScopeOfAnotherOrg_IsRefused()
    {
        var world = await SeedAsync();
        await using var db = _kit.NewDb();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(db).ExportOrgFiscalDataAsync(world.OrgId, new HostScope(Guid.NewGuid())));
    }

    [Fact]
    public async Task Export_AScopeNarrowerThanTheOrg_KeepsTheShapeOfTheAnswer()
    {
        var world = await SeedAsync();

        var whole = await ExportAsync(world, new HostScope(world.OrgId));
        var narrow = await ExportAsync(world, new HostScope(world.OrgId, GrantedToUserId: Anna));

        Assert.Equal(
            whole.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            narrow.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}

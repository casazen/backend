using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E29/F02/US01/T01 (NF-125, decision D3): the one resolver of the regime of a property, on the official directory of the build. A Lugano property is under
/// the Ticino regime, one whose municipality is not known (or is left open) under the unknown Swiss place, an Italian one under Italy; the duty in effect
/// is the host's setting or the default of the regime, and the default is <c>Ask</c> for every canton not verified as covering private rentals; the host's
/// setting is not read while the flag is off; another organization's property is not found.
/// </summary>
public class GuestRegistrationRegimeResolverTests(GuestRegistrationRegimeResolverTests.SeededPlaces places) : IClassFixture<GuestRegistrationRegimeResolverTests.SeededPlaces>
{
    private static readonly Guid OrgA = Guid.Parse("a1111111-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("b2222222-0000-0000-0000-00000000000b");

    /// <summary>One in-memory database with the seed of the build, loaded once for the class; each test adds its own properties.</summary>
    public sealed class SeededPlaces : IAsyncLifetime
    {
        public string DatabaseName { get; } = "guest-registration-resolver-" + Guid.NewGuid().ToString("N");

        public AppDbContext NewContext(ITenantContext? tenant = null) =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(DatabaseName).Options, tenant);

        public async Task InitializeAsync()
        {
            await using var db = NewContext();
            await SwissMunicipalitySeed.ApplyAsync(db, SwissMunicipalitySeed.ReadEmbedded()!, TimeProvider.System, CancellationToken.None);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Tenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>A Lugano property (6900) is in Ticino and under its regime (acceptance criterion 2), with a structure number nothing is missing.</summary>
    [Fact]
    public async Task Resolve_ALuganoProperty_IsTheTicinoRegime()
    {
        await using var db = places.NewContext();

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(Swiss("6900", "Lugano", structureId: "NL-12345678"));

        Assert.Equal(GuestRegistrationRegimeCatalog.TicinoCode, resolution.Regime!.Code);
        Assert.True(resolution.Place!.IsInTicino);
        Assert.Equal(GuestRegistrationEffectiveDuty.Required, resolution.EffectiveDuty);
        Assert.Null(resolution.Override);
        Assert.Empty(resolution.IdentifierMissing);
    }

    [Fact]
    public async Task Resolve_ATicinoPropertyWithoutAStructureNumber_SaysWhatIsMissing()
    {
        await using var db = places.NewContext();

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(Swiss("6900", "Lugano"));

        Assert.Equal("CH-TI", resolution.Regime!.Code);
        Assert.Equal([GuestRegistrationReadiness.TicinoStructureIdMissing], resolution.IdentifierMissing);
        Assert.Equal("ticino_structure_id_missing", GuestRegistrationReadiness.TicinoStructureIdMissing);
    }

    /// <summary>
    /// A Swiss property whose municipality is not known (none, or a postal code the directory does not have) is the unknown Swiss place (acceptance
    /// criterion 2): no canton, nothing raised, and the municipality is what is missing.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("0000", "Nowhere")]
    [InlineData("20822", "Seveso")]
    public async Task Resolve_ASwissPropertyWithoutAKnownMunicipality_IsTheUnknownSwissPlace(string postalCode, string city)
    {
        await using var db = places.NewContext();

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(Swiss(postalCode, city));

        Assert.Equal(GuestRegistrationRegimeCatalog.UnknownSwissCode, resolution.Regime!.Code);
        Assert.Null(resolution.Place);
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, resolution.EffectiveDuty);
        Assert.Equal(["swiss_municipality_missing"], resolution.IdentifierMissing);
    }

    /// <summary>A postal code shared by several municipalities, with a city that names none, is ambiguous: the resolver never picks a canton.</summary>
    [Theory]
    [InlineData("6900", "Ticino")]
    [InlineData("6600", "Lago Maggiore")]
    [InlineData("6533", "Lumino")]
    public async Task Resolve_AnAmbiguousPlace_IsTheUnknownSwissPlace_NeverACantonChosenSilently(string postalCode, string city)
    {
        await using var db = places.NewContext();

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(Swiss(postalCode, city));

        Assert.True(resolution.Place!.Ambiguous);
        Assert.Equal(GuestRegistrationRegimeCatalog.UnknownSwissCode, resolution.Regime!.Code);
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, resolution.EffectiveDuty);
        Assert.Equal(["swiss_municipality_missing"], resolution.IdentifierMissing);
    }

    [Theory]
    [InlineData("8001", "Zürich", "CH-ZH")]
    [InlineData("3011", "Bern", "CH-BE")]
    [InlineData("1003", "Lausanne", "CH-VD")]
    [InlineData("4001", "Basel", "CH-BS")]
    [InlineData("9000", "St. Gallen", "CH-SG")]
    [InlineData("6300", "Zug", "CH-ZG")]
    [InlineData("8200", "Schaffhausen", "CH-SH")]
    public async Task Resolve_AnotherCanton_IsItsOwnRegime(string postalCode, string city, string expected)
    {
        await using var db = places.NewContext();

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(Swiss(postalCode, city));

        Assert.Equal(expected, resolution.Regime!.Code);
        Assert.Empty(resolution.IdentifierMissing);
    }

    /// <summary>An Italian property (any case, or an old row with no country) is under Italy: no place, no setting of the host, nothing missing here.</summary>
    [Theory]
    [InlineData("IT")]
    [InlineData(" it ")]
    [InlineData("")]
    public async Task Resolve_AnItalianProperty_IsTheItalianRegime_AndNeverReadsTheSetting(string country)
    {
        await using var db = places.NewContext();
        var italian = Swiss("20822", "Seveso");
        italian.Country = country;
        italian.GuestNotificationOverride = GuestNotificationOverride.NotRequired;

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(italian);

        Assert.Same(GuestRegistrationRegimeCatalog.Italian, resolution.Regime);
        Assert.Null(resolution.Place);
        Assert.Null(resolution.Override);
        Assert.Equal(GuestRegistrationEffectiveDuty.Required, resolution.EffectiveDuty);
        Assert.Empty(resolution.IdentifierMissing);
    }

    [Fact]
    public async Task Resolve_ACountryThatIsNeitherItalyNorSwitzerland_HasNoRegime()
    {
        await using var db = places.NewContext();
        var french = Swiss("75001", "Paris");
        french.Country = "FR";

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(french);

        Assert.Null(resolution.Regime);
        Assert.Null(resolution.Place);
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, resolution.EffectiveDuty);
        Assert.Empty(resolution.IdentifierMissing);
    }

    /// <summary>
    /// Acceptance criterion 3, the effective-duty table: the setting of the host wins; without it Ticino and a verified obligation to report that covers
    /// private rentals (Zug, Schaffhausen) are required; every other canton, and the place nobody knows, is left to the host.
    /// </summary>
    [Theory]
    [InlineData("6900", "Lugano", null, GuestRegistrationEffectiveDuty.Required)]
    [InlineData("6900", "Lugano", GuestNotificationOverride.NotRequired, GuestRegistrationEffectiveDuty.NotRequired)]
    [InlineData("6900", "Lugano", GuestNotificationOverride.Required, GuestRegistrationEffectiveDuty.Required)]
    [InlineData("6300", "Zug", null, GuestRegistrationEffectiveDuty.Required)]
    [InlineData("8200", "Schaffhausen", null, GuestRegistrationEffectiveDuty.Required)]
    [InlineData("8001", "Zürich", null, GuestRegistrationEffectiveDuty.Ask)]
    [InlineData("8001", "Zürich", GuestNotificationOverride.Required, GuestRegistrationEffectiveDuty.Required)]
    [InlineData("8001", "Zürich", GuestNotificationOverride.NotRequired, GuestRegistrationEffectiveDuty.NotRequired)]
    [InlineData("1003", "Lausanne", null, GuestRegistrationEffectiveDuty.Ask)]
    [InlineData("3011", "Bern", null, GuestRegistrationEffectiveDuty.Ask)]
    [InlineData("", "", null, GuestRegistrationEffectiveDuty.Ask)]
    [InlineData("", "", GuestNotificationOverride.Required, GuestRegistrationEffectiveDuty.Required)]
    public async Task EffectiveDuty_IsTheSettingOfTheHost_OrTheDefaultOfTheRegime(
        string postalCode, string city, GuestNotificationOverride? hostChoice, GuestRegistrationEffectiveDuty expected)
    {
        await using var db = places.NewContext();
        var property = Swiss(postalCode, city, structureId: "NL-12345678");
        property.GuestNotificationOverride = hostChoice;

        var resolution = await Resolver(db, flagOn: true).ResolveAsync(property);

        Assert.Equal(expected, resolution.EffectiveDuty);
        Assert.Equal(hostChoice, resolution.Override);
    }

    /// <summary>
    /// Proof (data): no false legal alarm. The duty is raised by default only for Ticino and for a regime that is verified as an obligation to report that
    /// covers private rentals; a regime that is partial, not verified, a register or a faculty, or that does not say it covers private rentals, is
    /// <c>Ask</c>: the host decides, nothing is raised in red. Downgrading one of the three raised ones takes its duty away by itself.
    /// </summary>
    [Fact]
    public void EffectiveDuty_UncertainCanton_IsAskNeverRequired_Test()
    {
        var raised = new List<string>();
        foreach (var regime in GuestRegistrationRegimeCatalog.All)
        {
            var duty = GuestRegistrationDutyRules.DefaultDuty(regime);
            if (duty == GuestRegistrationEffectiveDuty.Required)
                raised.Add(regime.Code);
            else
                Assert.Equal(GuestRegistrationEffectiveDuty.Ask, duty);

            if (regime.Code != GuestRegistrationRegimeCatalog.TicinoCode
                && (regime.Certainty != GuestRegistrationCertainty.Verified
                    || regime.Obligation != GuestRegistrationObligation.ReportToAuthority
                    || regime.PrivateRentals != GuestRegistrationPrivateRentals.Yes))
            {
                Assert.True(duty == GuestRegistrationEffectiveDuty.Ask, $"{regime.Code} is not verified as a duty to report on private rentals: its duty is Ask, not {duty}");
            }
        }

        Assert.Equal(["CH-SH", "CH-TI", "CH-ZG", "IT"], raised.Order(StringComparer.Ordinal));

        var schaffhausen = GuestRegistrationRegimeCatalog.ForCanton("SH");
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, GuestRegistrationDutyRules.DefaultDuty(schaffhausen with { Certainty = GuestRegistrationCertainty.Partial }));
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, GuestRegistrationDutyRules.DefaultDuty(schaffhausen with { Certainty = GuestRegistrationCertainty.Unverified }));
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, GuestRegistrationDutyRules.DefaultDuty(schaffhausen with { PrivateRentals = GuestRegistrationPrivateRentals.Partial }));
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, GuestRegistrationDutyRules.DefaultDuty(schaffhausen with { Obligation = GuestRegistrationObligation.KeepRegister }));
        var zurich = GuestRegistrationRegimeCatalog.ForCanton("ZH");
        Assert.Equal(GuestRegistrationEffectiveDuty.Ask, GuestRegistrationDutyRules.DefaultDuty(zurich with { PrivateRentals = GuestRegistrationPrivateRentals.Yes }));
    }

    /// <summary>The flag is off: nothing reads the setting of the host, so the duty is the regime's own and Ticino behaves as before.</summary>
    [Fact]
    public async Task Resolve_WithTheFlagOff_NeverReadsTheSettingOfTheHost_Test()
    {
        await using var db = places.NewContext();
        var zurich = Swiss("8001", "Zürich");
        zurich.GuestNotificationOverride = GuestNotificationOverride.Required;
        var lugano = Swiss("6900", "Lugano", structureId: "NL-12345678");
        lugano.GuestNotificationOverride = GuestNotificationOverride.NotRequired;

        var off = Resolver(db, flagOn: false);
        var zurichOff = await off.ResolveAsync(zurich);
        var luganoOff = await off.ResolveAsync(lugano);
        var on = Resolver(db, flagOn: true);
        var zurichOn = await on.ResolveAsync(zurich);
        var luganoOn = await on.ResolveAsync(lugano);

        Assert.Equal(((GuestNotificationOverride?)null, GuestRegistrationEffectiveDuty.Ask), (zurichOff.Override, zurichOff.EffectiveDuty));
        Assert.Equal(((GuestNotificationOverride?)null, GuestRegistrationEffectiveDuty.Required), (luganoOff.Override, luganoOff.EffectiveDuty));
        Assert.Equal(((GuestNotificationOverride?)GuestNotificationOverride.Required, GuestRegistrationEffectiveDuty.Required), (zurichOn.Override, zurichOn.EffectiveDuty));
        Assert.Equal(((GuestNotificationOverride?)GuestNotificationOverride.NotRequired, GuestRegistrationEffectiveDuty.NotRequired), (luganoOn.Override, luganoOn.EffectiveDuty));
    }

    /// <summary>Proof (permission): another organization's property is not found by id, whatever it holds; the own one is.</summary>
    [Fact]
    public async Task Resolve_APropertyOfAnotherOrganization_IsNotFound_Test()
    {
        var property = Swiss("6900", "Lugano", structureId: "NL-12345678");
        property.OrgId = OrgA;
        await using (var seed = places.NewContext())
        {
            seed.Properties.Add(property);
            await seed.SaveChangesAsync();
        }

        await using var asOrgB = places.NewContext(new Tenant(OrgB));
        await using var asOrgA = places.NewContext(new Tenant(OrgA));

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => Resolver(asOrgB, flagOn: true).ResolveAsync(property.Id));
        Assert.Equal("property_not_found", refused.Code);
        var found = await Resolver(asOrgA, flagOn: true).ResolveAsync(property.Id);
        Assert.Equal((property.Id, "CH-TI"), (found.PropertyId, found.Regime!.Code));
        await Assert.ThrowsAsync<NotFoundException>(() => Resolver(asOrgA, flagOn: true).ResolveAsync(Guid.NewGuid()));
    }

    private static GuestRegistrationRegimeResolver Resolver(AppDbContext db, bool flagOn)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.GuestRegistrationRegimes)).Returns(flagOn);
        return new GuestRegistrationRegimeResolver(db, new PropertySwissPlaceResolver(db), flags.Object);
    }

    private static Property Swiss(string postalCode, string city, string? structureId = null)
    {
        var property = HostScopeScenario.NewProperty(OrgA, "auth0|owner", $"Casa {Guid.NewGuid():N}");
        property.Country = "CH";
        property.PostalCode = postalCode;
        property.City = city;
        property.TicinoStructureId = structureId;
        return property;
    }
}

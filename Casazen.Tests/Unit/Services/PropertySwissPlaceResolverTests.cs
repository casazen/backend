using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E29/F01/US01/T02 (NF-124, decisions D3 and D4): the one resolver of the place of a Swiss property, on the official directory of the build. The
/// stored municipality wins; without it the postal code and the city give the municipality, and several left open is ambiguous, never a silent
/// choice; reading never writes; the Ticino police notification and the OTR report read their canton from it.
/// </summary>
public class PropertySwissPlaceResolverTests(PropertySwissPlaceResolverTests.SeededPlaces places) : IClassFixture<PropertySwissPlaceResolverTests.SeededPlaces>
{
    private static readonly DateTime Day = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly ArrivalDay = DateOnly.FromDateTime(Day);
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero));

    /// <summary>One in-memory database with the seed of the build, loaded once for the class; each test adds its own org and properties.</summary>
    public sealed class SeededPlaces : IAsyncLifetime
    {
        public string DatabaseName { get; } = "swiss-place-resolver-" + Guid.NewGuid().ToString("N");

        public AppDbContext NewContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(DatabaseName).Options);

        public async Task InitializeAsync()
        {
            await using var db = NewContext();
            await SwissMunicipalitySeed.ApplyAsync(db, SwissMunicipalitySeed.ReadEmbedded()!, TimeProvider.System, CancellationToken.None);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Resolve_AStoredMunicipality_WinsOverThePostalCode()
    {
        await using var db = places.NewContext();

        var place = await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss("6900", "Lugano", stored: "5113"));

        Assert.Equal(new SwissPlace("5113", "Locarno", "TI", SwissPlaceSource.Stored, Ambiguous: false), place);
    }

    /// <summary>A property in Lugano with the postal code 6900 and no stored municipality is in Lugano, canton Ticino (acceptance criterion 1).</summary>
    [Fact]
    public async Task Resolve_Lugano6900WithoutAStoredMunicipality_IsLuganoInTicino()
    {
        await using var db = places.NewContext();

        var place = await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss("6900", "Lugano"));

        Assert.Equal(new SwissPlace("5192", "Lugano", "TI", SwissPlaceSource.Derived, Ambiguous: false), place);
        Assert.True(place!.IsInTicino);
    }

    [Theory]
    [InlineData("6512", "Giubiasco", "5002")]
    [InlineData("6512", "", "5002")]
    [InlineData("6600", "Locarno", "5113")]
    [InlineData("6600", "Muralto", "5120")]
    [InlineData("6600", "Solduno", "5113")]
    [InlineData("6612", "Ascona", "5091")]
    [InlineData("8044", "Gockhausen", "191")]
    public async Task Resolve_ThePostalCodeAndTheCity_GiveTheMunicipality(string postalCode, string city, string municipality)
    {
        await using var db = places.NewContext();

        var place = await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss(postalCode, city));

        Assert.NotNull(place);
        Assert.Equal((municipality, SwissPlaceSource.Derived, false), (place.MunicipalityCode, place.Source, place.Ambiguous));
    }

    /// <summary>
    /// Data: a postal code shared by several municipalities, with a city that names none of them (or no city), is ambiguous and gives no
    /// municipality and no canton: a wrong canton would apply the wrong legal regime, so the resolver never chooses (acceptance criterion 2).
    /// </summary>
    [Theory]
    [InlineData("6900", "Ticino")]
    [InlineData("6900", "")]
    [InlineData("6600", "Lago Maggiore")]
    public async Task Resolver_AmbiguousPostalCode_NeverChoosesSilently_Test(string postalCode, string city)
    {
        await using var db = places.NewContext();

        var place = await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss(postalCode, city));

        Assert.NotNull(place);
        Assert.True(place.Ambiguous);
        Assert.Null(place.MunicipalityCode);
        Assert.Null(place.MunicipalityName);
        Assert.Null(place.CantonCode);
        Assert.False(place.IsKnown);
        Assert.False(place.IsInTicino);
    }

    [Fact]
    public async Task Resolve_AnItalianProperty_HasNoPlace_EvenWithASwissNumberOrPostalCode()
    {
        await using var db = places.NewContext();
        var italian = Swiss("6900", "Lugano", stored: "5192");
        italian.Country = "IT";

        Assert.Null(await new PropertySwissPlaceResolver(db).ResolveAsync(italian));
    }

    [Theory]
    [InlineData("69A0")]
    [InlineData("22100")]
    [InlineData("690")]
    [InlineData("0690")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("9999")]
    public async Task Resolve_AMalformedOrUnknownPostalCode_HasNoPlace(string? postalCode)
    {
        await using var db = places.NewContext();

        Assert.Null(await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss(postalCode, "Lugano")));
    }

    [Theory]
    [InlineData("CH-6900", "Lugano")]
    [InlineData(" 6 900 ", "Lugano")]
    [InlineData("690000", "Lugano")]
    [InlineData("6900", "  LUGANO ")]
    [InlineData("8044", "zurich")]
    [InlineData("8044", "ZÜRICH")]
    public async Task Resolve_IgnoresThePrefixAndTheSpacesOfThePostalCode_AndTheCaseAndAccentsOfTheCity(string postalCode, string city)
    {
        await using var db = places.NewContext();

        var place = await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss(postalCode, city));

        Assert.NotNull(place);
        Assert.True(place.IsKnown);
        Assert.Equal(postalCode.StartsWith("8044", StringComparison.Ordinal) ? "261" : "5192", place.MunicipalityCode);
    }

    [Fact]
    public async Task Resolve_AStoredNumberTheDirectoryDoesNotHave_IsDerivedAgain()
    {
        await using var db = places.NewContext();

        var place = await new PropertySwissPlaceResolver(db).ResolveAsync(Swiss("6900", "Lugano", stored: "9999"));

        Assert.Equal(new SwissPlace("5192", "Lugano", "TI", SwissPlaceSource.Derived, Ambiguous: false), place);
    }

    /// <summary>Data: resolving reads only; a derived municipality is never stored behind the host's back.</summary>
    [Fact]
    public async Task Resolver_ReadDoesNotWrite_Test()
    {
        var property = Swiss("6900", "Lugano");
        await using (var setup = places.NewContext())
        {
            setup.Properties.Add(property);
            await setup.SaveChangesAsync();
        }

        await using var db = places.NewContext();
        var tracked = await db.Properties.SingleAsync(p => p.Id == property.Id);
        var resolver = new PropertySwissPlaceResolver(db);
        var one = await resolver.ResolveAsync(tracked);
        var many = await resolver.ResolveManyAsync([SwissPlaceLookup.From(tracked)]);

        Assert.Equal(SwissPlaceSource.Derived, one!.Source);
        Assert.Equal(one, many[tracked.Id]);
        Assert.Null(tracked.SwissMunicipalityCode);
        Assert.False(db.ChangeTracker.HasChanges());
        await using var reread = places.NewContext();
        Assert.Null((await reread.Properties.AsNoTracking().SingleAsync(p => p.Id == property.Id)).SwissMunicipalityCode);
    }

    [Fact]
    public async Task ResolveMany_AnswersEveryPropertyThatHasAPlace_AndLeavesOutTheOthers()
    {
        await using var db = places.NewContext();
        var stored = Swiss("6900", "Lugano", stored: "5002");
        var derived = Swiss("6600", "Locarno");
        var ambiguous = Swiss("6900", "Ticino");
        var italian = Swiss("6900", "Lugano");
        italian.Country = "IT";
        var malformed = Swiss("ABCD", "Lugano");

        var answer = await new PropertySwissPlaceResolver(db).ResolveManyAsync(
            [.. new[] { stored, derived, ambiguous, italian, malformed }.Select(SwissPlaceLookup.From)]);

        Assert.Equal(3, answer.Count);
        Assert.Equal(("5002", SwissPlaceSource.Stored), (answer[stored.Id].MunicipalityCode, answer[stored.Id].Source));
        Assert.Equal(("5113", SwissPlaceSource.Derived), (answer[derived.Id].MunicipalityCode, answer[derived.Id].Source));
        Assert.True(answer[ambiguous.Id].Ambiguous);
        Assert.Empty(await new PropertySwissPlaceResolver(db).ResolveManyAsync([]));
    }

    [Fact]
    public async Task RequireMunicipality_TheNumberOfTheDirectory_OrA422()
    {
        await using var db = places.NewContext();
        var resolver = new PropertySwissPlaceResolver(db);

        Assert.Equal(new SwissPlace("5192", "Lugano", "TI", SwissPlaceSource.Stored, Ambiguous: false), await resolver.RequireMunicipalityAsync("05192"));
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() => resolver.RequireMunicipalityAsync("9999"));
        Assert.Equal(SwissPlaceErrorCodes.Unknown, unknown.Code);

        await using var empty = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var unavailable = await Assert.ThrowsAsync<DomainRuleException>(() => new PropertySwissPlaceResolver(empty).RequireMunicipalityAsync("5192"));
        Assert.Equal(SwissPlaceErrorCodes.Unavailable, unavailable.Code);
    }

    /// <summary>
    /// A property with only the postal code 6900 and the city «Lugano» is in Ticino for the police notification: the endpoints no longer answer
    /// <c>property_not_in_ticino</c>, its arrival day is in the days to notify, and the OTR report is made.
    /// </summary>
    [Fact]
    public async Task TicinoNotificationAndOtrReport_AProperty6900LuganoWithoutMunicipality_IsInTicino()
    {
        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = "test@example.com" };
        var property = Swiss("6900", "Lugano");
        property.OrgId = org.Id;
        await using (var setup = places.NewContext())
        {
            setup.Orgs.Add(org);
            setup.Properties.Add(property);
            var guest = new Guest { OrgId = org.Id, FirstName = "TEST", LastName = "Guest" };
            setup.Guests.Add(guest);
            setup.Bookings.Add(new Booking
            {
                OrgId = org.Id,
                PropertyId = property.Id,
                GuestId = guest.Id,
                Status = BookingStatus.Confirmed,
                CheckInDate = Day,
                CheckOutDate = Day.AddDays(2),
                NumberOfGuests = 1,
            });
            await setup.SaveChangesAsync();
        }

        await using var db = places.NewContext();
        var police = new TicinoNotificationService(db, Clock);

        Assert.True(await police.IsPropertyInTicinoAsync(property.Id));
        Assert.Equal(1, (await police.GetDayAsync(property.Id, ArrivalDay)).StayCount);
        var pending = Assert.Single(await police.ListPendingDaysAsync(org.Id, null, ArrivalDay));
        Assert.Equal((property.Id, ArrivalDay), (pending.PropertyId, pending.Day));
        var report = await new OtrTaxService(db).GeneratePeriodReportAsync(property.Id, Day, Day.AddDays(2));
        Assert.Contains(property.Id.ToString(), report.CsvContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCasazenServices_RegistersTheResolverAndTheDirectory_Scoped()
    {
        var services = new ServiceCollection();

        services.AddCasazenServices();

        foreach (var (service, implementation) in new (Type, Type)[]
                 {
                     (typeof(IPropertySwissPlaceResolver), typeof(PropertySwissPlaceResolver)),
                     (typeof(ISwissPlacesDirectory), typeof(SwissPlacesDirectory)),
                 })
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, descriptor.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }

    private static Property Swiss(string? postalCode, string city, string? stored = null) => new()
    {
        OrgId = Guid.NewGuid(),
        Name = "TEST",
        Country = "CH",
        Currency = "CHF",
        Timezone = "Europe/Zurich",
        PostalCode = postalCode!,
        City = city,
        SwissMunicipalityCode = stored,
    };
}

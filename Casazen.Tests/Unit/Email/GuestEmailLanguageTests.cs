using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Email;

public sealed class GuestEmailLanguageTests : IDisposable
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("fr", "DE", "es", "fr")]
    [InlineData(" DE ", null, "it", "de")]
    [InlineData(null, "DE", "es", "de")]
    [InlineData(null, "Francia", "en", "fr")]
    [InlineData(null, "NL", "it", "it")]
    [InlineData(null, "NL", "de", "de")]
    [InlineData(null, "CH", "it", "it")]
    [InlineData(null, "CHE", "fr", "fr")]
    [InlineData(null, "Svizzera", "de-CH", "de")]
    [InlineData(null, "Switzerland", null, "it")]
    [InlineData("de", "CH", "it", "de")]
    [InlineData(null, "US", "it", "en")]
    [InlineData(null, "GBR", "de", "en")]
    [InlineData(null, "Ireland", "es", "en")]
    [InlineData(null, "UK", "fr", "en")]
    [InlineData(null, null, "es", "es")]
    [InlineData(null, "", "en", "en")]
    [InlineData(null, null, "it", "it")]
    [InlineData(null, null, null, "it")]
    [InlineData("xx", null, "xx", "it")]
    [InlineData("xx", "IT", "en", "it")]
    [InlineData("pt", null, "de-CH", "de")]
    public void ResolveForEmail_SessionThenUnambiguousCountryThenOrgDefaultThenItalian(string? session, string? country, string? orgDefault, string expected)
    {
        Assert.Equal(expected, GuestLanguages.ResolveForEmail(session, country, orgDefault));
    }

    [Theory]
    [InlineData("IT", "it")]
    [InlineData("ita", "it")]
    [InlineData("Italia", "it")]
    [InlineData("DE", "de")]
    [InlineData("AT", "de")]
    [InlineData("Österreich", "de")]
    [InlineData("FR", "fr")]
    [InlineData("France", "fr")]
    [InlineData("ES", "es")]
    [InlineData("España", "es")]
    [InlineData("GB", "en")]
    [InlineData("Regno Unito", "en")]
    [InlineData("US", "en")]
    [InlineData("Stati Uniti", "en")]
    [InlineData("IE", "en")]
    [InlineData("Irlanda", "en")]
    [InlineData("CH", null)]
    [InlineData("Svizzera", null)]
    [InlineData("NL", null)]
    [InlineData("PT", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void OfUnambiguousCountry_OnlyACountryWithOneLanguageGivesOne(string? country, string? expected)
    {
        Assert.Equal(expected, GuestLanguages.OfUnambiguousCountry(country));
    }

    [Theory]
    [InlineData("IT", "it")]
    [InlineData("AT", "de")]
    [InlineData("Francia", "fr")]
    [InlineData("ES", "es")]
    [InlineData("US", "en")]
    [InlineData("CH", "en")]
    [InlineData("NL", "en")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void OfCountry_IsWhatTheEmailsOfTodayAlwaysRead(string? country, string? expected)
    {
        Assert.Equal(expected, GuestLanguages.OfCountry(country));
    }

    /// <summary>
    /// PROOF TEST (what language a guest is written to in, and that the portal switch changes nothing while it is off): with the portal on the language comes from the check-in page,
    /// then the country on the booking, then the host's account, then Italian; an unsupported code is skipped; with the portal off the culture the caller has always used comes
    /// back untouched, whatever the booking says.
    /// </summary>
    [Fact]
    public async Task Language_FromSessionThenOrgDefaultThenItalian_FlagOff_Unchanged()
    {
        var flags = new TestFeatureFlags(FeatureFlags.GuestPortal);
        var provider = Provider(flags);

        var withSession = await SeedAsync(orgDefault: "en", country: "FR", sessionLanguage: "de");
        Assert.Equal("de", (await provider.ForBookingAsync(withSession.OrgId, withSession.BookingId, Italian)).Name);

        Assert.Equal("es", (await provider.ForBookingAsync(withSession.OrgId, withSession.BookingId, Italian, chosenLanguage: "es")).Name);

        var withCountry = await SeedAsync(orgDefault: "en", country: "FR", sessionLanguage: null);
        Assert.Equal("fr", (await provider.ForBookingAsync(withCountry.OrgId, withCountry.BookingId, Italian)).Name);
        var american = await SeedAsync(orgDefault: "it", country: "US", sessionLanguage: null);
        Assert.Equal("en", (await provider.ForBookingAsync(american.OrgId, american.BookingId, Italian)).Name);

        var swissGerman = await SeedAsync(orgDefault: "de", country: "CH", sessionLanguage: null);
        Assert.Equal("de", (await provider.ForBookingAsync(swissGerman.OrgId, swissGerman.BookingId, English)).Name);
        var swissItalian = await SeedAsync(orgDefault: "it", country: "Svizzera", sessionLanguage: null);
        Assert.Equal("it-IT", (await provider.ForBookingAsync(swissItalian.OrgId, swissItalian.BookingId, English)).Name);

        var orgDefault = await SeedAsync(orgDefault: "en", country: "", sessionLanguage: null);
        Assert.Equal("en", (await provider.ForBookingAsync(orgDefault.OrgId, orgDefault.BookingId, Italian)).Name);

        var italian = await SeedAsync(orgDefault: "it", country: "", sessionLanguage: null);
        Assert.Equal("it-IT", (await provider.ForBookingAsync(italian.OrgId, italian.BookingId, English)).Name);

        var unsupported = await SeedAsync(orgDefault: "xx", country: "", sessionLanguage: "xx");
        Assert.Equal("it-IT", (await provider.ForBookingAsync(unsupported.OrgId, unsupported.BookingId, English)).Name);

        var off = Provider(new TestFeatureFlags());
        Assert.Same(English, await off.ForBookingAsync(withSession.OrgId, withSession.BookingId, English));
        Assert.Same(Italian, await off.ForBookingAsync(withCountry.OrgId, withCountry.BookingId, Italian));

        IGuestEmailLanguageProvider? none = null;
        Assert.Same(English, await none.CultureOrAsync(withSession.OrgId, withSession.BookingId, English));
    }

    [Fact]
    public async Task Language_OfABookingOfAnotherOrg_IsNotRead()
    {
        var provider = Provider(new TestFeatureFlags(FeatureFlags.GuestPortal));
        var other = await SeedAsync(orgDefault: "en", country: "DE", sessionLanguage: "fr");
        var mine = await SeedAsync(orgDefault: "es", country: "", sessionLanguage: null);

        var culture = await provider.ForBookingAsync(mine.OrgId, other.BookingId, English);

        Assert.Equal("it-IT", culture.Name);
    }

    private GuestEmailLanguageProvider Provider(TestFeatureFlags flags) =>
        new(_db, flags, NullLogger<GuestEmailLanguageProvider>.Instance);

    private async Task<(Guid OrgId, Guid BookingId)> SeedAsync(string orgDefault, string country, string? sessionLanguage)
    {
        var org = HostScopeScenario.NewOrg("Casa Aurora", $"aurora-{Guid.NewGuid():N}"[..20]);
        org.DefaultLanguage = orgDefault;
        var property = HostScopeScenario.NewProperty(org.Id, "auth0|titolare-aurora", "Trullo Aurora");
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com", Country = country };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 10),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 13),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        _db.AddRange(org, property, guest, booking);
        if (sessionLanguage is not null)
        {
            _db.GuestCheckInSessions.Add(new GuestCheckInSession
            {
                OrgId = org.Id,
                BookingId = booking.Id,
                TokenHash = new string('a', 64),
                ExpiresAt = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
                Language = sessionLanguage,
            });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (org.Id, booking.Id);
    }
}
